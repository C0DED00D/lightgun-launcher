using System.Diagnostics;
using System.Drawing.Imaging;

namespace LightgunLauncher;

/// <summary>
/// Records each game session so a play test can be checked afterwards without the player describing it:
/// &lt;app&gt;\sessions\&lt;time&gt;_&lt;game&gt;\ gets session.log (everything the launcher logs while the game runs, plus window
/// focus changes, guns losing/regaining their aim, button presses), screenshots (every N s and shortly after every
/// gun button press), smoothness checks, copies of the emulator logs, and summary.txt. All switchable in
/// config\games.json "recording" (RecordingSettings).
/// </summary>
static class Session
{
    static readonly object Lock = new();
    static string? _dir;
    static StreamWriter? _log;
    static Thread? _thread;
    static volatile bool _running;
    static DateTime _start;
    static string _game = "", _gameTitle = "", _windowProcess = "";
    static string? _endReason;
    static int _shots;
    static DateTime _nextShot, _shotSoon = DateTime.MaxValue;
    static string _shotSoonWhy = "";
    static readonly Dictionary<string, int> Counts = new();
    static string _root = "";

    public static bool Active => _running;

    static readonly object StartEndLock = new();

    static RecordingSettings _cfg = new();
    static string _captureTool = "";
    static List<string> _logs = new();

    public static void Start(string root, LauncherConfig cfg, Game game)
    {
        lock (StartEndLock) StartLocked(root, cfg, game);
    }

    static void StartLocked(string root, LauncherConfig launcherCfg, Game game)
    {
        EndLocked("next game started");
        var settings = launcherCfg.Recording;
        _cfg = settings;
        if (!settings.Enabled) return;   // recording switched off in games.json "recording"
        _root = root;
        _captureTool = Path.Combine(root, launcherCfg.ScreenCaptureTool);
        _logs = settings.EmulatorLogs.Concat(game.Logs).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        try
        {
            var baseDir = Path.Combine(AppContext.BaseDirectory, "sessions");
            Directory.CreateDirectory(baseDir);
            Prune(baseDir, Math.Max(1, settings.KeepSessions));
            _dir = Path.Combine(baseDir, $"{DateTime.Now:yyyyMMdd-HHmmss}_{game.Id}");
            Directory.CreateDirectory(_dir);
            _log = new StreamWriter(Path.Combine(_dir, "session.log")) { AutoFlush = true };
        }
        catch { _dir = null; return; }
        lock (Lock) Counts.Clear();
        _game = game.Id; _gameTitle = game.Title;
        _windowProcess = game.WindowProcess ?? Path.GetFileNameWithoutExtension(game.Run.Exe);
        _start = DateTime.UtcNow; _endReason = null; _shots = 0;
        _nextShot = settings.ScreenshotEverySeconds > 0 ? DateTime.UtcNow.AddSeconds(3) : DateTime.MaxValue;
        _shotSoon = DateTime.MaxValue;
        _nextFps = settings.SmoothnessCheckEverySeconds > 0 ? DateTime.UtcNow.AddSeconds(20) : DateTime.MaxValue;
        _minFps = null;
        _running = true;
        Write($"session start: {game.Id} ({game.Title})");
        Write($"recording: screenshots every {settings.ScreenshotEverySeconds} s (0 = off), on button {settings.ScreenshotOnButton}, " +
              $"smoothness every {settings.SmoothnessCheckEverySeconds} s (0 = off), emulator logs {settings.CopyEmulatorLogs}, keep {settings.KeepSessions}");
        _thread = new Thread(Watch) { IsBackground = true, Name = "session watch" };
        _thread.Start();
    }

    /// <summary>Called by GameRunner.Log for every launcher log line.</summary>
    public static void Write(string line)
    {
        if (!_running || _log == null) return;
        try { lock (Lock) _log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}"); } catch { }
    }

    public static void Count(string what) { if (_running) lock (Lock) Counts[what] = Counts.GetValueOrDefault(what) + 1; }

    /// <summary>Take a screenshot shortly (150 ms) after an event such as a trigger pull.</summary>
    public static void ShotSoon(string why)
    {
        if (!_running || !_cfg.ScreenshotOnButton) return;
        var t = DateTime.UtcNow.AddMilliseconds(150);
        if (t < _shotSoon) { _shotSoon = t; _shotSoonWhy = why; }
    }

    /// <summary>Why the game ended (first reason wins: "quit: D-pad held", "emulator exited", ...).</summary>
    public static void EndReason(string why) { if (_running) _endReason ??= why; Write("end reason: " + why); }

    public static void End(string fallbackReason)
    {
        lock (StartEndLock) EndLocked(fallbackReason);
    }

    static void EndLocked(string fallbackReason)
    {
        if (!_running) return;
        _endReason ??= fallbackReason;
        Write("session end: " + _endReason);
        if (_cfg.CopyEmulatorLogs)
            try { CopyEmulatorLogs(); } catch (Exception e) { Write("copy logs: " + e.Message); }
        _running = false;
        _thread?.Join(3000);
        try { WriteSummary(); } catch { }
        lock (Lock) { try { _log?.Dispose(); } catch { } _log = null; }
    }

    // ---- watcher ----
    static void Watch()
    {
        string lastFg = "";
        var aimLostSince = new DateTime?[3];
        var edgeSince = new DateTime?[3];
        while (_running)
        {
            try
            {
                var now = DateTime.UtcNow;
                // Window in front (and whether the game window is minimized).
                var fg = Native.GetForegroundWindow();
                Native.GetWindowThreadProcessId(fg, out uint pid);
                string name = "?";
                try { name = Process.GetProcessById((int)pid).ProcessName; } catch { }
                string desc = name + (fg != IntPtr.Zero && Native.IsIconic(fg) ? " (minimized)" : "");
                if (desc != lastFg)
                {
                    Write($"front window: {desc}");
                    if (lastFg != "" && !name.Equals(_windowProcess, StringComparison.OrdinalIgnoreCase)) Count("game not in front");
                    lastFg = desc;
                }
                // Guns: a gun whose aim reports stop (Sinden lost the screen) or sit at the very edge.
                for (int g = 1; g <= 2; g++)
                {
                    var seen = MenuForm.GunSeenUtc[g];
                    if (seen == default) continue;
                    bool lost = (now - seen).TotalMilliseconds > 1000;
                    if (lost && aimLostSince[g] == null) { aimLostSince[g] = seen; Write($"gun {g} aim reports stopped (last at {MenuForm.GunAim[g].X:0.00},{MenuForm.GunAim[g].Y:0.00})"); Count($"gun {g} aim lost"); }
                    else if (!lost && aimLostSince[g] is DateTime ls) { Write($"gun {g} aim back after {(now - ls).TotalSeconds:0.0} s"); aimLostSince[g] = null; }
                    var a = MenuForm.GunAim[g];
                    bool edge = !lost && (a.X <= 0.005f || a.X >= 0.995f || a.Y <= 0.005f || a.Y >= 0.995f);
                    if (edge && edgeSince[g] == null) { edgeSince[g] = now; Write($"gun {g} aim at the screen edge {a.X:0.00},{a.Y:0.00}"); }
                    else if (!edge && edgeSince[g] != null) edgeSince[g] = null;
                }
                // Frame rate reaching the screen: a few seconds measured every N seconds, in real gameplay (scripted
                // tests and attract mode can look smooth when 2-player gameplay isn't).
                if (now >= _nextFps && (_fpsThread == null || !_fpsThread.IsAlive))
                {
                    _nextFps = now.AddSeconds(Math.Max(10, _cfg.SmoothnessCheckEverySeconds));
                    _fpsThread = new Thread(MeasureFps) { IsBackground = true, Name = "session fps" };
                    _fpsThread.Start();
                }
                // Screenshots: every 5 s, and just after button presses.
                if (now >= _shotSoon) { Shot(_shotSoonWhy); _shotSoon = DateTime.MaxValue; }
                else if (now >= _nextShot) { Shot("periodic"); _nextShot = now.AddSeconds(Math.Max(1, _cfg.ScreenshotEverySeconds)); }
            }
            catch (Exception e) { Write("watch error: " + e.Message); }
            Thread.Sleep(100);
        }
    }

    static DateTime _nextFps;
    static Thread? _fpsThread;

    /// <summary>dupshot "fps 5": frames presented to the screen over 5 s, plus the longest gaps.</summary>
    static void MeasureFps()
    {
        try
        {
            // Scripted tests that measure the screen themselves set this; two screen captures at once starve each other.
            if (Environment.GetEnvironmentVariable("LIGHTGUN_NO_SESSION_FPS") == "1") return;
            var exe = _captureTool;
            if (!File.Exists(exe)) return;
            var psi = new ProcessStartInfo(exe, "fps 5") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, WorkingDirectory = _dir ?? "" };
            using var p = Process.Start(psi);
            if (p == null) return;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(10000);
            var line = output.Split('\n').LastOrDefault()?.Trim() ?? "";
            Write($"screen {line}");
            // How the picture MOVES (60 presented fps can still be half repeats in clumps, which looks worse than 30):
            // new pictures per second in the middle of the screen and the longest freeze while it was moving.
            var mpsi = new ProcessStartInfo(exe, "motion 4 - 0.15 0.25 0.85 0.75") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true };
            using (var mp = Process.Start(mpsi))
                if (mp != null) { var mo = mp.StandardOutput.ReadToEnd().Trim(); mp.WaitForExit(10000); Write("screen " + (mo.Split('\n').LastOrDefault()?.Trim() ?? "")); }
            var parts = line.Split(' ');
            int i = Array.IndexOf(parts, "fps");
            if (i >= 0 && i + 1 < parts.Length && double.TryParse(parts[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double fps))
            {
                lock (Lock)
                {
                    if (!_minFps.HasValue || fps < _minFps) _minFps = fps;
                    if (fps < 50) Counts["fps checks below 50"] = Counts.GetValueOrDefault("fps checks below 50") + 1;
                    Counts["fps checks"] = Counts.GetValueOrDefault("fps checks") + 1;
                }
            }
        }
        catch (Exception e) { Write("fps check: " + e.Message); }
    }
    static double? _minFps;

    static void Shot(string why)
    {
        if (_dir == null || _shots >= 2000) return;
        var exe = _captureTool;
        if (!File.Exists(exe)) return;
        var stamp = $"{(DateTime.UtcNow - _start).TotalSeconds:0000.0}s";
        var bmp = Path.Combine(_dir, "shot.bmp");
        try
        {
            File.Delete(bmp);
            var p = Process.Start(new ProcessStartInfo(exe, $"\"{bmp}\"") { WorkingDirectory = _dir, CreateNoWindow = true, UseShellExecute = false });
            if (p == null || !p.WaitForExit(4000)) { try { p?.Kill(); } catch { } return; }
            var alt = Path.Combine(_dir, "dupshot.bmp");
            if (!File.Exists(bmp) && File.Exists(alt)) File.Move(alt, bmp);
            if (!File.Exists(bmp)) return;
            using (var src = new Bitmap(bmp))
            using (var small = new Bitmap(src, src.Width / 4, src.Height / 4))
            {
                var safe = string.Concat(why.Select(c => char.IsLetterOrDigit(c) ? c : '-'));
                var file = Path.Combine(_dir, $"{stamp}_{safe}.jpg");
                small.Save(file, ImageFormat.Jpeg);
            }
            File.Delete(bmp);
            _shots++;
        }
        catch { }
    }

    static void CopyEmulatorLogs()
    {
        if (_dir == null) return;
        // Emulator logs written during this session (each emulator overwrites its log on start).
        foreach (var rel in _logs)
        {
            var f = Path.Combine(_root, rel);
            if (!File.Exists(f) || File.GetLastWriteTimeUtc(f) < _start.AddSeconds(-5)) continue;
            var to = Path.Combine(_dir, rel.Replace('\\', '_'));
            using var src = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var dst = File.Create(to);
            src.CopyTo(dst);
            Write($"copied {rel}");
        }
    }

    static void WriteSummary()
    {
        if (_dir == null) return;
        var lines = new List<string>
        {
            $"Game: {_gameTitle} ({_game})",
            $"Started: {_start.ToLocalTime():yyyy-MM-dd HH:mm:ss}   Length: {(DateTime.UtcNow - _start).TotalSeconds:0} s",
            $"Ended: {_endReason}",
            $"Screenshots: {_shots}",
            $"Lowest screen fps (5 s checks every {_cfg.SmoothnessCheckEverySeconds} s): {(_minFps.HasValue ? _minFps.Value.ToString("0.0") : "n/a")}",
        };
        lock (Lock) foreach (var (k, v) in Counts.OrderBy(k => k.Key)) lines.Add($"{k}: {v}");
        File.WriteAllLines(Path.Combine(_dir, "summary.txt"), lines);
    }

    static void Prune(string baseDir, int keep)
    {
        foreach (var d in new DirectoryInfo(baseDir).GetDirectories().OrderByDescending(d => d.Name).Skip(keep - 1))
            try { d.Delete(true); } catch { }
    }
}
