using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace SoftbodyManager;

/// <summary>
/// 1.1.0: the window's colours. Dark by default (no blinding white), light on request; the choice is remembered for this
/// Windows user (%LOCALAPPDATA%\SoftbodyManager\theme.txt). The controls below read <see cref="Current"/> when they paint.
/// </summary>
sealed class Theme
{
    public bool Dark;
    public Color Window, Card, Border, Text, Muted, Accent, AccentHover, AccentText, Button, ButtonHover, Success, Warning, Danger, Input, SwitchOff;

    public static readonly Theme DarkTheme = new()
    {
        Dark = true, Window = C(0x1B1C20), Card = C(0x25272C), Border = C(0x35383F), Text = C(0xE9EAEC), Muted = C(0x9CA1A9),
        Accent = C(0x7867F0), AccentHover = C(0x8A7BF3), AccentText = Color.White, Button = C(0x33363D), ButtonHover = C(0x3E4149),
        Success = C(0x4CC38A), Warning = C(0xF0B35A), Danger = C(0xF0706A), Input = C(0x1F2125), SwitchOff = C(0x4B4E57),
    };
    public static readonly Theme LightTheme = new()
    {
        Dark = false, Window = C(0xF1F2F4), Card = Color.White, Border = C(0xDADDE2), Text = C(0x1D2025), Muted = C(0x5C626B),
        Accent = C(0x5A49DD), AccentHover = C(0x6A5BE3), AccentText = Color.White, Button = C(0xE7E9ED), ButtonHover = C(0xDCDFE4),
        Success = C(0x1D8752), Warning = C(0xA35F00), Danger = C(0xBF3A30), Input = C(0xF8F9FA), SwitchOff = C(0xC2C6CD),
    };

    public static Theme Current = Load();

    static Color C(int rgb) => Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);

    static string ChoiceFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoftbodyManager", "theme.txt");

    static Theme Load()
    {
        try { if (File.Exists(ChoiceFile) && File.ReadAllText(ChoiceFile).Trim() == "light") return LightTheme; } catch { }
        return DarkTheme;
    }

    public static void Remember(Theme t)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(ChoiceFile)); File.WriteAllText(ChoiceFile, t.Dark ? "dark" : "light"); } catch { }
    }

    /// <summary>A colour between two (0: <paramref name="a"/>, 1: <paramref name="b"/>).</summary>
    public static Color Mix(Color a, Color b, float k) =>
        Color.FromArgb(255, (int)(a.R + (b.R - a.R) * k), (int)(a.G + (b.G - a.G) * k), (int)(a.B + (b.B - a.B) * k));

    // ------------------------------------------------------------------ the parts Windows draws

    [DllImport("dwmapi.dll")] static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)] static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>A dark (or light) title bar (Windows 10 1809 and later; elsewhere nothing happens).</summary>
    public static void TitleBar(Form f, bool dark)
    {
        try
        {
            if (!f.IsHandleCreated) return;
            int on = dark ? 1 : 0;
            if (DwmSetWindowAttribute(f.Handle, 20, ref on, 4) != 0) DwmSetWindowAttribute(f.Handle, 19, ref on, 4);   // 19: before Windows 10 20H1
            SetWindowPos(f.Handle, IntPtr.Zero, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0004 | 0x0010 | 0x0020);   // redraw the frame now
        }
        catch { }
    }

    /// <summary>Dark (or light) scroll bars on a control that has them.</summary>
    public static void ScrollBars(Control c, bool dark)
    {
        try { if (c.IsHandleCreated) SetWindowTheme(c.Handle, dark ? "DarkMode_Explorer" : "Explorer", null); } catch { }
    }

    public static GraphicsPath Round(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
        if (d <= 0) { p.AddRectangle(r); return p; }
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    /// <summary>A length in pixels for this control's screen (96 dpi: as written).</summary>
    public static float Px(Control c, float at96) => at96 * c.DeviceDpi / 96f;
}

/// <summary>1.1.0: a rounded card in the theme's card colour, holding its rows top to bottom (one column).</summary>
sealed class Card : TableLayoutPanel
{
    /// <summary>A coloured edge (the update banner), else the theme's border.</summary>
    public Color? Edge;
    /// <summary>1.2.0: filled with a tint of the theme's warning colour (on an unstable build), else the card colour.</summary>
    public bool Tinted;
    /// <summary>What the card is filled with (what its controls sit on).</summary>
    public Color FillColor => Tinted ? Theme.Mix(Theme.Current.Card, Theme.Current.Warning, Theme.Current.Dark ? 0.14f : 0.11f) : Theme.Current.Card;

    public Card()
    {
        ColumnCount = 1;
        ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Dock = DockStyle.Fill;
        Padding = new Padding(16, 12, 16, 12);
        Margin = new Padding(0, 0, 0, 12);
        DoubleBuffered = true;
        ResizeRedraw = true;
    }

    public void Add(Control c) { c.Dock = DockStyle.Fill; Controls.Add(c); }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        var t = Theme.Current;
        e.Graphics.Clear(BackColor);   // what the card sits on
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float edge = Edge != null ? Theme.Px(this, 2f) : 1f;
        var r = new RectangleF(edge / 2, edge / 2, Width - edge - 1, Height - edge - 1);
        using var path = Theme.Round(r, Theme.Px(this, 10f));
        using var fill = new SolidBrush(FillColor);
        e.Graphics.FillPath(fill, path);
        using var pen = new Pen(Edge ?? t.Border, edge);
        e.Graphics.DrawPath(pen, path);
    }
}

enum ButtonKind { Primary, Secondary, Quiet }

/// <summary>1.1.0: a rounded flat button in the theme's colours (still a Button: keyboard, focus and screen readers as usual).</summary>
sealed class PillButton : Button
{
    public ButtonKind Kind = ButtonKind.Secondary;
    bool _hover, _down;

    public PillButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12, 5, 12, 5);
        MinimumSize = new Size(0, 34);
        Margin = new Padding(0, 3, 8, 3);
        Cursor = Cursors.Hand;
        UseMnemonic = false;
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; _down = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { _down = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { _down = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color fill, text;
        switch (Kind)
        {
            case ButtonKind.Primary: fill = _hover ? t.AccentHover : t.Accent; text = t.AccentText; break;
            case ButtonKind.Quiet: fill = _hover ? t.ButtonHover : BackColor; text = _hover ? t.Text : t.Muted; break;
            default: fill = _hover ? t.ButtonHover : t.Button; text = t.Text; break;
        }
        if (_down && Enabled) fill = Theme.Mix(fill, t.Dark ? Color.Black : Color.Gray, 0.15f);
        if (!Enabled)
        {
            fill = Kind == ButtonKind.Quiet ? BackColor : Theme.Mix(t.Button, BackColor, 0.4f);
            text = Theme.Mix(t.Muted, BackColor, 0.35f);
        }
        float inset = Theme.Px(this, 1f);
        var r = new RectangleF(inset, inset, Width - 2 * inset - 1, Height - 2 * inset - 1);
        using (var path = Theme.Round(r, Theme.Px(this, 8f)))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Focused && ShowFocusCues)
            {
                using var ring = new Pen(Kind == ButtonKind.Primary ? t.Text : t.Accent, Theme.Px(this, 1.5f));
                g.DrawPath(ring, path);
            }
        }
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }
}

/// <summary>1.1.0: an on/off switch that shows a state rather than owning it: a click asks for the change, and the window sets
/// <see cref="On"/> to what really happened.</summary>
sealed class ToggleSwitch : Button
{
    bool _on, _hover;

    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public bool On
    {
        get => _on;
        set { if (_on == value) return; _on = value; AccessibleDescription = value ? "on" : "off"; Invalidate(); }
    }

    public ToggleSwitch()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Size = new Size(60, 32);
        Margin = new Padding(8, 4, 0, 4);
        Cursor = Cursors.Hand;
        AccessibleRole = AccessibleRole.CheckButton;
        AccessibleDescription = "off";
    }

    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float pad = Theme.Px(this, 2f);
        var track = new RectangleF(pad, pad, Width - 2 * pad - 1, Height - 2 * pad - 1);
        Color fill = _on ? t.Success : t.SwitchOff;
        if (_hover && Enabled) fill = Theme.Mix(fill, t.Text, 0.12f);
        if (!Enabled) fill = Theme.Mix(fill, BackColor, 0.55f);
        using (var path = Theme.Round(track, track.Height / 2))
        {
            using (var b = new SolidBrush(fill)) g.FillPath(b, path);
            if (Focused && ShowFocusCues) { using var ring = new Pen(t.Accent, Theme.Px(this, 1.5f)); g.DrawPath(ring, path); }
        }
        float d = track.Height - 2 * Theme.Px(this, 4f);
        float x = _on ? track.Right - d - Theme.Px(this, 4f) : track.X + Theme.Px(this, 4f);
        using var knob = new SolidBrush(Enabled ? Color.White : Theme.Mix(Color.White, BackColor, 0.4f));
        g.FillEllipse(knob, x, track.Y + Theme.Px(this, 4f), d, d);
    }
}

/// <summary>1.2.0: a small rounded tag in the theme's warning colour (UNSTABLE, next to the title).</summary>
sealed class Badge : Control
{
    public Badge()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Font = new Font("Segoe UI Semibold", 9f);
        Margin = new Padding(12, 2, 0, 0);
        AccessibleRole = AccessibleRole.StaticText;
    }

    public override Size GetPreferredSize(Size proposedSize)
    {
        var text = TextRenderer.MeasureText(Text ?? "", Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
        return new Size(text.Width + (int)Math.Round(Theme.Px(this, 20f)), text.Height + (int)Math.Round(Theme.Px(this, 8f)));
    }

    void Fit() { Size = GetPreferredSize(Size.Empty); Invalidate(); }
    protected override void OnTextChanged(EventArgs e) { base.OnTextChanged(e); AccessibleName = Text; Fit(); }
    protected override void OnFontChanged(EventArgs e) { base.OnFontChanged(e); Fit(); }
    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); Fit(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var r = new RectangleF(0.5f, 0.5f, Width - 1.5f, Height - 1.5f);
        using (var path = Theme.Round(r, r.Height / 2))
        using (var b = new SolidBrush(t.Warning)) g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, t.Dark ? t.Window : Color.White,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);
    }
}

enum StepState { Todo, Done, Attention }

/// <summary>1.1.0: a setup step's mark: its number in a ring (to do), a tick (done) or "!" (needs attention).</summary>
sealed class StepIcon : Control
{
    StepState _state;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public StepState State { get => _state; set { if (_state != value) { _state = value; Invalidate(); } } }

    public StepIcon(string number)
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        Text = number;
        Size = new Size(26, 26);
        Margin = new Padding(0, 4, 10, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var t = Theme.Current;
        var g = e.Graphics;
        g.Clear(BackColor);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float w = Theme.Px(this, 1.6f);
        var r = new RectangleF(w, w, Width - 2 * w - 1, Height - 2 * w - 1);
        switch (_state)
        {
            case StepState.Done:
                using (var b = new SolidBrush(t.Success)) g.FillEllipse(b, r);
                using (var pen = new Pen(Color.White, Theme.Px(this, 2.2f)) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                    g.DrawLines(pen, new[]
                    {
                        new PointF(r.X + r.Width * 0.28f, r.Y + r.Height * 0.53f),
                        new PointF(r.X + r.Width * 0.44f, r.Y + r.Height * 0.68f),
                        new PointF(r.X + r.Width * 0.73f, r.Y + r.Height * 0.36f),
                    });
                break;
            case StepState.Attention:
                using (var b = new SolidBrush(t.Warning)) g.FillEllipse(b, r);
                using (var bold = new Font(Font, FontStyle.Bold))
                    TextRenderer.DrawText(g, "!", bold, Rectangle.Round(r), t.Dark ? t.Window : Color.White,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                break;
            default:
                using (var pen = new Pen(t.Muted, w)) g.DrawEllipse(pen, r);
                TextRenderer.DrawText(g, Text, Font, Rectangle.Round(r), t.Muted, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
                break;
        }
    }
}

/// <summary>1.1.0: a small round light (Resonite closed, running, ...).</summary>
sealed class StatusDot : Control
{
    Color _color = Color.Gray;
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public Color Color { get => _color; set { if (_color != value) { _color = value; Invalidate(); } } }

    public StatusDot()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
        Size = new Size(12, 12);
        Margin = new Padding(0, 6, 8, 0);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var b = new SolidBrush(_color);
        e.Graphics.FillEllipse(b, 1, 1, Width - 3, Height - 3);
    }
}
