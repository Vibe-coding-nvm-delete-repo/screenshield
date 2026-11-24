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
/// Tactile macOS/iOS-style pill toggle switch (38x20 track, crisp elevated thumb,
/// zero clunky Win32 checkbox chrome, >= 24px vertical hit target, full keyboard support).
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
        Margin = new Padding(0, 2, 2, 2);
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

        int width = 38 + (string.IsNullOrEmpty(Text) ? 4 : 10 + textSize.Width + 10);
        int height = Math.Max(24, textSize.Height + 4);
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

        int trackW = 36;
        int trackH = 20;
        int trackY = (Height - trackH) / 2;
        Rectangle track = new(2, trackY, trackW, trackH);

        Color onFill = _onColor != Color.Empty ? _onColor : Color.FromArgb(34, 197, 94);
        Color offFill = _palette.IsDark ? Color.FromArgb(63, 67, 78) : Color.FromArgb(203, 213, 225);
        Color trackFill = _checked ? onFill : offFill;
        if (_hovered) trackFill = DesignTokens.Blend(Color.White, trackFill, 0.10);

        RoundedDraw.FillRounded(g, track, trackH / 2, trackFill);

        int thumbSize = _pressed ? 15 : 14;
        int thumbX = _checked ? (track.Right - thumbSize - 3) : (track.Left + 3);
        int thumbY = trackY + (trackH - thumbSize) / 2;
        using (var thumbBrush = new SolidBrush(Color.White))
        {
            g.FillEllipse(thumbBrush, thumbX, thumbY, thumbSize, thumbSize);
        }

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, new Rectangle(0, trackY - 2, trackW + 4, trackH + 4), (trackH + 4) / 2, _palette.FocusOuter, 1.5f);
        }

        if (!string.IsNullOrEmpty(Text))
        {
            Rectangle textRect = new(track.Right + 10, 0, Width - track.Right - 10, Height);
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
/// Tactile rounded segmented control (replaces clunky Win32 ComboBox dropdowns).
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
        Height = 32;
        MinimumSize = new Size(200, 32);
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

        Rectangle outer = new(0, 0, Width - 1, Height - 1);
        Color trayFill = _palette.IsDark ? Color.FromArgb(20, 21, 25) : Color.FromArgb(229, 231, 235);
        RoundedDraw.FillRounded(g, outer, 8, trayFill);

        if (_options.Length == 0) return;

        int pad = 3;
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
                Color pillFill = _palette.IsDark ? Color.FromArgb(52, 56, 66) : Color.White;
                RoundedDraw.FillRounded(g, seg, 6, pillFill);
            }
            else if (isHover)
            {
                RoundedDraw.FillRounded(g, seg, 6, _palette.HoverFill);
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
            RoundedDraw.DrawRounded(g, outer, 8, _palette.FocusOuter, 1.5f);
        }
    }
}

/// <summary>
/// Tactile rounded stepper (- 15s +) replacing clunky white-arrow Win32 NumericUpDown.
/// </summary>
public sealed class TactileStepper : Control
{
    private int _value;
    private readonly int _min;
    private readonly int _max;
    private readonly int _step;
    private readonly string _suffix;
    private int _hoverZone; // -1 minus, 1 plus, 0 none
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
        Size = new Size(104, 32);
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

    private int ZoneAt(int x) => x < 30 ? -1 : x > Width - 30 ? 1 : 0;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        int z = ZoneAt(e.X);
        if (z != _hoverZone) { _hoverZone = z; Invalidate(); }
    }

    protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); _hoverZone = 0; Invalidate(); }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        int z = ZoneAt(e.X);
        if (z == -1) Value -= _step;
        else if (z == 1) Value += _step;
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

        Rectangle outer = new(0, 0, Width - 1, Height - 1);
        Color trayFill = _palette.IsDark ? Color.FromArgb(20, 21, 25) : Color.FromArgb(229, 231, 235);
        RoundedDraw.FillRounded(g, outer, 8, trayFill);

        Rectangle leftBtn = new(3, 3, 26, Height - 7);
        Rectangle rightBtn = new(Width - 30, 3, 26, Height - 7);
        if (_hoverZone == -1) RoundedDraw.FillRounded(g, leftBtn, 6, _palette.HoverFill);
        if (_hoverZone == 1) RoundedDraw.FillRounded(g, rightBtn, 6, _palette.HoverFill);

        TextRenderer.DrawText(g, "−", Font, leftBtn, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, "+", Font, rightBtn, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        TextRenderer.DrawText(g, $"{_value}{_suffix}", Font, outer, _palette.TextPrimary, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, outer, 8, _palette.FocusOuter, 1.5f);
        }
    }
}

/// <summary>
/// Sleek rounded pill input box (8px corners, leading icon, crisp focus ring, zero Win32 harsh edges).
/// </summary>
public sealed class RoundedInput : Panel
{
    private readonly TextBox _inner;
    private readonly Label _iconLabel;
    private ThemePalette _palette = DesignTokens.Dark;
    private bool _focused;

    public event EventHandler? InputTextChanged;
    public event EventHandler? EnterPressed;

    public RoundedInput(string iconGlyph, string placeholder)
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Height = 34;
        Padding = new Padding(10, 6, 10, 5);
        Cursor = Cursors.IBeam;

        _iconLabel = new Label
        {
            Text = iconGlyph,
            AutoSize = false,
            Dock = DockStyle.Left,
            Width = string.IsNullOrEmpty(iconGlyph) ? 0 : 22,
            TextAlign = ContentAlignment.MiddleLeft,
            Font = DesignTokens.CreateUiFont(9.5f),
            Cursor = Cursors.IBeam
        };

        _inner = new TextBox
        {
            Dock = DockStyle.Fill,
            BorderStyle = BorderStyle.None,
            PlaceholderText = placeholder,
            Font = DesignTokens.CreateUiFont(9.25f)
        };
        _inner.TextChanged += (_, e) => InputTextChanged?.Invoke(this, e);
        _inner.GotFocus += (_, _) => { _focused = true; Invalidate(); };
        _inner.LostFocus += (_, _) => { _focused = false; Invalidate(); };
        _inner.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                EnterPressed?.Invoke(this, EventArgs.Empty);
            }
        };

        Click += (_, _) => _inner.Focus();
        _iconLabel.Click += (_, _) => _inner.Focus();

        Controls.Add(_inner);
        Controls.Add(_iconLabel);
    }

    public string InputText
    {
        get => _inner.Text;
        set => _inner.Text = value;
    }

    public void ClearInput() => _inner.Clear();
    public void FocusInput() => _inner.Focus();
    public Color SurfaceOverride { get; set; } = Color.Empty;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Color fill = palette.IsDark ? Color.FromArgb(20, 21, 25) : Color.FromArgb(248, 249, 251);
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

        Rectangle rect = new(1, 1, Width - 3, Height - 3);
        Color fill = _palette.IsDark ? Color.FromArgb(20, 21, 25) : Color.FromArgb(248, 249, 251);
        Color border = _focused ? _palette.AccentFill : _palette.InputBorder;
        RoundedDraw.FillRounded(g, rect, 8, fill);
        RoundedDraw.DrawRounded(g, rect, 8, border, _focused ? 1.5f : 1f);
    }
}

/// <summary>
/// Clean elevated rounded surface card (10px corners) for Settings sections.
/// </summary>
public sealed class SurfaceCard : Panel
{
    private ThemePalette _palette = DesignTokens.Dark;

    public SurfaceCard()
    {
        SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw | ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint, true);
        Padding = new Padding(14, 9, 14, 9);
    }

    public Color CardFill => _palette.IsDark ? Color.FromArgb(30, 32, 38) : Color.FromArgb(255, 255, 255);

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

        Rectangle r = new(0, 0, Width - 1, Height - 1);
        Color border = _palette.IsDark ? Color.FromArgb(46, 50, 60) : Color.FromArgb(226, 232, 240);
        RoundedDraw.FillRounded(g, r, 10, CardFill);
        RoundedDraw.DrawRounded(g, r, 10, border, 1f);
    }
}
