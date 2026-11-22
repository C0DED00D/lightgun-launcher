using System.Drawing.Drawing2D;

namespace LightgunLauncher;

/// <summary>Click-through, always-on-top window. Never takes focus.</summary>
class OverlayForm : Form
{
    protected readonly Color Key = Color.FromArgb(255, 0, 255);
    readonly System.Windows.Forms.Timer _topmost = new() { Interval = 1000 };

    public OverlayForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Key;
        TransparencyKey = Key;
        DoubleBuffered = true;
        Bounds = Screen.PrimaryScreen!.Bounds;
        // Games sometimes push themselves on top; keep re-asserting.
        _topmost.Tick += (_, _) => { if (Visible) BringToTop(); };
        _topmost.Start();
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_LAYERED | Native.WS_EX_TRANSPARENT | Native.WS_EX_TOOLWINDOW |
                          Native.WS_EX_NOACTIVATE | Native.WS_EX_TOPMOST;
            return cp;
        }
    }

    public void BringToTop() =>
        Native.SetWindowPos(Handle, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_SHOWWINDOW);

    protected override void Dispose(bool disposing)
    {
        if (disposing) _topmost.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>White frame around the screen edge that the Sinden camera tracks.</summary>
class BorderForm : OverlayForm
{
    readonly double _percent;
    Rectangle? _area;
    public BorderForm(double percent) { _percent = percent; }

    /// <summary>Draw the frame around this screen area instead of the whole screen (null = whole screen).
    /// The Sinden camera measures aim relative to the frame, so a frame around a 4:3 picture makes the gun
    /// report 0..100 % across the picture.</summary>
    public void SetArea(Rectangle? screenArea)
    {
        _area = screenArea;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        int t = Math.Max(4, (int)Math.Round(Height * _percent / 100.0));
        var r = _area is Rectangle a ? new Rectangle(a.X - Bounds.X, a.Y - Bounds.Y, a.Width, a.Height) : ClientRectangle;
        using var b = new SolidBrush(Color.White);
        e.Graphics.FillRectangle(b, r.X, r.Y, r.Width, t);
        e.Graphics.FillRectangle(b, r.X, r.Bottom - t, r.Width, t);
        e.Graphics.FillRectangle(b, r.X, r.Y, t, r.Height);
        e.Graphics.FillRectangle(b, r.Right - t, r.Y, t, r.Height);
    }
}

/// <summary>Plain black full-screen window behind 4:3 games, so the sides are black instead of the desktop.</summary>
class BackdropForm : Form
{
    public BackdropForm()
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Color.Black;
        Bounds = Screen.PrimaryScreen!.Bounds;
        Cursor = new Cursor(new Bitmap(1, 1).GetHicon());
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE;
            return cp;
        }
    }
}

/// <summary>"Hold to quit" banner with a fill bar.</summary>
class QuitForm : OverlayForm
{
    double _progress;

    public QuitForm()
    {
        var s = Screen.PrimaryScreen!.Bounds;
        int w = s.Width / 3, h = s.Height / 7;
        Bounds = new Rectangle(s.X + (s.Width - w) / 2, s.Y + (s.Height - h) / 2, w, h);
    }

    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public double Progress
    {
        get => _progress;
        set { _progress = Math.Clamp(value, 0, 1); Invalidate(); }
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        var r = ClientRectangle;
        using (var bg = new SolidBrush(Color.FromArgb(20, 20, 24)))
            g.FillRectangle(bg, r);
        using (var edge = new Pen(Color.FromArgb(230, 60, 60), Math.Max(3, Height / 40f)))
            g.DrawRectangle(edge, r.X + 2, r.Y + 2, r.Width - 5, r.Height - 5);

        using var font = new Font("Segoe UI", Height / 5f, FontStyle.Bold, GraphicsUnit.Pixel);
        var text = "HOLD TO QUIT";
        var size = g.MeasureString(text, font);
        g.DrawString(text, font, Brushes.White, (Width - size.Width) / 2, Height * 0.18f);

        float barH = Height * 0.14f, barW = Width * 0.8f, barX = (Width - barW) / 2, barY = Height * 0.68f;
        using (var track = new SolidBrush(Color.FromArgb(70, 70, 80)))
            g.FillRectangle(track, barX, barY, barW, barH);
        using (var fill = new SolidBrush(Color.FromArgb(230, 60, 60)))
            g.FillRectangle(fill, barX, barY, (float)(barW * _progress), barH);
    }
}
