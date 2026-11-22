using System.Text.Json;
using System.Text.Json.Serialization;

namespace LightgunLauncher;

/// <summary>Everything in config\games.json. See docs\configuration.md for every option.</summary>
class LauncherConfig
{
    /// <summary>Text that identifies the player 1 / player 2 gun in its raw-input device path (USB VID/PID).</summary>
    public string P1Gun { get; set; } = "VID_16C0&PID_0F02";
    public string P2Gun { get; set; } = "VID_16C0&PID_0F01";
    /// <summary>Names shown in the menu and status messages.</summary>
    public string P1Name { get; set; } = "Gun 1";
    public string P2Name { get; set; } = "Gun 2";
    /// <summary>The gun driver (e.g. Sinden's Lightgun.exe). Null = the launcher doesn't start or stop it.</summary>
    public ProcessSpec? Sinden { get; set; }
    /// <summary>Sinden settings file (Lightgun.exe.Config) that the launcher may edit. Null/empty = never edited.</summary>
    public string? SindenConfig { get; set; }
    /// <summary>appSettings written into SindenConfig before Sinden starts, e.g. to pin which gun is slot A / B and
    /// which keys each button sends. Key = setting name, value = setting value.</summary>
    public Dictionary<string, string> SindenSettings { get; set; } = new();
    /// <summary>Key the guns send for "hold to quit" (a Windows key name, e.g. "F11").</summary>
    public string QuitKey { get; set; } = "F11";
    public int QuitHoldMs { get; set; } = 3000;
    /// <summary>While the quit key is held, the progress (0..1) is written to this file, for in-game mods that draw
    /// their own banner when the launcher's overlay can't show. Environment variables are expanded. Empty = off.</summary>
    public string? QuitProgressFile { get; set; } = "%TEMP%\\lightgun_quit.txt";
    /// <summary>White border thickness as a percentage of screen height.</summary>
    public double BorderPercent { get; set; } = 2.0;
    /// <summary>
    /// Keys that belong to one player, for the "wrong gun" check: when gun 1 sends a key listed for player 2 (or the
    /// other way round), the launcher logs it and warns in the menu. Key = Windows key name, value = player (1 or 2).
    /// </summary>
    public Dictionary<string, int> PlayerKeys { get; set; } = new();
    /// <summary>Screen capture helper (tools\dupshot) used for session screenshots and frame-rate checks.</summary>
    public string ScreenCaptureTool { get; set; } = "tools\\bin\\dupshot\\dupshot.exe";
    public MenuSettings Menu { get; set; } = new();
    /// <summary>Session recordings (app\sessions), see Session.cs.</summary>
    public RecordingSettings Recording { get; set; } = new();
    public List<Game> Games { get; set; } = new();
}

class MenuSettings
{
    public string Title { get; set; } = "LIGHTGUN ARCADE";
    public string Subtitle { get; set; } = "Shoot a game to play  ·  In game, hold the quit button for 3 seconds to quit";
    public int Columns { get; set; } = 5;
    /// <summary>Show the GUN RECOIL on/off tile (needs sindenConfig).</summary>
    public bool RecoilTile { get; set; } = true;
    /// <summary>Show the CALIBRATE tile (a centre target for the gun's own calibration).</summary>
    public bool CalibrateTile { get; set; } = true;
    /// <summary>Lines shown on the calibration page.</summary>
    public List<string> CalibrationHelp { get; set; } = new()
    {
        "1.  Hold D-pad LEFT on one gun for 3 seconds.",
        "2.  Aim at the red dot in the middle and pull the trigger once.",
        "     (Pump instead of the trigger cancels.)",
        "3.  Check: that gun's reticle should now sit on the dot.",
        "Do one gun at a time. It is saved on the gun itself.",
    };
}

class RecordingSettings
{
    /// <summary>Record each game at all (event log, emulator logs, summary). Off = no sessions folder.</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>Screenshot every N seconds while a game runs (0 = none).</summary>
    public int ScreenshotEverySeconds { get; set; } = 5;
    /// <summary>Screenshot just after each gun button/key press.</summary>
    public bool ScreenshotOnButton { get; set; } = true;
    /// <summary>Every N seconds, measure the frame rate and new pictures per second for a few seconds (0 = never).</summary>
    public int SmoothnessCheckEverySeconds { get; set; } = 30;
    /// <summary>Copy emulator logs (emulatorLogs + each game's logs) into the session folder when the game ends.</summary>
    public bool CopyEmulatorLogs { get; set; } = true;
    /// <summary>How many session folders to keep (oldest removed first).</summary>
    public int KeepSessions { get; set; } = 25;
    /// <summary>Emulator log files (relative to the launcher root) copied for every game, if written during it.</summary>
    public List<string> EmulatorLogs { get; set; } = new();
}

class ProcessSpec
{
    public string Exe { get; set; } = "";
    public string Args { get; set; } = "";
    public string? Cwd { get; set; }
    /// <summary>Wait this long after starting before launching the next thing.</summary>
    public int DelayMs { get; set; }
    public bool Hidden { get; set; }
    /// <summary>Extra environment variables, e.g. __COMPAT_LAYER=HighDpiAware to stop Windows DPI-scaling old emulators.</summary>
    public Dictionary<string, string>? Env { get; set; }
    /// <summary>Start with admin rights (Windows asks). Avoid it: most gun hooks work without admin when the
    /// emulator also runs as a normal program.</summary>
    public bool Elevate { get; set; }
}

class KeyTap
{
    public int DelayMs { get; set; }
    /// <summary>Windows key name, e.g. "Space".</summary>
    public string Key { get; set; } = "Space";
    /// <summary>Move the mouse pointer to the screen center first (e.g. a calibration shot needs the aim on screen).</summary>
    public bool Center { get; set; }
}

class TemplateSpec
{
    /// <summary>Source and destination (relative to the launcher root). Both may contain {PLACEHOLDERS}.</summary>
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Copy the file as-is (no placeholder replacement), e.g. per-game save data.</summary>
    public bool Binary { get; set; }
}

class Game
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public string System { get; set; } = "";
    public string Emulator { get; set; } = "";
    public string? Note { get; set; }
    /// <summary>Shown on the menu tile: release year, player count, platform.</summary>
    public string Year { get; set; } = "";
    public int Players { get; set; } = 1;
    public string Platform { get; set; } = "Arcade";
    /// <summary>Short emulator name for the menu tile. Defaults to Emulator up to any " + ".</summary>
    public string? MenuEmulator { get; set; }
    [JsonIgnore] public string TileEmulator => MenuEmulator ?? Emulator.Split(" + ")[0];
    public ProcessSpec Run { get; set; } = new();
    /// <summary>Programs started before the emulator (gun hooks, memory patchers) and closed after it.</summary>
    public List<ProcessSpec> Helpers { get; set; } = new();
    public List<TemplateSpec> Templates { get; set; } = new();
    /// <summary>Files that must exist (relative to the launcher root). '*' wildcards are allowed in the file name.</summary>
    public List<string> Requires { get; set; } = new();
    /// <summary>Optional check command; a non-zero exit code marks the game as not ready.</summary>
    public ProcessSpec? Verify { get; set; }
    public string? VerifyMessage { get; set; }

    /// <summary>Force the emulator's window to cover the screen without a frame.</summary>
    public bool Borderless { get; set; }
    /// <summary>With Borderless: "4:3" centers the window at full height on a black backdrop instead of filling the screen.</summary>
    public string? Aspect { get; set; }
    /// <summary>Keep a 1-px border on the game window (and with Borderless, stop 1 px short of the screen bottom).
    /// Windows sends a frameless window that covers the whole screen straight to the display ("independent
    /// flip"), which hides the launcher's white border and quit banner.</summary>
    public bool KeepComposited { get; set; }
    /// <summary>With Borderless: also remove the window's menu bar and stretch its largest child window (the render
    /// surface) over the whole client area. For emulators that keep their render child at the old size when the
    /// frame is resized, while a gun hook maps the gun over that child.</summary>
    public bool FitChildWindow { get; set; }
    /// <summary>With Borderless: wait this long after the game window first appears before resizing it. For games
    /// that size their 3D picture from the window when they start; they then keep their own resolution and Windows
    /// scales it up, so the picture and the gun mapping agree.</summary>
    public int BorderlessDelayMs { get; set; }
    /// <summary>With Borderless: shrink the game window by the white border's thickness (same aspect), so the border
    /// is drawn beside the game instead of over it. For games the graphics driver puts on their own display layer.</summary>
    public bool BorderInset { get; set; }
    /// <summary>With Borderless: for the whole game, bring the game window back to the front whenever another program's
    /// window (a loader console, the front-end, the desktop) has taken the foreground. Guns can't alt-tab.</summary>
    public bool KeepForeground { get; set; }
    /// <summary>With Borderless: minimize + restore the game window once, 2 s after it appears. Some full-screen
    /// games come up see-through until they redraw.</summary>
    public bool RefreshWindowOnStart { get; set; }
    /// <summary>Process name (without .exe) whose window is the game, when the emulator is a front-end that starts it.</summary>
    public string? WindowProcess { get; set; }
    /// <summary>"overlay" (launcher draws the white border), "emulator" (emulator/game draws it), or "none".</summary>
    public string Border { get; set; } = "overlay";
    /// <summary>"4:3": draw the overlay border around the centred 4:3 picture instead of the screen edges. The Sinden
    /// camera measures aim relative to the border, so the gun then reports 0..100 % across the picture itself
    /// (for gun hooks that map the gun over the whole screen).</summary>
    public string? BorderArea { get; set; }

    /// <summary>Keys tapped into the emulator after launch, e.g. to dismiss a warning or pass a boot calibration.</summary>
    public List<KeyTap> PostLaunchKeys { get; set; } = new();
    /// <summary>Extra process names (without .exe) to close when the game ends.</summary>
    public List<string> Kill { get; set; } = new();
    /// <summary>How long to wait for the emulator to close by itself (saving its files) before it is killed.</summary>
    public int CloseWaitMs { get; set; } = 4000;
    /// <summary>Key the quit button also presses in this game, e.g. "Down" for menus. Holding it for quitHoldMs still
    /// quits. Null = the quit key never reaches the game.</summary>
    public string? QuitKeyAlso { get; set; }
    /// <summary>Gun trigger as a key: {"1": "F7", "2": "F6"} = while this game runs, the launcher presses/releases that
    /// key with each gun's trigger (left click). For emulators that ignore the gun's mouse button but take keys.</summary>
    public Dictionary<string, string>? TriggerKeys { get; set; }
    /// <summary>Gun keys the launcher swallows and sends again as software key presses (e.g. "D1"). For front-ends
    /// that bind "a key from software" rather than a specific keyboard device (TeknoParrot DevicePath "null").</summary>
    public List<string> RelayKeys { get; set; } = new();
    /// <summary>Like RelayKeys, but the real key still reaches the game: the launcher only sends an extra software copy.</summary>
    public List<string> CopyKeys { get; set; } = new();
    /// <summary>Files (relative to the launcher root) deleted before each launch, e.g. a front-end's cached bindings
    /// that would otherwise override the ones the launcher writes.</summary>
    public List<string> DeleteBeforeLaunch { get; set; } = new();
    /// <summary>Extra emulator log files for this game, copied into its session recording.</summary>
    public List<string> Logs { get; set; } = new();

    [JsonIgnore] public string? MissingReason { get; set; }
}

/// <summary>Choices made in the menu, kept in config\settings.json between runs.</summary>
class UserSettings
{
    public bool Recoil { get; set; }

    public static UserSettings Load(string path)
    {
        try { return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new(); }
        catch { return new(); }
    }

    public void Save(string path)
    {
        try { File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true })); }
        catch (Exception ex) { GameRunner.Log("settings save: " + ex.Message); }
    }
}

static class ConfigStore
{
    static readonly JsonSerializerOptions Opts = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
    };

    public static LauncherConfig Load(string path) =>
        JsonSerializer.Deserialize<LauncherConfig>(File.ReadAllText(path), Opts) ?? new LauncherConfig();
}
