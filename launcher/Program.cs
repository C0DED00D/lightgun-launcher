using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LightgunLauncher;

static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        using var single = new Mutex(true, "LightgunLauncher.Single", out bool first);
        if (!first) return;

        ApplicationConfiguration.Initialize();
        // Errors on the UI thread are logged and the menu carries on (no hidden error box on a TV).
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => GameRunner.Log("UI error (carrying on): " + e.Exception);
        TaskScheduler.UnobservedTaskException += (_, e) => { GameRunner.Log("background error: " + e.Exception); e.SetObserved(); };
        // Never leave the pointer invisible if the launcher crashes mid-game.
        AppDomain.CurrentDomain.UnhandledException += (_, e) => { GameRunner.Log("CRASH: " + e.ExceptionObject); Native.RestoreSystemCursors(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Native.RestoreSystemCursors();

        string root = FindRoot();
        var cfgPath = Path.Combine(root, "config", "games.json");
        if (!File.Exists(cfgPath))
        {
            MessageBox.Show("config\\games.json not found.\n\nCopy config\\games.example.json to config\\games.json and add your games " +
                            "(see docs\\configuration.md).", "Lightgun Launcher");
            return;
        }
        LauncherConfig cfg;
        try { cfg = ConfigStore.Load(cfgPath); }
        catch (Exception ex) { MessageBox.Show("Could not read config\\games.json:\n" + ex.Message, "Lightgun Launcher"); return; }

        // "--no-sinden": don't start the Sinden software (aim tests drive the guns' serial ports themselves).
        if (args.Any(a => a.Equals("--no-sinden", StringComparison.OrdinalIgnoreCase))) cfg.Sinden = null;
        var ctx = new LauncherContext(root, cfg);
        // "--run <id>" starts a game straight away (handy for testing or other front-ends).
        int i = Array.FindIndex(args, a => a.Equals("--run", StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && i + 1 < args.Length) ctx.LaunchById(args[i + 1]);
        // "--menu-run <id>[,<id>...]": like a player shooting the tile: the menu runs for 10 s first, then the tile's
        // own launch path; the next id starts 10 s after the previous game ends.
        int m = Array.FindIndex(args, a => a.Equals("--menu-run", StringComparison.OrdinalIgnoreCase));
        if (m >= 0 && m + 1 < args.Length)
        {
            var id = args[m + 1];
            var t = new System.Windows.Forms.Timer { Interval = 10000 };
            t.Tick += (_, _) => { t.Stop(); ctx.LaunchFromMenu(id); };
            t.Start();
        }
        Application.Run(ctx);
    }

    /// <summary>The launcher root is the folder holding config\games.json (the exe can live in a subfolder).</summary>
    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "config", "games.json")) ||
                File.Exists(Path.Combine(dir.FullName, "config", "games.example.json"))) return dir.FullName;
            dir = dir.Parent;
        }
        return AppContext.BaseDirectory;
    }
}

/// <summary>Owns the menu, the overlays, the Sinden driver, and the running game.</summary>
class LauncherContext : ApplicationContext
{
    readonly string _root;
    readonly LauncherConfig _cfg;
    readonly MenuForm _menu;
    readonly BorderForm _border;
    readonly QuitForm _quit;
    readonly BackdropForm _backdrop = new();
    readonly System.Windows.Forms.Timer _tick = new() { Interval = 30 };
    readonly Native.LowLevelKeyboardProc _hookProc; // kept alive for the hook
    readonly IntPtr _hook;
    readonly int _quitVk;
    readonly string? _quitProgressFile;
    Process? _sinden;
    bool _startedSinden, _sindenFresh, _restartingSinden;
    readonly string _settingsPath;
    readonly UserSettings _settings;
    GameRunner? _runner;
    DateTime? _quitHeldSince;

    public LauncherContext(string root, LauncherConfig cfg)
    {
        _root = root; _cfg = cfg;
        _quitVk = (int)Enum.Parse<Keys>(cfg.QuitKey, true);
        _quitProgressFile = string.IsNullOrWhiteSpace(cfg.QuitProgressFile) ? null : Environment.ExpandEnvironmentVariables(cfg.QuitProgressFile);

        CheckGames();
        _settingsPath = Path.Combine(root, "config", "settings.json");
        _settings = UserSettings.Load(_settingsPath);
        // Sinden reads its settings only when it starts, so write them first.
        bool sindenChanged = WriteSindenSettings(_settings.Recoil);
        StartSinden();
        if (sindenChanged && _startedSinden && _sinden is { HasExited: false } && !_sindenFresh) RestartSinden();

        _border = new BorderForm(cfg.BorderPercent);
        _quit = new QuitForm();
        _menu = new MenuForm(cfg, root) { RecoilOn = _settings.Recoil };
        _menu.RecoilToggled += ToggleRecoil;
        _menu.GameChosen += Launch;
        _menu.ExitChosen += reason => { ExitReason ??= reason; ExitThread(); };
        _menu.FormClosed += (_, e) => { ExitReason ??= "menu window closed (" + e.CloseReason + ")"; ExitThread(); };

        _hookProc = HookCallback;
        _hook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, _hookProc, Native.GetModuleHandle(null), 0);

        _tick.Tick += (_, _) => TickQuit();
        _tick.Start();

        _menu.Show();
        _border.Show();
        _border.BringToTop();
        CenterCursor();
    }

    void CheckGames()
    {
        foreach (var g in _cfg.Games)
        {
            g.MissingReason = null;
            foreach (var req in g.Requires)
            {
                var full = Path.Combine(_root, req);
                bool ok = req.Contains('*')
                    ? Directory.Exists(Path.GetDirectoryName(full)) && Directory.GetFiles(Path.GetDirectoryName(full)!, Path.GetFileName(full)).Length > 0
                    : File.Exists(full) || Directory.Exists(full);
                if (!ok) { g.MissingReason = "Missing " + Path.GetFileName(req.TrimEnd('\\', '/')); break; }
            }
        }
        // Slower checks (e.g. MAME -verifyroms) run in the background.
        foreach (var g in _cfg.Games.Where(g => g.MissingReason == null && g.Verify != null))
        {
            var game = g;
            Task.Run(() =>
            {
                try
                {
                    var exe = Path.Combine(_root, game.Verify!.Exe);
                    var psi = new ProcessStartInfo(exe, game.Verify.Args.Replace("{ROOT}", _root))
                    {
                        WorkingDirectory = game.Verify.Cwd != null ? Path.Combine(_root, game.Verify.Cwd) : Path.GetDirectoryName(exe)!,
                        CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
                    };
                    using var p = Process.Start(psi)!;
                    p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
                    p.WaitForExit(30000);
                    if (p.ExitCode != 0) game.MissingReason = game.VerifyMessage ?? "Check failed";
                }
                catch { }
                _menu?.BeginInvoke(() => _menu.SetStatus(""));
            });
        }
    }

    void StartSinden()
    {
        if (_cfg.Sinden == null) return;
        var exe = Path.Combine(_root, _cfg.Sinden.Exe);
        if (!File.Exists(exe)) return;
        var existing = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe));
        if (existing.Length > 0)
        {
            // Reuse a running copy. If it is the launcher's own copy, close it on exit as if we had started it;
            // a separate Sinden install is left alone.
            _sinden = existing.FirstOrDefault(p => SamePath(p, exe)) ?? existing[0];
            _startedSinden = SamePath(_sinden, exe);
            return;
        }
        LaunchSinden(exe);
        _sindenFresh = true;
        if (_cfg.Sinden.DelayMs > 0) Thread.Sleep(_cfg.Sinden.DelayMs);
    }

    void LaunchSinden(string exe)
    {
        _sinden = Process.Start(new ProcessStartInfo(exe, _cfg.Sinden!.Args)
        {
            WorkingDirectory = Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            WindowStyle = ProcessWindowStyle.Minimized,
        });
        _startedSinden = true;
    }

    /// <summary>Write recoil on/off and the configured sindenSettings into the Sinden settings file.
    /// Returns true if the file changed.</summary>
    bool WriteSindenSettings(bool recoil)
    {
        if (string.IsNullOrWhiteSpace(_cfg.SindenConfig)) return false;
        try
        {
            var path = Path.Combine(_root, _cfg.SindenConfig);
            if (!File.Exists(path)) return false;
            var text = File.ReadAllText(path);
            var updated = SetAppSetting(text, "chkEnableRecoil", recoil ? "1" : "0");
            foreach (var (k, v) in _cfg.SindenSettings) updated = SetAppSetting(updated, k, v);
            // Recoil only works once its terms box is ticked; trigger recoil in single-shot mode.
            if (recoil)
            {
                updated = SetAppSetting(updated, "chkRecoilTerms", "1");
                updated = SetAppSetting(updated, "chkRecoilTrigger", "1");
            }
            if (updated == text) return false;
            File.WriteAllText(path, updated);
            GameRunner.Log($"Sinden settings written (recoil {(recoil ? "on" : "off")}, {_cfg.SindenSettings.Count} configured settings)");
            return true;
        }
        catch (Exception ex) { GameRunner.Log("sinden settings: " + ex.Message); return false; }
    }

    static string SetAppSetting(string xml, string key, string value) =>
        System.Text.RegularExpressions.Regex.Replace(xml,
            $"(<add\\s+key=\"{System.Text.RegularExpressions.Regex.Escape(key)}\"\\s+value=\")[^\"]*(\")",
            m => m.Groups[1].Value + value + m.Groups[2].Value);

    void ToggleRecoil()
    {
        if (_restartingSinden) return;
        if (string.IsNullOrWhiteSpace(_cfg.SindenConfig)) { _menu.SetStatus("Recoil switch needs \"sindenConfig\" in games.json"); return; }
        _settings.Recoil = !_settings.Recoil;
        _settings.Save(_settingsPath);
        _menu.RecoilOn = _settings.Recoil;
        WriteSindenSettings(_settings.Recoil);
        if (_sinden != null && !_startedSinden)
        {
            // Another Sinden install is running; the launcher must not touch it.
            _menu.SetStatus("Recoil saved – restart your Sinden software to apply it");
            return;
        }
        _menu.SetStatus(_settings.Recoil ? "Turning recoil on…" : "Turning recoil off…");
        _restartingSinden = true;
        _menu.RestartingSinden = true;
        Task.Run(() =>
        {
            RestartSinden();
            _menu.BeginInvoke(() =>
            {
                _restartingSinden = false;
                _menu.RestartingSinden = false;
                _menu.RefreshGunHandles();
                // A freshly started Sinden can pop its window up; keep the menu in front.
                _menu.Activate();
                _border.BringToTop();
                _menu.SetStatus(_settings.Recoil ? "Recoil is on" : "Recoil is off");
            });
        });
    }

    /// <summary>Close the launcher's Sinden copy and start it again so it picks up new settings.</summary>
    void RestartSinden()
    {
        if (_cfg.Sinden == null) return;
        var exe = Path.Combine(_root, _cfg.Sinden.Exe);
        try
        {
            GameRunner.Log("sinden restart: closing");
            CloseSinden(_sinden);
            // Give Windows a moment to release the guns' cameras and serial ports before the new copy opens them.
            Thread.Sleep(1500);
            if (File.Exists(exe)) LaunchSinden(exe);
            Thread.Sleep(Math.Max(_cfg.Sinden.DelayMs, 3000));
            GameRunner.Log($"sinden restart: running={_sinden is { HasExited: false }}");
        }
        catch (Exception ex) { GameRunner.Log("sinden restart: " + ex); }
    }

    /// <summary>
    /// Close Sinden the way its own Exit does, so it disconnects each gun (camera and serial port) cleanly.
    /// Force-killing it can make the guns drop off USB until they are replugged. It lives in the tray with a hidden
    /// window, so WM_CLOSE goes to that window; kill only as a last resort.
    /// </summary>
    static void CloseSinden(Process? p)
    {
        if (p is not { HasExited: false }) return;
        try
        {
            int sent = Native.PostCloseToProcessWindows(p.Id, "SindenLightgun");
            if (sent == 0) p.CloseMainWindow();
            if (p.WaitForExit(8000)) return;
            GameRunner.Log("sinden did not close in time; killing it");
            p.Kill();
            p.WaitForExit(3000);
        }
        catch (Exception ex) { GameRunner.Log("sinden close: " + ex.Message); }
    }

    static bool SamePath(Process p, string exe)
    {
        try { return string.Equals(Path.GetFullPath(p.MainModule!.FileName), Path.GetFullPath(exe), StringComparison.OrdinalIgnoreCase); }
        catch { return false; }
    }

    readonly Queue<string> _menuRunQueue = new();

    /// <summary>Test: "a,b" runs a, and when that game ends, b 10 s later, all via the menu path.</summary>
    public void LaunchFromMenu(string ids)
    {
        foreach (var x in ids.Split(',', StringSplitOptions.RemoveEmptyEntries).Skip(1)) _menuRunQueue.Enqueue(x);
        var id = ids.Split(',')[0];
        var g = _cfg.Games.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (g != null) { GameRunner.Log($"test: tile {id} chosen (--menu-run)"); Launch(g, force: true); }
    }

    public void LaunchById(string id)
    {
        var g = _cfg.Games.FirstOrDefault(x => x.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (g != null) Launch(g, force: true);
    }

    // A gun that has dropped off USB can't aim in the game, and emulators only look for the guns when they start.
    // Warn first; shooting the tile again within 10 s starts anyway.
    Game? _missingGunWarned;
    DateTime _missingGunWarnedAt;

    void Launch(Game game) => Launch(game, force: false);

    void Launch(Game game, bool force)
    {
        if (_runner?.IsRunning == true || _restartingSinden) return;
        var guns = GunDevices.Detect(_cfg.P1Gun, _cfg.P2Gun);
        var missing = new List<string>();
        if (guns.P1Mouse == null) missing.Add(_cfg.P1Name);
        if (guns.P2Mouse == null && game.Players > 1) missing.Add(_cfg.P2Name);
        if (missing.Count > 0 && !force && !(_missingGunWarned == game && DateTime.UtcNow - _missingGunWarnedAt < TimeSpan.FromSeconds(10)))
        {
            _missingGunWarned = game;
            _missingGunWarnedAt = DateTime.UtcNow;
            GameRunner.Log($"launch {game.Id}: not connected: {string.Join(", ", missing)}");
            _menu.SetStatus($"{string.Join(" and ", missing)} not connected – replug the USB cable. Shoot again to start anyway.");
            return;
        }
        _missingGunWarned = null;
        try
        {
            _menu.SetStatus("Starting " + game.Title + "…");
            _menu.Refresh();
            var runner = new GameRunner(_root, _cfg, game) { BackdropHandle = _backdrop.Handle };
            runner.Exited += () => _menu.BeginInvoke(GameEnded);
            _runner = runner;
            if (game.Aspect != null)
            {
                // On top of everything until the game window appears, so loader windows stay hidden; the window
                // watcher then puts it directly below the game window, which also drops its "always on top".
                _backdrop.Show();
                Native.SetWindowPos(_backdrop.Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            Session.Start(_root, _cfg, game);
            MenuForm.TriggerKeys = TriggerKeyMap(game);
            runner.Start();
            _menu.Hide();
            _border.SetArea(game.BorderArea != null ? GameRunner.TargetRect(game.BorderArea) : null);
            if (game.Border == "overlay") { _border.Show(); _border.BringToTop(); } else _border.Hide();
            HideCursorOffscreen();
        }
        catch (Exception ex)
        {
            GameRunner.Log("launch failed: " + ex);
            Session.End("launch failed: " + ex.Message);
            _border.SetArea(null);
            _menu.SetStatus("Could not start " + game.Title + ": " + ex.Message);
            _runner = null;
        }
    }

    static byte[]? TriggerKeyMap(Game game)
    {
        if (game.TriggerKeys is not { Count: > 0 } tk) return null;
        var map = new byte[3];
        foreach (var (gun, key) in tk)
            if (int.TryParse(gun, out int g) && g is 1 or 2 && Enum.TryParse<Keys>(key, true, out var k)) map[g] = (byte)k;
        GameRunner.Log($"  trigger keys: gun 1 = {(Keys)map[1]}, gun 2 = {(Keys)map[2]}");
        return map;
    }

    void GameEnded()
    {
        MenuForm.ReleaseTriggerKeys();
        MenuForm.TriggerKeys = null;
        Task.Run(() => Session.End("game ended"));   // copies logs; keep the menu responsive
        _runner = null;
        _quitHeldSince = null;
        _quit.Hide();
        _backdrop.Hide();
        _menu.SetStatus("");
        _menu.Show();
        _menu.Activate();
        _border.SetArea(null);
        _border.Show();
        _border.BringToTop();
        CenterCursor();
        if (_menuRunQueue.Count > 0)
        {
            var next = _menuRunQueue.Dequeue();
            var t = new System.Windows.Forms.Timer { Interval = 10000 };
            t.Tick += (_, _) => { t.Stop(); t.Dispose(); LaunchFromMenu(next); };
            t.Start();
        }
    }

    static void HideCursorOffscreen()
    {
        Native.HideSystemCursors();
        var s = Screen.PrimaryScreen!.Bounds;
        Native.SetCursorPos(s.Right - 1, s.Bottom - 1);
    }

    static void CenterCursor()
    {
        Native.RestoreSystemCursors();
        var s = Screen.PrimaryScreen!.Bounds;
        Native.SetCursorPos(s.X + s.Width / 2, s.Y + s.Height / 2);
    }

    static bool IsDown(IntPtr wParam) => (int)wParam == Native.WM_KEYDOWN || (int)wParam == Native.WM_SYSKEYDOWN;

    static bool InList(List<string> keys, uint vk) =>
        keys.Any(r => Enum.TryParse<Keys>(r, true, out var rk) && (uint)rk == vk);

    IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var k = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            bool down = IsDown(wParam);
            bool injected = (k.flags & 0x10) != 0; // LLKHF_INJECTED: our own software copies pass straight through
            // Esc quits from the menu even when another window has the keyboard focus (no game running).
            if (k.vkCode == 0x1B && _runner == null && !_exiting && down)
            {
                _menu.BeginInvoke(() => { ExitReason ??= "Escape key (global)"; ExitThread(); });
                return 1;
            }
            if (k.vkCode == _quitVk)
            {
                if (down) { if (_quitHeldSince == null && _runner != null) _quitHeldSince = DateTime.UtcNow; }
                else _quitHeldSince = null;
                // Some games need the quit button for menus: pass it on as another key (posted, so the hook returns at once).
                if (_runner?.Game.QuitKeyAlso is string also && Enum.TryParse<Keys>(also, true, out var alsoKey))
                    _menu.BeginInvoke(() => SendKey((byte)alsoKey, down));
                return 1; // the quit button is reserved for the launcher; games never see it
            }
            // RelayKeys: swallow the real press and send it again from software (see Game.RelayKeys).
            if (!injected && _runner?.Game.RelayKeys is { Count: > 0 } relay && InList(relay, k.vkCode))
            {
                byte vk = (byte)k.vkCode;
                _menu.BeginInvoke(() => SendKey(vk, down));
                return 1;
            }
            // CopyKeys: the real key passes on; a software copy follows (see Game.CopyKeys).
            if (!injected && _runner?.Game.CopyKeys is { Count: > 0 } copy && InList(copy, k.vkCode))
            {
                byte vk = (byte)k.vkCode;
                _menu.BeginInvoke(() => SendKey(vk, down));
            }
        }
        return Native.CallNextHookEx(_hook, nCode, wParam, lParam);
    }

    /// <summary>Press or release a key for the game in front. Arrows/Insert/Delete/Home/End/PgUp/PgDn are "extended" keys.</summary>
    static void SendKey(byte vk, bool down)
    {
        bool extended = vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;
        uint flags = (extended ? 1u : 0u) | (down ? 0u : 2u);
        Native.keybd_event(vk, (byte)Native.MapVirtualKey(vk, 0), flags, UIntPtr.Zero);
    }

    // Games that can't show the launcher's banner window can draw it themselves while this file exists (an in-game
    // mod reads it). It holds the hold-to-quit progress, 0..1. See LauncherConfig.QuitProgressFile.
    double _quitFileWritten = -1;

    void WriteQuitProgress(double p)
    {
        if (_quitProgressFile == null) return;
        try
        {
            if (p < 0) { if (_quitFileWritten >= 0) File.Delete(_quitProgressFile); }
            else if (Math.Abs(p - _quitFileWritten) >= 0.02) File.WriteAllText(_quitProgressFile, p.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
            else return;
            _quitFileWritten = p;
        }
        catch { }
    }

    void TickQuit()
    {
        if (_quitHeldSince is DateTime t && _runner != null)
        {
            var held = (DateTime.UtcNow - t).TotalMilliseconds;
            _quit.Progress = held / _cfg.QuitHoldMs;
            WriteQuitProgress(held >= 500 ? Math.Min(1, held / _cfg.QuitHoldMs) : -1);
            // Only show the "hold to quit" banner after half a second, so quick taps (menu Down) don't flash it.
            if (held >= 500)
            {
                if (!_quit.Visible) { _quit.Show(); }
                _quit.BringToTop();
            }
            if (held >= _cfg.QuitHoldMs)
            {
                _quitHeldSince = null;
                _quit.Hide();
                WriteQuitProgress(-1);
                GameRunner.Log("quit: quit button held");
                Session.EndReason("quit by the player (quit button held)");
                _runner.Stop();
            }
        }
        else
        {
            if (_quit.Visible) _quit.Hide();
            WriteQuitProgress(-1);
        }
    }

    bool _exiting;
    /// <summary>Why the launcher is closing, for launcher.log.</summary>
    public static string? ExitReason;

    protected override void ExitThreadCore()
    {
        if (_exiting) return;
        _exiting = true;
        GameRunner.Log("launcher exiting: " + (ExitReason ?? "unknown"));
        _tick.Stop();
        if (_hook != IntPtr.Zero) Native.UnhookWindowsHookEx(_hook);
        Session.EndReason("launcher closed (" + (ExitReason ?? "unknown") + ")");
        _runner?.Stop();
        Session.End("launcher closed");
        if (_startedSinden) CloseSinden(_sinden);
        Native.RestoreSystemCursors();
        Cursor.Show();
        _border.Close(); _quit.Close(); _backdrop.Close(); _menu.Close();
        base.ExitThreadCore();
    }
}
