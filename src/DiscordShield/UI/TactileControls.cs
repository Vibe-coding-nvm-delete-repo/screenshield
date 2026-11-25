using System.Drawing.Drawing2D;

namespace DiscordShield.UI;

internal static class RoundedDraw
{
    public static GraphicsPath CreatePath(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        if (r.Width <= 0 || r.Height <= 0) return path;

        int d = Math.Max(2, Math.Min(radius * 2, Math.Min(r.Width, r.Height)));
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    public static void FillRounded(Graphics g, Rectangle r, int radius, Color color)
    {
        using GraphicsPath path = CreatePath(r, radius);
        using var brush = new SolidBrush(color);
        g.FillPath(brush, path);
    }

    public static void DrawRounded(Graphics g, Rectangle r, int radius, Color color, float width = 1f)
    {
        using GraphicsPath path = CreatePath(r, radius);
        using var pen = new Pen(color, width);
        g.DrawPath(pen, path);
    }
}

/// <summary>
/// Tactile macOS/iOS-style pill toggle switch (40x22 track, soft drop-shadowed thumb,
/// tactile thumb stretch on press, zero clunky Win32 checkbox chrome, full keyboard support).
/// </summary>
public sealed class TactileToggle : Control
{
    private bool _checked;
    private bool _hovered;
    private bool _pressed;
    private ThemePalette _palette = DesignTokens.Dark;
    private Color _onColor = Color.Empty;

    public event EventHandler? CheckedChanged;

    public TactileToggle(string label, bool isChecked = false)
    {
        Text = label;
        _checked = isChecked;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Font = DesignTokens.CreateUiFont(9.25f);
        Margin = new Padding(0, 3, 4, 3);
        UpdatePreferredSize();
    }

    public bool Checked
    {
        get => _checked;
        set
        {
            if (_checked != value)
            {
                _checked = value;
                Invalidate();
                CheckedChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Color OnColorOverride
    {
        get => _onColor;
        set { _onColor = value; Invalidate(); }
    }

    public Color SurfaceOverride { get; set; } = Color.Empty;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    private void UpdatePreferredSize()
    {
        Size textSize = TextRenderer.MeasureText(
            string.IsNullOrEmpty(Text) ? " " : Text,
            Font,
            new Size(int.MaxValue, int.MaxValue),
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix);

        int width = 42 + (string.IsNullOrEmpty(Text) ? 4 : 11 + textSize.Width + 10);
        int height = Math.Max(26, textSize.Height + 6);
        Size = new Size(width, height);
    }

    protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); _hovered = true; Invalidate(); }
    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hovered = false; _pressed = false; Invalidate(); }
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left) { _pressed = true; Invalidate(); }
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed)
        {
            _pressed = false;
            if (ClientRectangle.Contains(e.Location)) Checked = !Checked;
            else Invalidate();
        }
    }
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            Checked = !Checked;
        }
    }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : _palette.ContentSurface;
        g.Clear(bg);

        int trackW = 40;
        int trackH = 22;
        int trackY = (Height - trackH) / 2;
        Rectangle track = new(2, trackY, trackW, trackH);

        Color onFill = _onColor != Color.Empty ? _onColor : Color.FromArgb(34, 197, 94);
        Color offFill = _palette.IsDark ? Color.FromArgb(60, 65, 78) : Color.FromArgb(203, 213, 225);
        Color trackFill = _checked ? onFill : offFill;
        if (_hovered) trackFill = DesignTokens.Blend(Color.White, trackFill, 0.10);

        RoundedDraw.FillRounded(g, track, trackH / 2, trackFill);

        // Subtle inner rim on track
        Color trackBorder = _checked
            ? DesignTokens.Blend(Color.Black, trackFill, 0.14)
            : (_palette.IsDark ? Color.FromArgb(74, 80, 96) : Color.FromArgb(182, 192, 206));
        RoundedDraw.DrawRounded(g, track, trackH / 2, trackBorder, 1f);

        // Tactile thumb with soft drop shadow and subtle horizontal stretch on press
        int thumbH = 16;
        int thumbW = _pressed ? 19 : 16;
        int thumbX = _checked ? (track.Right - thumbW - 3) : (track.Left + 3);
        int thumbY = trackY + (trackH - thumbH) / 2;

        RoundedDraw.FillRounded(g, new Rectangle(thumbX, thumbY + 1, thumbW, thumbH), thumbH / 2, Color.FromArgb(55, 0, 0, 0));
        RoundedDraw.FillRounded(g, new Rectangle(thumbX, thumbY, thumbW, thumbH), thumbH / 2, Color.White);

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, new Rectangle(0, trackY - 2, trackW + 4, trackH + 4), (trackH + 4) / 2, _palette.FocusOuter, 1.5f);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            Rectangle textRect = new(track.Right + 11, 0, Width - track.Right - 11, Height);
            TextRenderer.DrawText(
                g,
                Text,
                Font,
                textRect,
                _palette.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }
}

/// <summary>
/// Tactile rounded segmented control with recessed tray, elevated active pill, and subtle drop shadow.
/// </summary>
public sealed class SegmentedControl : Control
{
    private readonly string[] _options;
    private int _selectedIndex;
    private int _hoverIndex = -1;
    private ThemePalette _palette = DesignTokens.Dark;

    public event EventHandler? SelectedIndexChanged;

    public SegmentedControl(string[] options, int initialIndex = 0)
    {
        _options = options;
        _selectedIndex = Math.Clamp(initialIndex, 0, Math.Max(0, options.Length - 1));
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Height = 36;
        MinimumSize = new Size(200, 34);
        Font = DesignTokens.CreateUiFont(8.5f, semibold: true);
    }

    public int SelectedIndex
    {
        get => _selectedIndex;
        set
        {
            int clamped = Math.Clamp(value, 0, Math.Max(0, _options.Length - 1));
            if (_selectedIndex != clamped)
            {
                _selectedIndex = clamped;
                Invalidate();
                SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Color SurfaceOverride { get; set; } = Color.Empty;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    private int HitTest(int x)
    {
        if (_options.Length == 0 || Width <= 0) return -1;
        int segW = Width / _options.Length;
        return Math.Clamp(x / Math.Max(1, segW), 0, _options.Length - 1);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int idx = HitTest(e.X);
        if (idx != _hoverIndex) { _hoverIndex = idx; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1) { _hoverIndex = -1; Invalidate(); }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            int idx = HitTest(e.X);
            if (idx >= 0) SelectedIndex = idx;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode == Keys.Left) { SelectedIndex = Math.Max(0, _selectedIndex - 1); e.Handled = true; }
        else if (e.KeyCode == Keys.Right) { SelectedIndex = Math.Min(_options.Length - 1, _selectedIndex + 1); e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : _palette.ContentSurface;
        g.Clear(bg);

        Rectangle outer = new(1, 1, Width - 3, Height - 3);
        Color trayFill = _palette.IsDark ? Color.FromArgb(19, 21, 26) : Color.FromArgb(228, 232, 238);
        Color trayBorder = _palette.IsDark ? Color.FromArgb(44, 48, 58) : Color.FromArgb(210, 216, 226);
        RoundedDraw.FillRounded(g, outer, 9, trayFill);
        RoundedDraw.DrawRounded(g, outer, 9, trayBorder, 1f);

        if (_options.Length == 0) return;

        int pad = 4;
        float segW = (Width - (pad * 2f)) / _options.Length;
        for (int i = 0; i < _options.Length; i++)
        {
            Rectangle seg = new(
                (int)Math.Round(pad + (i * segW)),
                pad,
                (int)Math.Round(segW) - 1,
                Height - (pad * 2) - 1);

            bool isSelected = i == _selectedIndex;
            bool isHover = i == _hoverIndex && !isSelected;

            if (isSelected)
            {
                Color shadowColor = _palette.IsDark ? Color.FromArgb(65, 0, 0, 0) : Color.FromArgb(28, 15, 23, 42);
                RoundedDraw.FillRounded(g, new Rectangle(seg.X, seg.Y + 1, seg.Width, seg.Height), 7, shadowColor);

                Color pillFill = _palette.IsDark ? Color.FromArgb(54, 59, 72) : Color.White;
                Color pillBorder = _palette.IsDark ? Color.FromArgb(74, 80, 96) : Color.FromArgb(214, 220, 230);
                RoundedDraw.FillRounded(g, seg, 7, pillFill);
                RoundedDraw.DrawRounded(g, seg, 7, pillBorder, 1f);
            }
            else if (isHover)
            {
                RoundedDraw.FillRounded(g, seg, 7, _palette.HoverFill);
            }

            TextRenderer.DrawText(
                g,
                _options[i],
                Font,
                seg,
                isSelected ? _palette.TextPrimary : _palette.TextSub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
        }

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, outer, 9, _palette.FocusOuter, 1.5f);
        }
    }
}

/// <summary>
/// Tactile rounded stepper (- 15s +) with elevated mini-buttons and physical press feedback.
/// </summary>
public sealed class TactileStepper : Control
{
    private int _value;
    private readonly int _min;
    private readonly int _max;
    private readonly int _step;
    private readonly string _suffix;
    private int _hoverZone;
    private int _pressedZone;
    private ThemePalette _palette = DesignTokens.Dark;

    public event EventHandler? ValueChanged;

    public TactileStepper(int value, int min, int max, int step = 5, string suffix = "s")
    {
        _min = min;
        _max = max;
        _step = step;
        _suffix = suffix;
        _value = Math.Clamp(value, min, max);
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Size = new Size(112, 36);
        MinimumSize = new Size(104, 34);
        Font = DesignTokens.CreateUiFont(9f, semibold: true);
    }

    public int Value
    {
        get => _value;
        set
        {
            int clamped = Math.Clamp(value, _min, _max);
            if (_value != clamped)
            {
                _value = clamped;
                Invalidate();
                ValueChanged?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    public Color SurfaceOverride { get; set; } = Color.Empty;

    public void ApplyTheme(ThemePalette palette) { _palette = palette; Invalidate(); }

    private int ZoneAt(int x) => x < 34 ? -1 : x > Width - 34 ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int z = ZoneAt(e.X);
        if (z != _hoverZone) { _hoverZone = z; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverZone = 0; _pressedZone = 0; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int z = ZoneAt(e.X);
        _pressedZone = z;
        if (z == -1) Value -= _step;
        else if (z == 1) Value += _step;
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressedZone != 0) { _pressedZone = 0; Invalidate(); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Left or Keys.Down) { Value -= _step; e.Handled = true; }
        else if (e.KeyCode is Keys.Right or Keys.Up) { Value += _step; e.Handled = true; }
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : _palette.ContentSurface;
        g.Clear(bg);

        Rectangle outer = new(1, 1, Width - 3, Height - 3);
        Color trayFill = _palette.IsDark ? Color.FromArgb(19, 21, 26) : Color.FromArgb(228, 232, 238);
        Color trayBorder = _palette.IsDark ? Color.FromArgb(44, 48, 58) : Color.FromArgb(210, 216, 226);
        RoundedDraw.FillRounded(g, outer, 9, trayFill);
        RoundedDraw.DrawRounded(g, outer, 9, trayBorder, 1f);

        int btnW = 28;
        int btnH = Height - 9;
        Rectangle leftBtn = new(4, 4 + (_pressedZone == -1 ? 1 : 0), btnW, btnH - (_pressedZone == -1 ? 1 : 0));
        Rectangle rightBtn = new(Width - btnW - 5, 4 + (_pressedZone == 1 ? 1 : 0), btnW, btnH - (_pressedZone == 1 ? 1 : 0));

        Color pillRest = _palette.IsDark ? Color.FromArgb(42, 46, 56) : Color.White;
        Color pillHover = _palette.IsDark ? Color.FromArgb(56, 61, 74) : Color.FromArgb(248, 250, 252);
        Color pillBorder = _palette.IsDark ? Color.FromArgb(62, 68, 82) : Color.FromArgb(212, 218, 228);

        RoundedDraw.FillRounded(g, leftBtn, 6, _hoverZone == -1 ? pillHover : pillRest);
        RoundedDraw.DrawRounded(g, leftBtn, 6, pillBorder, 1f);

        RoundedDraw.FillRounded(g, rightBtn, 6, _hoverZone == 1 ? pillHover : pillRest);
        RoundedDraw.DrawRounded(g, rightBtn, 6, pillBorder, 1f);

        TextRenderer.DrawText(g, "−", Font, leftBtn, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "+", Font, rightBtn, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{_value}{_suffix}", Font, outer, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, outer, 9, _palette.FocusOuter, 1.5f);
        }
    }
}

/// <summary>
/// Tactile rounded pill input box (36px height, pixel-centered text at any DPI, hover highlight,
/// glowing focus ring, clean leading icon, zero Win32 harsh borders).
/// </summary>
public sealed class RoundedInput : Panel
{
    private readonly TextBox _inner;
    private readonly Label _iconLabel;
    private readonly string _placeholder;
    private ThemePalette _palette = DesignTokens.Dark;
    private bool _focused;
    private bool _hovered;

    public event EventHandler? InputTextChanged;
    public event EventHandler? EnterPressed;

    public RoundedInput(string iconGlyph, string placeholder)
    {
        _placeholder = placeholder;
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Height = 36;
        MinimumSize = new Size(90, 34);
        Cursor = Cursors.IBeam;

        _iconLabel = new Label
        {
            Text = iconGlyph,
            AutoSize = false,
            Width = string.IsNullOrEmpty(iconGlyph) ? 0 : 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = DesignTokens.CreateUiFont(9.5f),
            Cursor = Cursors.IBeam
        };

        _inner = new TextBox
        {
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Font = DesignTokens.CreateUiFont(9.25f)
        };
        _inner.TextChanged += (_, e) => InputTextChanged?.Invoke(this, e);
        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _inner.MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        _inner.MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        _inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                EnterPressed?.Invoke(this, EventArgs.Empty);
            }
        };

        MouseEnter += (_, _) => { _hovered = true; Invalidate(); };
        MouseLeave += (_, _) => { _hovered = false; Invalidate(); };
        Click += (_, _) => _inner.Focus();
        _iconLabel.Click += (_, _) => _inner.Focus();

        Controls.Add(_inner);
        if (!string.IsNullOrEmpty(iconGlyph))
        {
            Controls.Add(_iconLabel);
        }

        LayoutChildren();
    }

    public string InputText
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    public string Placeholder => _placeholder;
    public void ClearInput() => _inner.Clear();
    public void FocusInput() => _inner.Focus();
    public Color SurfaceOverride { get; set; } = Color.Empty;

    protected override void OnResize(EventArgs eventargs)
    {
        base.OnResize(eventargs);
        LayoutChildren();
    }

    private void LayoutChildren()
    {
        if (_inner is null || _iconLabel is null) return;
        int leftPad = 12;
        int rightPad = 12;
        int iconW = _iconLabel.Width;
        if (iconW > 0)
        {
            _iconLabel.Bounds = new Rectangle(leftPad, 2, iconW, Height - 4);
        }

        int textX = leftPad + (iconW > 0 ? iconW + 2 : 0);
        int textH = Math.Max(16, _inner.PreferredHeight);
        int textY = Math.Max(4, (Height - textH) / 2);
        _inner.Bounds = new Rectangle(textX, textY, Math.Max(20, Width - textX - rightPad), textH);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Color fill = palette.IsDark ? Color.FromArgb(20, 22, 27) : Color.FromArgb(255, 255, 255);
        _inner.BackColor = fill;
        _inner.ForeColor = palette.TextPrimary;
        _iconLabel.BackColor = fill;
        _iconLabel.ForeColor = palette.TextSub;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : _palette.ContentSurface;
        g.Clear(bg);

        Rectangle rect = new(2, 2, Width - 5, Height - 5);
        Color fill = _palette.IsDark ? Color.FromArgb(20, 22, 27) : Color.FromArgb(255, 255, 255);

        if (_focused)
        {
            Color glow = Color.FromArgb(52, _palette.AccentFill.R, _palette.AccentFill.G, _palette.AccentFill.B);
            RoundedDraw.DrawRounded(g, new Rectangle(1, 1, Width - 3, Height - 3), 9, glow, 3f);
        }

        RoundedDraw.FillRounded(g, rect, 8, fill);

        Color border = _focused
            ? _palette.AccentFill
            : _hovered
                ? (_palette.IsDark ? Color.FromArgb(84, 90, 108) : Color.FromArgb(172, 182, 198))
                : (_palette.IsDark ? Color.FromArgb(54, 59, 72) : Color.FromArgb(210, 217, 228));

        RoundedDraw.DrawRounded(g, rect, 8, border, _focused ? 1.5f : 1f);
    }
}

/// <summary>
/// Elevated rounded surface card (12px corners, generous padding, subtle top highlight sheen).
/// </summary>
public sealed class SurfaceCard : Panel
{
    private ThemePalette _palette = DesignTokens.Dark;

    public SurfaceCard()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Padding = new Padding(20, 14, 20, 14);
    }

    public Color CardFill => _palette.IsDark ? Color.FromArgb(30, 32, 39) : Color.FromArgb(255, 255, 255);

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = CardFill;
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.BaseSurface);

        Rectangle shadow = new(0, 1, Width - 1, Height - 2);
        Color shadowColor = _palette.IsDark ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(16, 15, 23, 42);
        RoundedDraw.FillRounded(g, shadow, 12, shadowColor);

        Rectangle r = new(0, 0, Width - 1, Height - 2);
        Color border = _palette.IsDark ? Color.FromArgb(48, 53, 65) : Color.FromArgb(222, 228, 238);
        RoundedDraw.FillRounded(g, r, 12, CardFill);
        RoundedDraw.DrawRounded(g, r, 12, border, 1f);

        // Subtle top-edge highlight sheen in dark mode
        if (_palette.IsDark)
        {
            using var sheen = new Pen(Color.FromArgb(18, 255, 255, 255), 1f);
            g.DrawLine(sheen, r.Left + 12, r.Top + 1, r.Right - 12, r.Top + 1);
        }
    }
}
