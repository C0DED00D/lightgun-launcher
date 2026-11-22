using System.Diagnostics;

namespace LightgunLauncher;

/// <summary>Starts one game (helpers + emulator) and tears it all down again.</summary>
class GameRunner
{
    readonly string _root;
    readonly LauncherConfig _cfg;
    readonly Game _game;
    readonly List<Process> _helpers = new();
    Process? _emu;
    volatile bool _stopping;

    public event Action? Exited;

    public GameRunner(string root, LauncherConfig cfg, Game game)
    {
        _root = root; _cfg = cfg; _game = game;
    }

    /// <summary>Black backdrop for 4:3 games; kept directly below the game window so loader consoles etc. stay hidden.</summary>
    public IntPtr BackdropHandle { get; set; }

    public Game Game => _game;
    public bool IsRunning => _emu is { HasExited: false };

    public string Resolve(string s) => Path.IsPathRooted(s) ? s : Path.Combine(_root, s);

    public void Start()
    {
        var guns = GunDevices.Detect(_cfg.P1Gun, _cfg.P2Gun);
        var vars = TemplateVars(_root, _cfg, guns);
        Log($"Start {_game.Id}: P1 mouse #{guns.P1MouseIndex} {guns.P1Mouse?.Path}, P2 mouse #{guns.P2MouseIndex} {guns.P2Mouse?.Path}");
        ApplyTemplates(vars);
        StartProcesses(vars);
    }

    /// <summary>The {PLACEHOLDERS} filled into templates (contents and paths), helper and emulator arguments.
    /// See docs\configuration.md for the list.</summary>
    public static Dictionary<string, string> TemplateVars(string root, LauncherConfig cfg, GunDevices guns) =>
        new()
        {
            ["{ROOT}"] = root,
            ["{P1GUN}"] = cfg.P1Gun,
            ["{P2GUN}"] = cfg.P2Gun,
            // 1-based raw mouse numbers as Supermodel counts them.
            ["{P1MOUSE}"] = guns.P1MouseIndex.ToString(),
            ["{P2MOUSE}"] = guns.P2MouseIndex.ToString(),
            ["{P1MOUSE_PATH}"] = guns.P1Mouse?.Path ?? "",
            ["{P2MOUSE_PATH}"] = guns.P2Mouse?.Path ?? "",
            ["{P1KBD_PATH}"] = guns.P1Keyboard?.Path ?? "",
            ["{P2KBD_PATH}"] = guns.P2Keyboard?.Path ?? "",
            // Hardware id: the part of the path between "HID#" and the next '#', e.g. "VID_16C0&PID_0F02&MI_02&Col02".
            // Flycast names its per-device mapping files "<device name> [<hardware id>]_arcade.cfg".
            ["{P1MOUSE_HWID}"] = HardwareId(guns.P1Mouse?.Path),
            ["{P2MOUSE_HWID}"] = HardwareId(guns.P2Mouse?.Path),
            ["{P1KBD_HWID}"] = HardwareId(guns.P1Keyboard?.Path),
            ["{P2KBD_HWID}"] = HardwareId(guns.P2Keyboard?.Path),
            // Flycast's device ids: the path without "\\?\HID#" (its emu.cfg keys are maple_raw_mouse_<id>).
            ["{P1MOUSE_ID}"] = FlycastId(guns.P1Mouse?.Path),
            ["{P2MOUSE_ID}"] = FlycastId(guns.P2Mouse?.Path),
            ["{P1KBD_ID}"] = FlycastId(guns.P1Keyboard?.Path),
            ["{P2KBD_ID}"] = FlycastId(guns.P2Keyboard?.Path),
            // Every other mouse and keyboard switched off in Flycast (-1), so a desk mouse/keyboard can't take a player's port.
            ["{FLYCAST_OTHER_DEVICES}"] = FlycastOtherDevices(guns),
            // Current screen size, and the largest 4:3 area on it, for emulators that need explicit resolutions.
            ["{SCREEN_W}"] = Screen.PrimaryScreen!.Bounds.Width.ToString(),
            ["{SCREEN_H}"] = Screen.PrimaryScreen!.Bounds.Height.ToString(),
            // One pixel short of the screen: a window that doesn't cover the whole screen is never sent straight
            // to the display, so the launcher's border and quit banner stay visible over it.
            ["{SCREEN_H_1}"] = (Screen.PrimaryScreen!.Bounds.Height - 1).ToString(),
            ["{W43}"] = TargetRect("4:3").Width.ToString(),
            ["{H43}"] = TargetRect("4:3").Height.ToString(),
        };

    static string Fill(string s, Dictionary<string, string> vars)
    {
        foreach (var (k, v) in vars) s = s.Replace(k, v);
        return s;
    }

    /// <summary>Fill the placeholders into a text template. Device paths contain '&', escaped inside XML files.</summary>
    public static string RenderTemplate(string text, Dictionary<string, string> vars, bool xml)
    {
        foreach (var (k, v) in vars) text = text.Replace(k, xml ? System.Security.SecurityElement.Escape(v) : v);
        return text;
    }

    void ApplyTemplates(Dictionary<string, string> vars)
    {
        foreach (var del in _game.DeleteBeforeLaunch)
        {
            var path = Resolve(Fill(del, vars));
            try { if (File.Exists(path)) { File.Delete(path); Log("  deleted " + path); } }
            catch (Exception ex) { Log("  could not delete " + path + ": " + ex.Message); }
        }

        foreach (var t in _game.Templates)
        {
            var from = Resolve(Fill(t.From, vars));
            var dest = Resolve(Fill(t.To, vars));
            if (t.Binary)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(from, dest, true);
                continue;
            }
            var text = RenderTemplate(File.ReadAllText(from), vars, dest.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.WriteAllText(dest, text);
        }
    }

    void StartProcesses(Dictionary<string, string> vars)
    {
        // Let the game take focus from the launcher when its window opens.
        Native.AllowSetForegroundWindow(-1);
        foreach (var h in _game.Helpers)
        {
            var p = Launch(h, vars);
            if (p != null) _helpers.Add(p);
            if (h.DelayMs > 0) Thread.Sleep(h.DelayMs);
        }

        _emu = Launch(_game.Run, vars) ?? throw new InvalidOperationException("Could not start " + _game.Run.Exe);
        _emu.EnableRaisingEvents = true;
        _emu.Exited += (_, _) => OnEmuExited();

        if (_game.Borderless)
        {
            var backdrop = _game.Aspect != null ? BackdropHandle : IntPtr.Zero;
            var fit = _game.FitChildWindow;
            var keepBorder = _game.KeepComposited;
            var rect = TargetRect(_game.Aspect);
            // Also stop 1 px short of the screen bottom: a window covering the whole screen can still be sent
            // straight to the display, even with the 1-px border.
            if (keepBorder) rect.Height -= 1;
            if (_game.BorderInset)
            {
                // Same thickness BorderForm draws; keep the picture's aspect.
                int t = Math.Max(4, (int)Math.Round(Screen.PrimaryScreen!.Bounds.Height * _cfg.BorderPercent / 100.0));
                int h = rect.Height - 2 * t, w = (int)Math.Round(rect.Width * (double)h / rect.Height);
                rect = new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + t, w, h);
            }
            var delay = _game.BorderlessDelayMs;
            var keepFg = _game.KeepForeground;
            var refresh = _game.RefreshWindowOnStart;
            _ = Task.Run(() => MakeBorderless(_emu, rect, _game.WindowProcess, backdrop, fit, keepBorder, delay, keepFg, refresh));
        }
        else
        {
            _ = Task.Run(() => KeepInFront(_emu, _game.WindowProcess));
            if (_game.KeepComposited)
                _ = Task.Run(() => KeepComposited(_emu, _game.WindowProcess));
        }
        if (_game.PostLaunchKeys.Count > 0)
            _ = Task.Run(() => TapKeys(_emu, _game.PostLaunchKeys));
    }

    /// <summary>Flycast's raw-input id for a device path (windows/rawinput.cpp): "\\?\HID#" stripped, '=' '[' ']' -> '_'.</summary>
    public static string FlycastId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var id = path.StartsWith(@"\\?\HID#", StringComparison.OrdinalIgnoreCase) ? path[8..] : path;
        return id.Replace('=', '_').Replace('[', '_').Replace(']', '_');
    }

    /// <summary>"VID_xxxx&PID_xxxx&MI_xx&Colxx" from "\\?\HID#VID_xxxx&PID_xxxx&MI_xx&Colxx#...".</summary>
    public static string HardwareId(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var parts = path.Split('#');
        return parts.Length > 1 ? parts[1] : path;
    }

    static string FlycastOtherDevices(GunDevices guns)
    {
        var gunPaths = new[] { guns.P1Mouse, guns.P2Mouse, guns.P1Keyboard, guns.P2Keyboard }.Where(d => d != null).Select(d => d!.Path).ToHashSet();
        // Keyboards too: an unassigned desk keyboard lands on port 0 (P1) and can take over player 1's keys.
        var lines = RawDevices.List()
            .Where(d => (d.Type == Native.RIM_TYPEMOUSE || d.Type == Native.RIM_TYPEKEYBOARD) && !gunPaths.Contains(d.Path))
            .Select(d => $"maple_raw_{(d.Type == Native.RIM_TYPEMOUSE ? "mouse" : "keyboard")}_{FlycastId(d.Path)} = -1");
        return string.Join("\r\n", lines);
    }

    static void TapKeys(Process p, List<KeyTap> taps)
    {
        foreach (var t in taps)
        {
            Thread.Sleep(t.DelayMs);
            if (p.HasExited) return;
            var h = FindMainWindow(p.Id);
            if (h != IntPtr.Zero) Native.SetForegroundWindow(h);
            if (t.Center)
            {
                var s = Screen.PrimaryScreen!.Bounds;
                Native.SetCursorPos(s.X + s.Width / 2, s.Y + s.Height / 2);
                Thread.Sleep(150);
            }
            byte vk = (byte)Enum.Parse<Keys>(t.Key, true);
            Native.keybd_event(vk, 0, 0, UIntPtr.Zero);
            Thread.Sleep(120);
            Native.keybd_event(vk, 0, 2 /*KEYUP*/, UIntPtr.Zero);
        }
    }

    Process? Launch(ProcessSpec spec, Dictionary<string, string> vars)
    {
        string args = Fill(spec.Args, vars);
        var exe = Resolve(Fill(spec.Exe, vars));
        if (spec.Elevate)
        {
            Log($"  launch (admin) {exe} {args}");
            try
            {
                return Process.Start(new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = spec.Cwd != null ? Resolve(spec.Cwd) : Path.GetDirectoryName(exe)!,
                    UseShellExecute = true, Verb = "runas",
                    WindowStyle = spec.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
                });
            }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                Log("  admin prompt declined; starting the game without it");
                return null;
            }
        }
        var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = spec.Cwd != null ? Resolve(spec.Cwd) : Path.GetDirectoryName(exe)!,
            UseShellExecute = false,
            WindowStyle = spec.Hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal,
            // Console programs (MAME, Supermodel, memory helpers) would otherwise open a terminal window that can
            // flash up or sit over the game; their game window is unaffected by this.
            CreateNoWindow = spec.Hidden || IsConsoleProgram(exe),
        };
        if (spec.Env != null)
            foreach (var (k, v) in spec.Env) psi.Environment[k] = v;
        Log($"  launch {exe} {args}");
        return Process.Start(psi);
    }

    /// <summary>True for a Windows console-subsystem .exe (PE optional header Subsystem == 3).</summary>
    static bool IsConsoleProgram(string exe)
    {
        try
        {
            using var f = File.OpenRead(exe);
            using var r = new BinaryReader(f);
            f.Seek(0x3C, SeekOrigin.Begin);
            int pe = r.ReadInt32();
            f.Seek(pe + 4 + 20 + 68, SeekOrigin.Begin); // PE sig + file header + Subsystem offset (same in PE32/PE32+)
            return r.ReadUInt16() == 3;
        }
        catch { return false; }
    }

    /// <summary>Full screen, or the largest centered rectangle with the given aspect ("4:3").</summary>
    public static Rectangle TargetRect(string? aspect)
    {
        var s = Screen.PrimaryScreen!.Bounds;
        var parts = aspect?.Split(':');
        if (parts is not { Length: 2 } || !double.TryParse(parts[0], out var aw) || !double.TryParse(parts[1], out var ah))
            return s;
        int w = (int)Math.Round(s.Height * aw / ah), h = s.Height;
        if (w > s.Width) { w = s.Width; h = (int)Math.Round(s.Width * ah / aw); }
        return new Rectangle(s.X + (s.Width - w) / 2, s.Y + (s.Height - h) / 2, w, h);
    }

    /// <summary>Remove the menu bar and make the largest visible child window fill the client area.</summary>
    static void FitChild(IntPtr h)
    {
        if (Native.GetMenu(h) != IntPtr.Zero)
        {
            Native.SetMenu(h, IntPtr.Zero);
            Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
        }
        Native.GetClientRect(h, out var client);
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Native.EnumChildWindows(h, (c, _) =>
        {
            if (Native.GetParent(c) != h || !Native.IsWindowVisible(c)) return true;
            Native.GetWindowRect(c, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = c; }
            return true;
        }, IntPtr.Zero);
        if (best == IntPtr.Zero) return;
        Native.GetWindowRect(best, out var cr);
        Native.MapWindowPoints(IntPtr.Zero, h, ref cr, 2);
        if (cr.Left != 0 || cr.Top != 0 || cr.Right != client.Right || cr.Bottom != client.Bottom)
            Native.SetWindowPos(best, IntPtr.Zero, 0, 0, client.Right, client.Bottom, Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
    }

    static string ProcessNameOf(IntPtr h)
    {
        Native.GetWindowThreadProcessId(h, out uint pid);
        try { return Process.GetProcessById((int)pid).ProcessName; } catch { return "?"; }
    }

    /// <summary>True when fg belongs to the game's own process or to the launcher (its banner/border).</summary>
    static bool IsOwnProcessWindow(IntPtr fg, IntPtr game)
    {
        if (fg == IntPtr.Zero) return false;
        Native.GetWindowThreadProcessId(fg, out uint a);
        Native.GetWindowThreadProcessId(game, out uint b);
        return a == b || a == (uint)Environment.ProcessId;
    }

    static void MakeBorderless(Process p, Rectangle screen, string? windowProcess, IntPtr backdrop, bool fitChild = false, bool keepBorder = false, int delayMs = 0, bool keepForeground = false, bool refreshOnStart = false)
    {
        DateTime? firstSeen = null;
        // keepBorder: leave a 1-px WS_BORDER so Windows keeps compositing the window (see Game.KeepComposited).
        long strip = Native.WS_CAPTION | Native.WS_THICKFRAME | Native.WS_BORDER | Native.WS_DLGFRAME |
                     Native.WS_SYSMENU | Native.WS_MINIMIZEBOX | Native.WS_MAXIMIZEBOX;
        long frame = Native.WS_CAPTION | Native.WS_THICKFRAME;
        if (keepBorder) { strip &= ~Native.WS_BORDER; frame = Native.WS_DLGFRAME | Native.WS_THICKFRAME; }
        IntPtr last = IntPtr.Zero;
        // Emulators may recreate their window after loading, and some games (Unity) resize themselves back to
        // full screen later on, so keep checking for as long as the game runs. Only changes are re-applied.
        int fixes = 0, focusTries = 0, awayChecks = 0;
        DateTime focusSince = DateTime.MinValue, lastPull = DateTime.MinValue;
        bool gaveUp = false;
        DateTime fixWindowStart = DateTime.UtcNow;
        Log($"  window watch: {windowProcess ?? p.ProcessName} -> {screen.Width}x{screen.Height} at {screen.X},{screen.Y}");
        DateTime lastRestore = DateTime.MinValue;
        DateTime? refreshAt = null;
        // TeknoParrotUi can close while its game keeps running: keep watching the game's own process too.
        while (!p.HasExited || (windowProcess != null && Process.GetProcessesByName(windowProcess).Length > 0))
        {
            try
            {
                // Front-ends like TeknoParrot start the game as a separate process; follow that one's window.
                var h = IntPtr.Zero;
                if (windowProcess == null) h = FindMainWindow(p.Id);
                else
                    foreach (var gp in Process.GetProcessesByName(windowProcess))
                        if ((h = FindMainWindow(gp.Id)) != IntPtr.Zero) break;
                if (h != IntPtr.Zero && delayMs > 0 && (firstSeen ??= DateTime.UtcNow) > DateTime.UtcNow.AddMilliseconds(-delayMs))
                {
                    Thread.Sleep(250);
                    continue;
                }
                // keepForeground games: some full-screen games (Unity) minimize themselves as soon as anything else
                // takes focus (a loader console, Sinden), and then stay down. Restore them.
                if (keepForeground && h != IntPtr.Zero && h == last && Native.IsIconic(h) &&
                    DateTime.UtcNow - lastRestore > TimeSpan.FromSeconds(2))
                {
                    Log($"  game window minimized (foreground: {ProcessNameOf(Native.GetForegroundWindow())}); restoring it");
                    Native.ShowWindow(h, Native.SW_RESTORE);
                    Native.ForceForeground(h);
                    lastRestore = DateTime.UtcNow;
                }
                // Other games: a minimized game was put away on purpose (e.g. someone using the desktop): leave it
                // alone, and never pull it back into the foreground.
                if (h != IntPtr.Zero && !Native.IsIconic(h))
                {
                    long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE);
                    long ex = Native.GetWindowLongPtr(h, Native.GWL_EXSTYLE);
                    bool framed = (style & frame) != 0 || (keepBorder && (style & Native.WS_BORDER) == 0);
                    bool topmost = (ex & Native.WS_EX_TOPMOST) != 0;
                    Native.GetWindowRect(h, out var r);
                    bool covers = r.Left == screen.Left && r.Top == screen.Top && r.Right == screen.Right && r.Bottom == screen.Bottom;
                    // A game that snaps its window straight back (Unity sizes it from its own saved resolution) would
                    // be resized twice a second, which blanks the picture. After 10 fixes in 15 s, leave it alone.
                    if (h == last && (gaveUp || (fixes >= 10 && DateTime.UtcNow - fixWindowStart < TimeSpan.FromSeconds(15))))
                    {
                        if (!gaveUp) Log("  window keeps changing back; leaving it as the game wants");
                        gaveUp = true;
                    }
                    else if (framed || !covers || topmost || h != last)
                    {
                        if (fixes == 0 || h != last) fixWindowStart = DateTime.UtcNow;
                        if (++fixes <= 20)
                            Log($"  window fix #{fixes}: was {r.Right - r.Left}x{r.Bottom - r.Top} at {r.Left},{r.Top}" +
                                $"{(framed ? " framed" : "")}{(topmost ? " topmost" : "")}{(h != last ? " new" : "")}");
                        style &= ~strip;
                        if (keepBorder) style |= Native.WS_BORDER;
                        Native.SetWindowLongPtr(h, Native.GWL_STYLE, style);
                        // Games that make themselves "always on top" would cover the launcher's border and banner.
                        Native.SetWindowPos(h, topmost ? Native.HWND_NOTOPMOST : IntPtr.Zero, screen.X, screen.Y, screen.Width, screen.Height,
                            Native.SWP_FRAMECHANGED | Native.SWP_SHOWWINDOW);
                        if (h != last)
                        {
                            Native.ForceForeground(h);
                            focusSince = DateTime.UtcNow; focusTries = 0;
                            // keepForeground games: a window that comes up already full screen + topmost can stay
                            // see-through (the desktop shows) even though it is the foreground window. One minimize +
                            // restore makes the game rebuild its picture. Done 2 s later, after our resize has settled.
                            if ((keepForeground && topmost && !framed) || refreshOnStart) refreshAt = DateTime.UtcNow.AddSeconds(2);
                        }
                        last = h;
                    }
                    if (refreshAt is DateTime ra && DateTime.UtcNow >= ra && h == last)
                    {
                        refreshAt = null;
                        Log("  minimize + restore the game window so it draws (came up full screen + topmost, or refreshWindowOnStart)");
                        Native.ShowWindow(h, 6 /*SW_MINIMIZE*/);
                        Thread.Sleep(700);
                        Native.ShowWindow(h, Native.SW_RESTORE);
                        Native.ForceForeground(h);
                        lastRestore = DateTime.UtcNow;
                    }
                    // Front-ends and their loader consoles can take focus back after the game window opens, leaving
                    // the game behind them. For the first 30 s, pull it forward again.
                    // keepForeground games: for the whole game, whenever another program's window has stayed in
                    // front for a second, bring the game back, so nobody needs a keyboard to alt-tab.
                    // The launcher's own windows (border, banner) don't count.
                    if (keepForeground && h == last && DateTime.UtcNow - focusSince >= TimeSpan.FromSeconds(2))
                    {
                        var fgw = Native.GetForegroundWindow();
                        if (fgw != h && !IsOwnProcessWindow(fgw, h))
                        {
                            if (++awayChecks >= 2 && DateTime.UtcNow - lastPull > TimeSpan.FromSeconds(3))
                            {
                                Log($"  game window lost the foreground to {ProcessNameOf(fgw)}; bringing it back");
                                Native.ForceForeground(h);
                                lastPull = DateTime.UtcNow; awayChecks = 0;
                            }
                        }
                        else awayChecks = 0;
                    }
                    if (h == last && DateTime.UtcNow - focusSince < TimeSpan.FromSeconds(30) && focusTries < 6 &&
                        Native.GetForegroundWindow() != h)
                    {
                        Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out uint fgPid);
                        string fg = "?";
                        try { fg = Process.GetProcessById((int)fgPid).ProcessName; } catch { }
                        Log($"  game window not in front (foreground: {fg}); bringing it forward");
                        Native.ForceForeground(h);
                        focusTries++;
                    }
                    if (fitChild) FitChild(h);
                    if (backdrop != IntPtr.Zero)
                        Native.SetWindowPos(backdrop, h, 0, 0, 0, 0,
                            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
            }
            catch (Exception e) { Log("  window watch error: " + e.Message); }
            Thread.Sleep(500);
        }
        Log("  window watch ended");
    }

    /// <summary>For games that size their own window: make sure the game window ends up in front of everything
    /// (otherwise it can open behind whatever window had focus before the launcher).</summary>
    static void KeepInFront(Process p, string? windowProcess)
    {
        var sw = Stopwatch.StartNew();
        IntPtr last = IntPtr.Zero;
        while (sw.Elapsed < TimeSpan.FromSeconds(60) && !p.HasExited)
        {
            var h = IntPtr.Zero;
            if (windowProcess == null) h = FindMainWindow(p.Id);
            else
                foreach (var gp in Process.GetProcessesByName(windowProcess))
                    if ((h = FindMainWindow(gp.Id)) != IntPtr.Zero) break;
            // Only push when a (new) game window appears, so a user who switches away later isn't fought.
            if (h != IntPtr.Zero && h != last)
            {
                Thread.Sleep(300);
                Native.ForceForeground(h);
                last = h;
            }
            Thread.Sleep(500);
        }
    }

    static void KeepComposited(Process p, string? windowProcess)
    {
        // Games may go fullscreen (or recreate the window) at any time, so keep checking while the game runs.
        while (!p.HasExited)
        {
            var h = IntPtr.Zero;
            if (windowProcess == null) h = FindMainWindow(p.Id);
            else
                foreach (var gp in Process.GetProcessesByName(windowProcess))
                    if ((h = FindMainWindow(gp.Id)) != IntPtr.Zero) break;
            if (h != IntPtr.Zero)
            {
                long style = Native.GetWindowLongPtr(h, Native.GWL_STYLE);
                if ((style & Native.WS_BORDER) == 0)
                {
                    Native.SetWindowLongPtr(h, Native.GWL_STYLE, style | Native.WS_BORDER);
                    Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_FRAMECHANGED);
                }
            }
            Thread.Sleep(500);
        }
    }

    static IntPtr FindMainWindow(int pid)
    {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        Native.EnumWindows((h, _) =>
        {
            Native.GetWindowThreadProcessId(h, out uint wp);
            if (wp != pid || !Native.IsWindowVisible(h) || Native.GetWindow(h, 4 /*GW_OWNER*/) != IntPtr.Zero) return true;
            Native.GetWindowRect(h, out var r);
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }

    void OnEmuExited()
    {
        // Front-ends (TeknoParrotUi) may close while the game they started keeps running. Then the game is still
        // on: wait for its own process instead of killing it and showing the menu on top of it.
        if (!_stopping && _game.WindowProcess != null && Process.GetProcessesByName(_game.WindowProcess).Length > 0)
        {
            Log($"  {Path.GetFileNameWithoutExtension(_game.Run.Exe)} exited while {_game.WindowProcess} still runs; waiting for {_game.WindowProcess}");
            while (!_stopping && Process.GetProcessesByName(_game.WindowProcess).Length > 0) Thread.Sleep(500);
            Log($"  {_game.WindowProcess} exited");
        }
        string code = "";
        try { code = $" (exit code {_emu?.ExitCode})"; } catch { }
        Session.EndReason(_stopping ? "quit by the player" : $"{Path.GetFileNameWithoutExtension(_game.Run.Exe)} closed by itself{code}");
        CloseHelpers();
        Exited?.Invoke();
    }

    /// <summary>Ask the emulator to close (so it can save NVRAM/calibration), then force it.</summary>
    public void Stop()
    {
        if (_stopping) return;
        _stopping = true;
        Task.Run(() =>
        {
            try
            {
                if (_emu is { HasExited: false })
                {
                    try { _emu.CloseMainWindow(); } catch { }
                    if (!_emu.WaitForExit(_game.CloseWaitMs))
                        KillTree(_emu);
                }
            }
            catch (Exception ex) { Log("stop: " + ex.Message); }
            CloseHelpers();
        });
    }

    void CloseHelpers()
    {
        foreach (var h in _helpers) { try { if (!h.HasExited) KillTree(h); } catch { } }
        foreach (var name in _game.Kill)
            foreach (var p in Process.GetProcessesByName(name))
                try { p.Kill(true); } catch { }
    }

    static void KillTree(Process p) { try { p.Kill(true); } catch { } }

    public static void Log(string s)
    {
        try { File.AppendAllText(Path.Combine(AppContext.BaseDirectory, "launcher.log"), $"{DateTime.Now:HH:mm:ss} {s}\r\n"); } catch { }
        Session.Write(s);
    }
}
