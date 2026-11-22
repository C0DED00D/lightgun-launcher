using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace LightgunLauncher;

/// <summary>Full-screen game picker. Each Sinden gun gets its own reticle; the real mouse gets a white one.</summary>
class MenuForm : Form
{
    readonly LauncherConfig _cfg;
    readonly string _artDir;
    readonly List<Game> _games;
    readonly Dictionary<Game, Bitmap> _art = new(); // pre-scaled art per tile (art band size)
    readonly List<(Rectangle rect, Game? game, string kind)> _tiles = new(); // kind: "game", "recoil", "exit"
    readonly Dictionary<IntPtr, int> _gunByHandle = new(); // raw handle -> 1 or 2
    readonly PointF?[] _gunPos = new PointF?[3];
    readonly DateTime[] _gunSeen = new DateTime[3];
    /// <summary>For Session: when each gun last reported its aim, and where (screen fractions).</summary>
    public static readonly DateTime[] GunSeenUtc = new DateTime[3];
    public static readonly PointF[] GunAim = new PointF[3];
    /// <summary>Set by the launcher while a game with Game.TriggerKeys runs: gun -> virtual key for its trigger.</summary>
    public static volatile byte[]? TriggerKeys;
    static readonly bool[] _triggerKeyDown = new bool[3];

    /// <summary>Release any trigger key still held (game ended, or the gun stopped reporting mid-press).</summary>
    public static void ReleaseTriggerKeys()
    {
        var map = TriggerKeys;
        for (int g = 1; g <= 2; g++)
            if (_triggerKeyDown[g] && map != null && map[g] != 0) { SendTriggerKey(map[g], false); _triggerKeyDown[g] = false; }
    }

    static void SendTriggerKey(byte vk, bool down) =>
        Native.keybd_event(vk, (byte)Native.MapVirtualKey(vk, 0), down ? 0u : 2u, UIntPtr.Zero);
    readonly System.Windows.Forms.Timer _frame = new() { Interval = 16 };
    Point _mousePos;
    DateTime _mouseSeen = DateTime.MinValue;
    readonly DateTime _started = DateTime.UtcNow;
    bool _dirty = true;
    string _status = "";

    static readonly Color Bg1 = Color.FromArgb(12, 14, 22), Bg2 = Color.FromArgb(30, 18, 26);
    static readonly Color Accent = Color.FromArgb(230, 60, 60);
    static readonly Color[] GunColors = { Color.White, Color.FromArgb(255, 70, 70), Color.FromArgb(70, 150, 255) };

    public event Action<Game>? GameChosen;
    public event Action<string>? ExitChosen; // argument: why (for launcher.log)
    public event Action? RecoilToggled;

    /// <summary>True while the launcher restarts Sinden; EXIT and RECOIL ignore shots meanwhile.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RestartingSinden { get; set; }
    DateTime _exitArmedAt = DateTime.MinValue; // EXIT needs a second shot within 3 s, so a stray shot can't quit

    bool _recoilOn;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool RecoilOn { get => _recoilOn; set { _recoilOn = value; _dirty = true; } }

    public MenuForm(LauncherConfig cfg, string root)
    {
        _cfg = cfg;
        _artDir = Path.Combine(root, "config", "art");
        _games = cfg.Games;
        foreach (var (key, player) in cfg.PlayerKeys)
            if (Enum.TryParse<Keys>(key, true, out var k) && player is 1 or 2) _playerKeys[(int)k] = (player, key);
        Text = "Lightgun Launcher";
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        Bounds = Screen.PrimaryScreen!.Bounds;
        BackColor = Bg1;
        DoubleBuffered = true;
        KeyPreview = true;
        // Windows often refuses to give focus back after a game closes; staying on top keeps the menu visible anyway.
        // (The menu is hidden while a game runs, so it never covers a game.)
        TopMost = true;
        _frame.Tick += (_, _) => { if (_dirty) { _dirty = false; Invalidate(); } };
        _frame.Start();
        Layout();
    }

    public void SetStatus(string s) { _status = s; _dirty = true; }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        RefreshGunHandles();
        // Mice (gun aim) and keyboards (gun buttons: which gun sent Start/coin), also while a game runs.
        var dev = new[]
        {
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 2, dwFlags = Native.RIDEV_INPUTSINK | Native.RIDEV_DEVNOTIFY, hwndTarget = Handle },
            new Native.RAWINPUTDEVICE { usUsagePage = 1, usUsage = 6, dwFlags = Native.RIDEV_INPUTSINK, hwndTarget = Handle },
        };
        Native.RegisterRawInputDevices(dev, (uint)dev.Length, (uint)Marshal.SizeOf<Native.RAWINPUTDEVICE>());
    }

    readonly Dictionary<IntPtr, int> _gunKeyboardByHandle = new(); // raw keyboard handle -> 1 or 2

    public void RefreshGunHandles()
    {
        _gunByHandle.Clear();
        _gunKeyboardByHandle.Clear();
        var g = GunDevices.Detect(_cfg.P1Gun, _cfg.P2Gun);
        if (g.P1Mouse != null) _gunByHandle[g.P1Mouse.Handle] = 1;
        if (g.P2Mouse != null) _gunByHandle[g.P2Mouse.Handle] = 2;
        foreach (var d in RawDevices.List().Where(d => d.Type == Native.RIM_TYPEKEYBOARD))
        {
            if (d.Path.Contains(_cfg.P1Gun, StringComparison.OrdinalIgnoreCase)) _gunKeyboardByHandle[d.Handle] = 1;
            else if (d.Path.Contains(_cfg.P2Gun, StringComparison.OrdinalIgnoreCase)) _gunKeyboardByHandle[d.Handle] = 2;
        }
    }

    // Keys that belong to one player (games.json "playerKeys"). If a gun sends the other player's key, the gun
    // driver has the guns the wrong way round: log it and say so.
    readonly Dictionary<int, (int player, string name)> _playerKeys = new();
    public static int WrongGunKeyCount;
    readonly Dictionary<(int, ushort), DateTime> _lastKeyDown = new();

    string GunName(int gun) => gun == 1 ? _cfg.P1Name : _cfg.P2Name;

    void CheckGunKey(IntPtr device, ushort vk, ushort flags)
    {
        if ((flags & 1) != 0) return; // key up
        if (!_gunKeyboardByHandle.TryGetValue(device, out int gun)) return;
        if (!_playerKeys.TryGetValue(vk, out var pk))
        {
            Session.Count($"gun {gun} key {(Keys)vk}");
            return;
        }
        Session.Count($"gun {gun} key {pk.name}");
        if (pk.player == gun)
        {
            // Held keys auto-repeat: log and photograph only the first press.
            if (_lastKeyDown.TryGetValue((gun, vk), out var t) && (DateTime.UtcNow - t).TotalMilliseconds < 600) { _lastKeyDown[(gun, vk)] = DateTime.UtcNow; return; }
            _lastKeyDown[(gun, vk)] = DateTime.UtcNow;
            GameRunner.Log($"gun {gun} sent {pk.name} (correct)");
            Session.ShotSoon($"gun{gun}-key{pk.name}");
            return;
        }
        WrongGunKeyCount++;
        GameRunner.Log($"WRONG GUN KEY: gun {gun} sent {pk.name}, which belongs to player {pk.player}. The gun driver has the guns swapped.");
        BeginInvoke(() => SetStatus($"{GunName(gun)} sent player {pk.player}'s key {pk.name} – the guns are swapped"));
    }

    new void Layout()
    {
        _tiles.Clear();
        var items = _games.Select(g => ((Game?)g, "game")).ToList();
        if (_cfg.Menu.CalibrateTile) items.Add((null, "calibrate"));
        if (_cfg.Menu.RecoilTile && !string.IsNullOrWhiteSpace(_cfg.SindenConfig)) items.Add((null, "recoil"));
        items.Add((null, "exit"));
        int cols = Math.Max(1, _cfg.Menu.Columns), rows = (int)Math.Ceiling(items.Count / (double)cols);
        float margin = Height * 0.06f, top = Height * 0.15f, gap = Height * 0.016f;
        float w = (Width - 2 * margin - (cols - 1) * gap) / cols;
        float h = (Height - top - margin - (rows - 1) * gap) / rows;
        for (int i = 0; i < items.Count; i++)
        {
            int c = i % cols, r = i / cols;
            _tiles.Add((new Rectangle((int)(margin + c * (w + gap)), (int)(top + r * (h + gap)), (int)w, (int)h), items[i].Item1, items[i].Item2));
        }
        LoadArt();
    }

    /// <summary>Art band at the top of a tile.</summary>
    static Rectangle ArtRect(Rectangle tile) => new(tile.X, tile.Y, tile.Width, (int)(tile.Height * 0.56f));

    /// <summary>Load config\art\&lt;id&gt;.png for each game and pre-render it at the art band's size:
    /// a dimmed, cropped copy fills the band and the whole image sits on top, so wide logos are never cut off.</summary>
    void LoadArt()
    {
        foreach (var b in _art.Values) b.Dispose();
        _art.Clear();
        foreach (var (rect, game, _) in _tiles)
        {
            if (game == null) continue;
            var file = Path.Combine(_artDir, game.Id + ".png");
            if (!File.Exists(file)) continue;
            try
            {
                using var src = Image.FromFile(file);
                var band = ArtRect(rect);
                var bmp = new Bitmap(band.Width, band.Height);
                using (var g = Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    float cover = Math.Max(band.Width / (float)src.Width, band.Height / (float)src.Height);
                    float cw = src.Width * cover, ch = src.Height * cover;
                    g.DrawImage(src, (band.Width - cw) / 2, (band.Height - ch) / 2, cw, ch);
                    using (var dim = new SolidBrush(Color.FromArgb(170, 8, 8, 12))) g.FillRectangle(dim, 0, 0, band.Width, band.Height);
                    float fit = Math.Min(band.Width / (float)src.Width, band.Height / (float)src.Height);
                    float fw = src.Width * fit, fh = src.Height * fit;
                    g.DrawImage(src, (band.Width - fw) / 2, (band.Height - fh) / 2, fw, fh);
                }
                _art[game] = bmp;
            }
            catch (Exception ex) { GameRunner.Log($"art {game.Id}: {ex.Message}"); }
        }
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Cursor.Hide();
        Activate();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (Visible) { RefreshGunHandles(); _dirty = true; }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WM_INPUT) HandleRawInput(m.LParam);
        // A gun was plugged in or out: its device handle changes, so look the guns up again.
        else if (m.Msg == Native.WM_INPUT_DEVICE_CHANGE) RefreshGunHandles();
        base.WndProc(ref m);
    }

    void HandleRawInput(IntPtr hRaw)
    {
        uint size = 0, hdr = (uint)Marshal.SizeOf<Native.RAWINPUTHEADER>();
        Native.GetRawInputData(hRaw, Native.RID_INPUT, IntPtr.Zero, ref size, hdr);
        if (size == 0) return;
        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Native.GetRawInputData(hRaw, Native.RID_INPUT, buf, ref size, hdr) != size) return;
            var header = Marshal.PtrToStructure<Native.RAWINPUTHEADER>(buf);
            if (header.dwType == Native.RIM_TYPEKEYBOARD)
            {
                // RAWKEYBOARD: MakeCode, Flags, Reserved, VKey (ushort each).
                ushort make = (ushort)Marshal.ReadInt16(buf + (int)hdr);
                ushort flags = (ushort)Marshal.ReadInt16(buf + (int)hdr + 2);
                ushort vk = (ushort)Marshal.ReadInt16(buf + (int)hdr + 6);
                // Raw scan code + flags of gun keys, for the session log (some emulators decode keys from these).
                if (_gunKeyboardByHandle.TryGetValue(header.hDevice, out int kg) && (flags & 1) == 0)
                    Session.Write($"gun {kg} raw key vk=0x{vk:X2} make=0x{make:X2} flags=0x{flags:X}");
                CheckGunKey(header.hDevice, vk, flags);
                return;
            }
            if (header.dwType != Native.RIM_TYPEMOUSE) return;
            var mouse = Marshal.PtrToStructure<Native.RAWMOUSE>(buf + (int)hdr);
            // Trigger -> key for games that need it (Game.TriggerKeys): 0x1 = left down, 0x2 = left up.
            if (TriggerKeys is byte[] tk && _gunByHandle.TryGetValue(header.hDevice, out int tg) && tk[tg] != 0)
            {
                if ((mouse.usButtonFlags & 0x1) != 0 && !_triggerKeyDown[tg]) { SendTriggerKey(tk[tg], true); _triggerKeyDown[tg] = true; }
                if ((mouse.usButtonFlags & 0x2) != 0 && _triggerKeyDown[tg]) { SendTriggerKey(tk[tg], false); _triggerKeyDown[tg] = false; }
            }
            // Log each gun button press with where the gun pointed, so "the trigger doesn't work" can be told apart
            // from the game ignoring it. Down flags: 0x1 left (trigger), 0x4 right, 0x10 middle, 0x40/0x100 4/5.
            if (_gunByHandle.TryGetValue(header.hDevice, out int bg) && (mouse.usButtonFlags & 0x155) != 0)
            {
                var names = new List<string>();
                if ((mouse.usButtonFlags & 0x1) != 0) names.Add("left (trigger)");
                if ((mouse.usButtonFlags & 0x4) != 0) names.Add("right");
                if ((mouse.usButtonFlags & 0x10) != 0) names.Add("middle");
                if ((mouse.usButtonFlags & 0x40) != 0) names.Add("button 4");
                if ((mouse.usButtonFlags & 0x100) != 0) names.Add("button 5");
                string at = (mouse.usFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0 ? $" at {mouse.lLastX / 65535f:0.00},{mouse.lLastY / 65535f:0.00}" : "";
                GameRunner.Log($"gun {bg} mouse {string.Join(" + ", names)}{at}");
                foreach (var n in names) Session.Count($"gun {bg} {n}");
                Session.ShotSoon($"gun{bg}-{names[0].Split(' ')[0]}");
            }
            if (_gunByHandle.TryGetValue(header.hDevice, out int gun) && (mouse.usFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0)
            {
                var area = (mouse.usFlags & Native.MOUSE_VIRTUAL_DESKTOP) != 0 ? SystemInformation.VirtualScreen : Screen.PrimaryScreen!.Bounds;
                var sx = area.X + mouse.lLastX / 65535f * area.Width;
                var sy = area.Y + mouse.lLastY / 65535f * area.Height;
                var pt = PointToClient(new Point((int)sx, (int)sy));
                if (_gunPos[gun] == null)
                {
                    // First report since the launcher started: log how long the gun took to wake up (the Sinden
                    // camera can take several seconds per gun).
                    GameRunner.Log($"gun {gun} first position report {(DateTime.UtcNow - _started).TotalSeconds:0.0} s after the menu opened");
                    _dirty = true;
                }
                _gunPos[gun] = pt;
                _gunSeen[gun] = DateTime.UtcNow;
                GunSeenUtc[gun] = DateTime.UtcNow;
                GunAim[gun] = new PointF(mouse.lLastX / 65535f, mouse.lLastY / 65535f);
                _dirty = true;
            }
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mousePos = e.Location;
        // Only show the plain mouse reticle when no gun moved the cursor just now.
        if ((DateTime.UtcNow - _gunSeen.Skip(1).Max()).TotalMilliseconds > 150) _mouseSeen = DateTime.UtcNow;
        _dirty = true;
    }

    // ---- Gun calibration page ----
    // A target at the exact screen centre for Sinden's own calibration (hold D-pad left 3 s, then shoot the centre;
    // Sinden saves it on the gun). Each gun's live reticle and its distance from the centre show the result.
    bool _calibrating;

    /// <summary>DONE button, in the bottom-right corner so calibration shots at the centre can't hit it.</summary>
    Rectangle CalibrationDoneRect()
    {
        int w = (int)(Height * 0.22f), h = (int)(Height * 0.1f), m = (int)(Height * 0.07f);
        return new Rectangle(Width - m - w, Height - m - h, w, h);
    }

    void OpenCalibration()
    {
        _calibrating = true;
        RefreshGunHandles();
        _dirty = true;
    }

    void PaintCalibration(Graphics g)
    {
        float unit = Height / 100f;
        using (var bg = new SolidBrush(Color.FromArgb(14, 16, 24))) g.FillRectangle(bg, ClientRectangle);
        float cx = Width / 2f, cy = Height / 2f;

        // Target: rings plus a crosshair whose lines stop short of the middle, so the exact centre stays visible.
        using (var ring = new Pen(Color.FromArgb(235, 235, 240), unit * 0.25f))
            foreach (var r in new[] { unit * 3f, unit * 9f, unit * 16f })
                g.DrawEllipse(ring, cx - r, cy - r, 2 * r, 2 * r);
        using (var line = new Pen(Color.FromArgb(235, 235, 240), unit * 0.18f))
        {
            float a = unit * 1.2f, b = unit * 20f;
            g.DrawLine(line, cx - b, cy, cx - a, cy); g.DrawLine(line, cx + a, cy, cx + b, cy);
            g.DrawLine(line, cx, cy - b, cx, cy - a); g.DrawLine(line, cx, cy + a, cx, cy + b);
        }
        using (var dot = new SolidBrush(Color.FromArgb(255, 70, 70)))
            g.FillEllipse(dot, cx - unit * 0.45f, cy - unit * 0.45f, unit * 0.9f, unit * 0.9f);

        using var title = new Font("Segoe UI", unit * 4.2f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var body = new Font("Segoe UI", unit * 2.6f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var dim = new SolidBrush(Color.FromArgb(185, 190, 205));
        float x0 = Height * 0.07f, y = Height * 0.06f;
        g.DrawString("GUN CALIBRATION", title, Brushes.White, x0, y);
        y += title.Height * 1.25f;
        foreach (var line in _cfg.Menu.CalibrationHelp)
        {
            g.DrawString(line, body, dim, x0, y);
            y += body.Height * 1.35f;
        }

        // Per-gun status: connected?, and how far its reticle is from the centre (in % of the screen).
        var now = DateTime.UtcNow;
        float sy = Height - Height * 0.07f - body.Height * 2.8f;
        for (int i = 1; i <= 2; i++)
        {
            string name = GunName(i);
            string text;
            if (!_gunByHandle.ContainsValue(i)) text = $"{name}: not connected – replug its USB cable";
            else if (_gunPos[i] is PointF p && (now - _gunSeen[i]).TotalSeconds < 5)
            {
                float dx = (p.X - cx) / Width * 100f, dy = (p.Y - cy) / Height * 100f;
                string h = Math.Abs(dx) < 0.5f ? "centred" : $"{Math.Abs(dx):0.0}% {(dx < 0 ? "left" : "right")}";
                string v = Math.Abs(dy) < 0.5f ? "centred" : $"{Math.Abs(dy):0.0}% {(dy < 0 ? "high" : "low")}";
                text = $"{name}: across {h}, up/down {v}";
            }
            else text = $"{name}: aim it at the screen";
            using var gb = new SolidBrush(GunColors[i]);
            g.DrawString(text, body, gb, x0, sy);
            sy += body.Height * 1.35f;
        }

        // DONE button.
        var done = CalibrationDoneRect();
        bool hot = Pointers().Any(p => done.Contains(Point.Round(p)));
        using (var path = Rounded(done, unit * 1.2f))
        {
            using (var b = new SolidBrush(hot ? Color.FromArgb(70, 30, 36) : Color.FromArgb(34, 36, 48))) g.FillPath(b, path);
            using (var pn = new Pen(hot ? Accent : Color.FromArgb(60, 64, 80), hot ? unit * 0.35f : unit * 0.12f)) g.DrawPath(pn, path);
        }
        var ds = g.MeasureString("DONE", title);
        g.DrawString("DONE", title, Brushes.White, done.X + (done.Width - ds.Width) / 2, done.Y + (done.Height - ds.Height) / 2);

        for (int i = 1; i <= 2; i++)
            if (_gunPos[i] is PointF p && (now - _gunSeen[i]).TotalSeconds < 30) DrawReticle(g, p, GunColors[i], unit);
        if ((now - _mouseSeen).TotalSeconds < 30) DrawReticle(g, _mousePos, GunColors[0], unit * 0.8f);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        if (_calibrating)
        {
            // Only DONE reacts; every other shot (the calibration shot at the centre) is ignored.
            if (CalibrationDoneRect().Contains(e.Location)) { _calibrating = false; SetStatus(""); _dirty = true; }
            return;
        }
        foreach (var (rect, game, kind) in _tiles)
        {
            if (!rect.Contains(e.Location)) continue;
            if (kind == "exit")
            {
                if (RestartingSinden) return;
                if ((DateTime.UtcNow - _exitArmedAt).TotalSeconds <= 3) { ExitChosen?.Invoke("EXIT tile shot twice"); return; }
                _exitArmedAt = DateTime.UtcNow;
                SetStatus("Shoot EXIT again to quit");
                return;
            }
            if (kind == "recoil") { if (!RestartingSinden) RecoilToggled?.Invoke(); return; }
            if (kind == "calibrate") { OpenCalibration(); return; }
            if (game == null) return;
            if (game.MissingReason != null) { SetStatus($"{game.Title}: {game.MissingReason}"); return; }
            GameChosen?.Invoke(game);
            return;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Escape) ExitChosen?.Invoke("Escape key");
    }

    IEnumerable<PointF> Pointers()
    {
        var now = DateTime.UtcNow;
        for (int g = 1; g <= 2; g++)
            if (_gunPos[g] is PointF p && (now - _gunSeen[g]).TotalSeconds < 30) yield return p;
        if ((now - _mouseSeen).TotalSeconds < 30) yield return _mousePos;
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        if (_calibrating) { PaintCalibration(g); return; }
        using (var bg = new LinearGradientBrush(ClientRectangle, Bg1, Bg2, 60f))
            g.FillRectangle(bg, ClientRectangle);

        float unit = Height / 100f;
        using (var titleFont = new Font("Segoe UI", unit * 5.2f, FontStyle.Bold, GraphicsUnit.Pixel))
            g.DrawString(_cfg.Menu.Title, titleFont, Brushes.White, Height * 0.06f, Height * 0.045f);
        using (var sub = new Font("Segoe UI", unit * 1.6f, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var subBrush = new SolidBrush(Color.FromArgb(170, 175, 190)))
        {
            g.DrawString(_cfg.Menu.Subtitle, sub, subBrush, Height * 0.065f, Height * 0.105f);
            // A connected gun that hasn't reported a position yet is still starting (Sinden camera).
            var waking = new List<string>();
            for (int i = 1; i <= 2; i++)
                if (_gunByHandle.ContainsValue(i) && _gunPos[i] == null && (DateTime.UtcNow - _started).TotalSeconds < 120)
                    waking.Add(GunName(i));
            var status = _status.Length > 0 ? _status : waking.Count > 0 ? string.Join(" and ", waking) + " starting up – aim at the screen…" : "";
            if (waking.Count > 0) _dirty = true;
            if (status.Length > 0)
            {
                var sz = g.MeasureString(status, sub);
                using var amber = new SolidBrush(Color.FromArgb(255, 190, 80));
                g.DrawString(status, sub, amber, Width - Height * 0.06f - sz.Width, Height * 0.105f);
            }
        }

        var pointers = Pointers().ToList();
        using var tFont = new Font("Segoe UI", unit * 2.1f, FontStyle.Bold, GraphicsUnit.Pixel);
        using var sFont = new Font("Segoe UI", unit * 1.35f, FontStyle.Regular, GraphicsUnit.Pixel);
        using var nFont = new Font("Segoe UI", unit * 1.25f, FontStyle.Italic, GraphicsUnit.Pixel);
        foreach (var (rect, game, kind) in _tiles)
        {
            bool hot = pointers.Any(p => rect.Contains(Point.Round(p)));
            bool missing = game?.MissingReason != null;
            using var path = Rounded(rect, unit * 1.2f);
            var fill = missing ? Color.FromArgb(26, 27, 34) : hot ? Color.FromArgb(70, 30, 36) : Color.FromArgb(34, 36, 48);
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            using (var p = new Pen(hot ? Accent : Color.FromArgb(60, 64, 80), hot ? unit * 0.35f : unit * 0.12f)) g.DrawPath(p, path);

            var pad = unit * 1.4f;
            var inner = new RectangleF(rect.X + pad, rect.Y + pad, rect.Width - 2 * pad, rect.Height - 2 * pad);
            if (kind == "calibrate")
            {
                var label = "CALIBRATE";
                var sub2 = "GUN AIM";
                using var bigFont = new Font("Segoe UI", unit * 2.8f, FontStyle.Bold, GraphicsUnit.Pixel);
                var ls = g.MeasureString(sub2, sFont);
                var bs = g.MeasureString(label, bigFont);
                float y0 = rect.Y + (rect.Height - ls.Height - bs.Height) / 2;
                using (var lb = new SolidBrush(Color.FromArgb(170, 175, 190)))
                    g.DrawString(sub2, sFont, lb, rect.X + (rect.Width - ls.Width) / 2, y0);
                g.DrawString(label, bigFont, Brushes.White, rect.X + (rect.Width - bs.Width) / 2, y0 + ls.Height);
                continue;
            }
            if (kind == "exit")
            {
                var sz = g.MeasureString("EXIT", tFont);
                g.DrawString("EXIT", tFont, Brushes.White, rect.X + (rect.Width - sz.Width) / 2, rect.Y + (rect.Height - sz.Height) / 2);
                continue;
            }
            if (kind == "recoil")
            {
                // Big ON/OFF state with a caption, so it reads from across the room.
                var label = "GUN RECOIL";
                var state = _recoilOn ? "ON" : "OFF";
                using var stateFont = new Font("Segoe UI", unit * 3.6f, FontStyle.Bold, GraphicsUnit.Pixel);
                var ls = g.MeasureString(label, sFont);
                var ss = g.MeasureString(state, stateFont);
                float total = ls.Height + ss.Height;
                float y0 = rect.Y + (rect.Height - total) / 2;
                using (var lb = new SolidBrush(Color.FromArgb(170, 175, 190)))
                    g.DrawString(label, sFont, lb, rect.X + (rect.Width - ls.Width) / 2, y0);
                using (var sb2 = new SolidBrush(_recoilOn ? Color.FromArgb(120, 220, 140) : Color.FromArgb(150, 152, 165)))
                    g.DrawString(state, stateFont, sb2, rect.X + (rect.Width - ss.Width) / 2, y0 + ls.Height);
                continue;
            }
            if (game == null) continue;
            // Art band on top (clipped to the rounded tile), text below.
            var band = ArtRect(rect);
            if (_art.TryGetValue(game, out var art))
            {
                var clip = g.Clip;
                g.SetClip(path);
                g.DrawImageUnscaled(art, band.X, band.Y);
                if (missing)
                    using (var veil = new SolidBrush(Color.FromArgb(185, 20, 20, 26))) g.FillRectangle(veil, band);
                g.Clip = clip;
                using (var p = new Pen(hot ? Accent : Color.FromArgb(60, 64, 80), hot ? unit * 0.35f : unit * 0.12f)) g.DrawPath(p, path);
            }
            else
                band = new Rectangle(rect.X, rect.Y, rect.Width, 0);

            var oneLine = new StringFormat(StringFormatFlags.NoWrap) { Trimming = StringTrimming.EllipsisCharacter };
            float y = band.Bottom + unit * 0.5f;
            using (var tb = new SolidBrush(missing ? Color.FromArgb(110, 112, 125) : Color.White))
                g.DrawString(game.Title, tFont, tb, new RectangleF(inner.X, y, inner.Width, tFont.Height * 1.1f), band.Height > 0 ? oneLine : null);
            y += band.Height > 0 ? tFont.Height * 1.05f : g.MeasureString(game.Title, tFont, (int)inner.Width).Height;
            var info = string.Join("  ·  ", new[] { game.Year, game.Players == 1 ? "1 player" : $"{game.Players} players", game.TileEmulator }
                .Where(s => !string.IsNullOrEmpty(s)));
            using (var sb = new SolidBrush(Color.FromArgb(140, 145, 165)))
                g.DrawString(info, sFont, sb, new RectangleF(inner.X, y, inner.Width, unit * 4.5f), band.Height > 0 ? oneLine : null);
            // Only problems get an extra line (e.g. a missing file).
            var note = missing ? game.MissingReason : null;
            if (!string.IsNullOrEmpty(note))
            {
                using var nb = new SolidBrush(missing ? Color.FromArgb(255, 170, 70) : Color.FromArgb(120, 190, 140));
                var noteRect = band.Height > 0
                    ? new RectangleF(inner.X, y + sFont.Height * 1.15f, inner.Width, nFont.Height * 1.2f)
                    : new RectangleF(inner.X, inner.Bottom - nFont.Height * 1.1f, inner.Width, nFont.Height * 1.2f);
                g.DrawString(note, nFont, nb, noteRect, oneLine);
            }
        }

        // Reticles: P1 red, P2 blue, real mouse white.
        var now = DateTime.UtcNow;
        for (int i = 1; i <= 2; i++)
            if (_gunPos[i] is PointF p && (now - _gunSeen[i]).TotalSeconds < 30) DrawReticle(g, p, GunColors[i], unit);
        if ((now - _mouseSeen).TotalSeconds < 30) DrawReticle(g, _mousePos, GunColors[0], unit * 0.8f);
    }

    static void DrawReticle(Graphics g, PointF p, Color c, float unit)
    {
        float r = unit * 2.2f;
        using var shadow = new Pen(Color.FromArgb(160, 0, 0, 0), unit * 0.6f);
        using var pen = new Pen(c, unit * 0.32f);
        foreach (var pn in new[] { shadow, pen })
        {
            g.DrawEllipse(pn, p.X - r, p.Y - r, 2 * r, 2 * r);
            g.DrawLine(pn, p.X - r * 1.5f, p.Y, p.X - r * 0.4f, p.Y);
            g.DrawLine(pn, p.X + r * 0.4f, p.Y, p.X + r * 1.5f, p.Y);
            g.DrawLine(pn, p.X, p.Y - r * 1.5f, p.X, p.Y - r * 0.4f);
            g.DrawLine(pn, p.X, p.Y + r * 0.4f, p.X, p.Y + r * 1.5f);
        }
        using var dot = new SolidBrush(c);
        g.FillEllipse(dot, p.X - unit * 0.3f, p.Y - unit * 0.3f, unit * 0.6f, unit * 0.6f);
    }

    static GraphicsPath Rounded(Rectangle r, float rad)
    {
        var p = new GraphicsPath();
        float d = rad * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _frame.Dispose(); foreach (var b in _art.Values) b.Dispose(); }
        base.Dispose(disposing);
    }
}
