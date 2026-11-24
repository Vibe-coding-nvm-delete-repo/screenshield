using System.Drawing.Drawing2D;
using DiscordShield.Native;

namespace DiscordShield.UI;

/// <summary>
/// Sleek, theme-aware, word-wrapped floating help card implemented as a non-activating
/// owned popup Form (WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW) so it floats cleanly above
/// every native Win32 control (ComboBox, TextBox, ListView) without stealing focus,
/// mirrors WDA_EXCLUDEFROMCAPTURE, and clamps strictly inside the host window & screen.
/// </summary>
public sealed class HelpPopover : Form
{
    public const int MaxPopoverWidth = 280;
    private const int PaddingX = 12;
    private const int PaddingY = 9;
    private const int MarginFromEdge = 10;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;

    private ThemePalette _palette = DesignTokens.Dark;
    private string _message = string.Empty;
    private readonly Font _font = DesignTokens.CreateUiFont(8.75f);
    private bool _excludeFromCapture = true;

    public HelpPopover()
    {
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        ShowInTaskbar = false;
        TopMost = false;
        DoubleBuffered = true;
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer,
            true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams cp = base.CreateParams;
            cp.ExStyle |= WsExNoActivate | WsExToolWindow;
            return cp;
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        if (IsHandleCreated)
        {
            Win32Interop.ApplyWindows11Chrome(Handle, palette.IsDark, palette.BaseSurface, palette.TextPrimary);
        }

        Invalidate();
    }

    public void SetCaptureExclusion(bool exclude)
    {
        _excludeFromCapture = exclude;
        if (IsHandleCreated && Win32Interop.IsExcludeFromCaptureSupported())
        {
            _ = Win32Interop.SetWindowDisplayAffinity(
                Handle,
                exclude ? Win32Interop.WDA_EXCLUDEFROMCAPTURE : Win32Interop.WDA_NONE);
        }
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32Interop.ApplyWindows11Chrome(Handle, _palette.IsDark, _palette.BaseSurface, _palette.TextPrimary);
        SetCaptureExclusion(_excludeFromCapture);
    }

    public void ShowFor(Control anchor, string text, Form hostForm)
    {
        _message = text;
        Size textSize = TextRenderer.MeasureText(
            text,
            _font,
            new Size(MaxPopoverWidth - (PaddingX * 2), int.MaxValue),
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);

        Size popoverSize = new(
            Math.Min(MaxPopoverWidth, textSize.Width + (PaddingX * 2) + 6),
            textSize.Height + (PaddingY * 2) + 4);

        Point screenPt = anchor.PointToScreen(new Point(0, anchor.Height + 4));
        Point anchorInForm = hostForm.PointToClient(screenPt);

        Rectangle clampedInForm = ComputeClampedBounds(
            anchorInForm,
            anchor.Height,
            popoverSize,
            hostForm.ClientSize);

        Point screenLocation = hostForm.PointToScreen(clampedInForm.Location);

        // Also clamp inside current monitor working area
        Rectangle workArea = Screen.FromControl(hostForm).WorkingArea;
        int sx = Math.Clamp(screenLocation.X, workArea.Left + 4, Math.Max(workArea.Left + 4, workArea.Right - popoverSize.Width - 4));
        int sy = Math.Clamp(screenLocation.Y, workArea.Top + 4, Math.Max(workArea.Top + 4, workArea.Bottom - popoverSize.Height - 4));

        Bounds = new Rectangle(sx, sy, popoverSize.Width, popoverSize.Height);
        if (Owner != hostForm && hostForm.IsHandleCreated)
        {
            Owner = hostForm;
        }

        if (!Visible)
        {
            Show();
        }

        Invalidate();
    }

    public void HidePopover()
    {
        if (Visible)
        {
            Hide();
        }
    }

    public static Rectangle ComputeClampedBounds(
        Point belowAnchorInForm,
        int anchorHeight,
        Size popoverSize,
        Size formClientSize)
    {
        int x = belowAnchorInForm.X;
        int y = belowAnchorInForm.Y;

        if (x + popoverSize.Width + MarginFromEdge > formClientSize.Width)
        {
            x = formClientSize.Width - popoverSize.Width - MarginFromEdge;
        }

        if (x < MarginFromEdge)
        {
            x = MarginFromEdge;
        }

        if (y + popoverSize.Height + MarginFromEdge > formClientSize.Height)
        {
            y = belowAnchorInForm.Y - anchorHeight - popoverSize.Height - 8;
        }

        if (y < MarginFromEdge)
        {
            y = MarginFromEdge;
        }

        return new Rectangle(x, y, popoverSize.Width, popoverSize.Height);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;

        Color cardBg = _palette.IsDark
            ? Color.FromArgb(44, 47, 54)
            : Color.FromArgb(255, 255, 255);
        Color borderColor = _palette.IsDark
            ? Color.FromArgb(84, 90, 104)
            : Color.FromArgb(203, 213, 225);

        g.Clear(cardBg);

        Rectangle borderRect = new(0, 0, Width - 1, Height - 1);
        using (var pen = new Pen(borderColor, 1f))
        {
            g.DrawRectangle(pen, borderRect);
        }

        Rectangle textRect = new(PaddingX, PaddingY, Width - (PaddingX * 2), Height - (PaddingY * 2));
        TextRenderer.DrawText(
            g,
            _message,
            _font,
            textRect,
            _palette.TextPrimary,
            TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _font.Dispose();
        }

        base.Dispose(disposing);
    }
}
