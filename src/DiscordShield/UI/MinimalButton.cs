using System.Drawing.Drawing2D;

namespace DiscordShield.UI;

public enum ButtonTone
{
    Quiet,
    Accent,
    ShieldOn,
    ShieldOff,
    ShieldError
}

/// <summary>
/// Minimalist, double-buffered flat button following app-design rules:
/// - Zero border at rest; 4px corners (or full pill)
/// - Hover 8%, Pressed 12%, visible 2px+1px keyboard focus ring
/// - >= 28px hit target height at 100% DPI; instant repaint with zero lag
/// </summary>
public sealed class MinimalButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private ThemePalette _palette = DesignTokens.Dark;
    private ButtonTone _tone = ButtonTone.Quiet;
    private bool _pill;
    private bool _selected;

    public MinimalButton(string text, ButtonTone tone = ButtonTone.Quiet, bool pill = false)
    {
        Text = text;
        _tone = tone;
        _pill = pill;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);
        TabStop = true;
        Cursor = Cursors.Hand;
        Padding = new Padding(12, 5, 12, 5);
        Margin = new Padding(2, 0, 2, 0);
        MinimumSize = new Size(DesignTokens.MinHitTarget, DesignTokens.MinHitTarget);
        Font = DesignTokens.CreateUiFont(9.25f, semibold: tone != ButtonTone.Quiet);
        UpdatePreferredSize();
    }

    public ButtonTone Tone
    {
        get => _tone;
        set
        {
            if (_tone != value)
            {
                _tone = value;
                Invalidate();
            }
        }
    }

    public bool Selected
    {
        get => _selected;
        set
        {
            if (_selected != value)
            {
                _selected = value;
                Invalidate();
            }
        }
    }

    [System.Diagnostics.CodeAnalysis.AllowNull]
    public override string Text
    {
        get => base.Text;
        set
        {
            string next = value ?? string.Empty;
            if (base.Text != next)
            {
                base.Text = next;
                UpdatePreferredSize();
                Invalidate();
            }
        }
    }

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

        int width = Math.Max(DesignTokens.MinHitTarget, textSize.Width + Padding.Horizontal + 6);
        int height = Math.Max(DesignTokens.ControlHeight, textSize.Height + Padding.Vertical + 2);
        Size = new Size(width, height);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdatePreferredSize();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        _pressed = false;
        Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left)
        {
            _pressed = true;
            Invalidate();
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (_pressed)
        {
            _pressed = false;
            Invalidate();
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            OnClick(EventArgs.Empty);
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
    }

    public Color SurfaceOverride { get; set; } = Color.Empty;

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color parentBg = Parent is not null && Parent.BackColor != Color.Transparent
            ? Parent.BackColor
            : _palette.BaseSurface;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : parentBg;
        g.Clear(bg);

        Rectangle bounds = new(2, 2, Width - 5, Height - 5);
        int radius = _pill ? bounds.Height / 2 : DesignTokens.CornerControl;

        (Color fill, Color text) = ResolveColors();

        if (fill != Color.Transparent)
        {
            using GraphicsPath path = CreateRoundedRect(bounds, radius);
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }

        if (_selected && _tone == ButtonTone.Quiet)
        {
            int inset = Math.Min(12, bounds.Width / 4);
            Rectangle pillRect = new(bounds.Left + inset, bounds.Bottom - 3, Math.Max(12, bounds.Width - (inset * 2)), 2);
            using GraphicsPath pillPath = CreateRoundedRect(pillRect, 1);
            using var accentBrush = new SolidBrush(_palette.AccentFill);
            g.FillPath(accentBrush, pillPath);
        }

        if (Focused && ShowFocusCues)
        {
            using GraphicsPath outerPath = CreateRoundedRect(new Rectangle(1, 1, Width - 3, Height - 3), radius + 1);
            using var outerPen = new Pen(_palette.FocusOuter, 2f);
            g.DrawPath(outerPen, outerPath);

            using GraphicsPath innerPath = CreateRoundedRect(bounds, radius);
            using var innerPen = new Pen(_palette.FocusInner, 1f);
            g.DrawPath(innerPen, innerPath);
        }

        TextRenderer.DrawText(
            g,
            Text,
            Font,
            bounds,
            Enabled ? text : _palette.TextDisabled,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
    }

    private (Color Fill, Color Text) ResolveColors()
    {
        return _tone switch
        {
            ButtonTone.Accent => (
                _pressed ? DesignTokens.Blend(_palette.TextOnAccent, _palette.AccentFill, 0.14)
                    : _hovered ? DesignTokens.Blend(_palette.TextOnAccent, _palette.AccentFill, 0.08)
                    : _palette.AccentFill,
                _palette.TextOnAccent),

            ButtonTone.ShieldOn => (
                _pressed ? DesignTokens.Blend(_palette.ShieldActiveText, _palette.ShieldActiveFill, 0.16)
                    : _hovered ? DesignTokens.Blend(_palette.ShieldActiveText, _palette.ShieldActiveFill, 0.08)
                    : _palette.ShieldActiveFill,
                _palette.ShieldActiveText),

            ButtonTone.ShieldOff => (
                _pressed ? DesignTokens.Blend(_palette.ShieldOffText, _palette.ShieldOffFill, 0.16)
                    : _hovered ? DesignTokens.Blend(_palette.ShieldOffText, _palette.ShieldOffFill, 0.08)
                    : _palette.ShieldOffFill,
                _palette.ShieldOffText),

            ButtonTone.ShieldError => (
                _hovered ? _palette.HoverFill : _palette.BaseSurface,
                _palette.DangerText),

            _ => (
                _pressed ? _palette.PressedFill
                    : _selected ? _palette.SelectedFill
                    : _hovered ? _palette.HoverFill
                    : Color.Transparent,
                _palette.TextPrimary)
        };
    }

    private static GraphicsPath CreateRoundedRect(Rectangle r, int radius)
    {
        var path = new GraphicsPath();
        int d = Math.Max(2, radius * 2);
        if (d > r.Width) d = r.Width;
        if (d > r.Height) d = r.Height;

        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }
}
