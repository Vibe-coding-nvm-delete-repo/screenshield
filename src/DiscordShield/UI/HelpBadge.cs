using System.Drawing.Drawing2D;

namespace DiscordShield.UI;

/// <summary>
/// Minimalist, accessible (?) help icon (24x24 hit target, 16px visual circle)
/// that opens a theme-aware, word-wrapped, edge-clamped HelpPopover on hover, click, or Space/Enter.
/// </summary>
public sealed class HelpBadge : Control
{
    private readonly Action<HelpBadge, string> _onShow;
    private readonly Action _onHide;
    private ThemePalette _palette = DesignTokens.Dark;
    private bool _hovered;
    private readonly Font _glyphFont;

    public string HelpText { get; }
    public Color SurfaceOverride { get; set; } = Color.Empty;

    public HelpBadge(string helpText, Action<HelpBadge, string> onShow, Action onHide)
    {
        HelpText = helpText;
        _onShow = onShow;
        _onHide = onHide;

        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.Selectable,
            true);

        Size = new Size(24, 24);
        MinimumSize = new Size(24, 24);
        Margin = new Padding(2, 0, 4, 0);
        Cursor = Cursors.Hand;
        TabStop = true;
        AccessibleName = "Help";
        AccessibleDescription = helpText;
        _glyphFont = DesignTokens.CreateUiFont(8f, semibold: true);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    protected override void OnMouseEnter(EventArgs e)
    {
        base.OnMouseEnter(e);
        _hovered = true;
        Invalidate();
        _onShow(this, HelpText);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
        _onHide();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        _onShow(this, HelpText);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            _onShow(this, HelpText);
        }
        else if (e.KeyCode == Keys.Escape)
        {
            _onHide();
        }
    }

    protected override void OnGotFocus(EventArgs e)
    {
        base.OnGotFocus(e);
        Invalidate();
        _onShow(this, HelpText);
    }

    protected override void OnLostFocus(EventArgs e)
    {
        base.OnLostFocus(e);
        Invalidate();
        _onHide();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        Color surface = SurfaceOverride != Color.Empty ? SurfaceOverride : _palette.ContentSurface;
        g.Clear(surface);

        Rectangle circle = new(4, 4, 15, 15);
        Color fill = _hovered ? _palette.HoverFill : _palette.BaseSurface;
        Color text = _hovered ? _palette.TextPrimary : _palette.TextSub;

        using (var bgBrush = new SolidBrush(fill))
        {
            g.FillEllipse(bgBrush, circle);
        }

        using (var borderPen = new Pen(_hovered ? _palette.TextSub : _palette.InputBorder, 1f))
        {
            g.DrawEllipse(borderPen, circle);
        }

        if (Focused && ShowFocusCues)
        {
            using var focusPen = new Pen(_palette.FocusOuter, 1.5f);
            g.DrawEllipse(focusPen, new Rectangle(2, 2, 19, 19));
        }

        TextRenderer.DrawText(
            g,
            "?",
            _glyphFont,
            circle,
            text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _glyphFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
