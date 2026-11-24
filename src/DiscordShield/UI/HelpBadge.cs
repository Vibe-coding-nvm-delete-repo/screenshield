using System.Drawing.Drawing2D;

namespace DiscordShield.UI;

/// <summary>
/// Minimalist, accessible (?) help icon (24x24 hit target, 16px visual circle)
/// that explains a setting in plain English on BOTH hover and click/keyboard press.
/// </summary>
public sealed class HelpBadge : Control
{
    private readonly ToolTip _toolTip;
    private ThemePalette _palette = DesignTokens.Dark;
    private bool _hovered;
    private readonly Font _glyphFont;

    public string HelpText { get; }

    public HelpBadge(string helpText, ToolTip sharedToolTip)
    {
        HelpText = helpText;
        _toolTip = sharedToolTip;

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

        _toolTip.SetToolTip(this, helpText);
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
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hovered = false;
        Invalidate();
    }

    protected override void OnClick(EventArgs e)
    {
        base.OnClick(e);
        ShowHelpPopup();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.KeyCode is Keys.Space or Keys.Enter)
        {
            e.Handled = true;
            ShowHelpPopup();
        }
    }

    private void ShowHelpPopup()
    {
        _toolTip.Show(HelpText, this, 0, Height + 4, 6500);
    }

    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.ContentSurface);

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
