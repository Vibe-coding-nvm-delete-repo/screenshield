using System.Drawing.Drawing2D;

namespace DiscordShield.UI;

public enum ButtonTone
{
    Quiet,
    Secondary,
    Accent,
    ShieldOn,
    ShieldOff,
    ShieldError
}

/// <summary>
/// Tactile, 144fps double-buffered button with Apple/Linear-grade craft:
/// - Subtle top-highlight bevel + crisp hairline border on elevated tones
/// - 1px physical press depression on click
/// - Smooth gradient sheen on primary Accent CTA
/// - Generous padding, >= 32px hit target, crisp keyboard focus ring
/// </summary>
public sealed class MinimalButton : Control
{
    private bool _hovered;
    private bool _pressed;
    private ThemePalette _palette = DesignTokens.Dark;
    private ButtonTone _tone = ButtonTone.Quiet;
    private readonly bool _pill;
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
        Padding = tone == ButtonTone.Quiet ? new Padding(13, 6, 13, 6) : new Padding(14, 7, 14, 7);
        Margin = new Padding(0, 0, 6, 0);
        MinimumSize = new Size(DesignTokens.MinHitTarget, 32);
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

        int width = Math.Max(DesignTokens.MinHitTarget, textSize.Width + Padding.Horizontal + 8);
        int height = Math.Max(32, textSize.Height + Padding.Vertical + 2);
        Size = new Size(width, height);
    }

    protected override void OnFontChanged(EventArgs e)
    {
        base.OnFontChanged(e);
        UpdatePreferredSize();
    }

    protected override void OnPaddingChanged(EventArgs e)
    {
        base.OnPaddingChanged(e);
        UpdatePreferredSize();
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
        if (_pressed) { _pressed = false; Invalidate(); }
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

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color parentBg = Parent is not null && Parent.BackColor != Color.Transparent
            ? Parent.BackColor
            : _palette.BaseSurface;
        Color bg = SurfaceOverride != Color.Empty ? SurfaceOverride : parentBg;
        g.Clear(bg);

        int pressShift = _pressed ? 1 : 0;
        Rectangle bounds = new(2, 2 + pressShift, Width - 5, Height - 5 - pressShift);
        if (bounds.Width <= 4 || bounds.Height <= 4) return;
        int radius = _pill ? bounds.Height / 2 : 8;

        (Color fill, Color text) = ResolveColors();

        // Subtle tactile drop shadow under elevated buttons when not pressed
        if (!_pressed && (_tone is ButtonTone.Secondary or ButtonTone.Accent or ButtonTone.ShieldOn or ButtonTone.ShieldOff || (_selected && _tone == ButtonTone.Quiet)))
        {
            Rectangle shadowRect = new(bounds.X, bounds.Y + 1, bounds.Width, bounds.Height);
            Color shadowColor = _palette.IsDark ? Color.FromArgb(70, 0, 0, 0) : Color.FromArgb(24, 15, 23, 42);
            RoundedDraw.FillRounded(g, shadowRect, radius, shadowColor);
        }

        if (fill != Color.Transparent)
        {
            using GraphicsPath path = RoundedDraw.CreatePath(bounds, radius);
            if (_tone == ButtonTone.Accent)
            {
                Color top = DesignTokens.Blend(Color.White, fill, _pressed ? 0.02 : 0.12);
                Color bottom = DesignTokens.Blend(Color.Black, fill, 0.06);
                using var grad = new LinearGradientBrush(bounds, top, bottom, LinearGradientMode.Vertical);
                g.FillPath(grad, path);
            }
            else if (_tone == ButtonTone.Secondary && _palette.IsDark)
            {
                Color top = DesignTokens.Blend(Color.White, fill, _pressed ? 0.01 : 0.05);
                using var grad = new LinearGradientBrush(bounds, top, fill, LinearGradientMode.Vertical);
                g.FillPath(grad, path);
            }
            else
            {
                using var brush = new SolidBrush(fill);
                g.FillPath(brush, path);
            }

            // Crisp top-edge specular bevel for tactile depth
            if (!_pressed && _tone is ButtonTone.Secondary or ButtonTone.Accent)
            {
                Color rim = _palette.IsDark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(140, 255, 255, 255);
                using var rimPen = new Pen(rim, 1f);
                g.DrawLine(rimPen, bounds.Left + radius, bounds.Top + 1, bounds.Right - radius, bounds.Top + 1);
            }
        }

        // Tactile border
        if (_tone == ButtonTone.Secondary)
        {
            Color borderColor = _hovered
                ? (_palette.IsDark ? Color.FromArgb(82, 88, 104) : Color.FromArgb(186, 194, 208))
                : (_palette.IsDark ? Color.FromArgb(58, 63, 76) : Color.FromArgb(212, 219, 230));
            RoundedDraw.DrawRounded(g, bounds, radius, borderColor, 1f);
        }
        else if (_tone is ButtonTone.ShieldOn or ButtonTone.ShieldOff)
        {
            Color tintBorder = DesignTokens.Blend(text, fill, _palette.IsDark ? 0.30 : 0.22);
            RoundedDraw.DrawRounded(g, bounds, radius, tintBorder, 1f);
        }
        else if (_selected && _tone == ButtonTone.Quiet)
        {
            Color tabBorder = _palette.IsDark ? Color.FromArgb(62, 67, 80) : Color.FromArgb(216, 222, 232);
            RoundedDraw.DrawRounded(g, bounds, radius, tabBorder, 1f);
            int inset = Math.Min(14, bounds.Width / 4);
            Rectangle indicator = new(bounds.Left + inset, bounds.Bottom - 3, Math.Max(14, bounds.Width - (inset * 2)), 2);
            RoundedDraw.FillRounded(g, indicator, 1, _palette.AccentFill);
        }

        if (Focused && ShowFocusCues)
        {
            RoundedDraw.DrawRounded(g, new Rectangle(1, 1, Width - 3, Height - 3), radius + 1, _palette.FocusOuter, 1.5f);
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
            ButtonTone.Secondary => (
                _pressed ? _palette.PressedFill
                    : _hovered ? (_palette.IsDark ? Color.FromArgb(48, 52, 63) : Color.FromArgb(248, 250, 252))
                    : (_palette.IsDark ? Color.FromArgb(39, 42, 51) : Color.FromArgb(255, 255, 255)),
                _palette.TextPrimary),

            ButtonTone.Accent => (
                _pressed ? DesignTokens.Blend(_palette.TextOnAccent, _palette.AccentFill, 0.14)
                    : _hovered ? DesignTokens.Blend(Color.White, _palette.AccentFill, 0.10)
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
                    : _selected ? (_palette.IsDark ? Color.FromArgb(42, 45, 55) : Color.FromArgb(255, 255, 255))
                    : _hovered ? _palette.HoverFill
                    : Color.Transparent,
                _palette.TextPrimary)
        };
    }
}
