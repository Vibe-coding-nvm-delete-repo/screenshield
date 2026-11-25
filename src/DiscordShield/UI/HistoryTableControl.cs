using System.Drawing.Drawing2D;
using DiscordShield.Core;

namespace DiscordShield.UI;

/// <summary>
/// High-performance 144fps double-buffered virtualized sortable table for ScreenShield History:
///   Date & Time | Type | Target | Details / Outcome
/// - Clickable column headers with ▲/▼ sort indicators
/// - Color-coded event type pills (Screenshot, Guard, Block/Allow, Setting, Taskbar, Snooze, Update, Verify)
/// - Thin custom rounded scrollbar, hover highlight, and full keyboard navigation
/// </summary>
public sealed class HistoryTableControl : Control
{
    private const int HeaderHeight = 34;
    private const int RowHeight = 40;
    private const int ScrollbarWidth = 6;
    private const int ColDateWidth = 186;
    private const int ColTypeWidth = 114;
    private const int ColTargetWidth = 154;

    private IReadOnlyList<HistoryEntry> _rows = [];
    private HistorySortColumn _sortColumn = HistorySortColumn.Timestamp;
    private bool _sortAscending;
    private int _scrollOffset;
    private int _hoverRow = -1;
    private int _hoverHeader = -1;
    private bool _draggingThumb;
    private int _dragStartY;
    private int _dragStartScroll;

    private ThemePalette _palette = DesignTokens.Dark;
    private readonly Font _headerFont;
    private readonly Font _dateFont;
    private readonly Font _pillFont;
    private readonly Font _targetFont;
    private readonly Font _detailFont;

    public event Action<HistorySortColumn, bool>? SortChanged;

    public HistoryTableControl()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);

        TabStop = true;
        MinimumSize = new Size(360, 120);
        _headerFont = DesignTokens.CreateUiFont(8.5f, semibold: true);
        _dateFont = new Font("Consolas", 8f);
        _pillFont = DesignTokens.CreateUiFont(7.75f, semibold: true);
        _targetFont = DesignTokens.CreateUiFont(9f, semibold: true);
        _detailFont = DesignTokens.CreateUiFont(8.75f);
    }

    public HistorySortColumn SortColumn => _sortColumn;
    public bool SortAscending => _sortAscending;
    public int RowCount => _rows.Count;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    public void SetRows(IReadOnlyList<HistoryEntry> rows)
    {
        _rows = rows;
        if (_hoverRow >= _rows.Count) _hoverRow = -1;
        ClampScroll();
        Invalidate();
    }

    public void SetSort(HistorySortColumn column, bool ascending)
    {
        _sortColumn = column;
        _sortAscending = ascending;
        Invalidate();
    }

    public void ToggleSort(HistorySortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortAscending = !_sortAscending;
        }
        else
        {
            _sortColumn = column;
            _sortAscending = column != HistorySortColumn.Timestamp;
        }

        Invalidate();
        SortChanged?.Invoke(_sortColumn, _sortAscending);
    }

    private int BodyTop => HeaderHeight + 2;
    private int ViewportHeight => Math.Max(1, Height - BodyTop - 6);
    private int TotalContentHeight => _rows.Count * RowHeight;
    private int MaxScroll => Math.Max(0, TotalContentHeight - ViewportHeight);

    private void ClampScroll()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScroll);
    }

    private int HeaderColAtX(int x)
    {
        if (x < 0 || x > Width) return -1;
        if (x < 12 + ColDateWidth) return 0;
        if (x < 12 + ColDateWidth + ColTypeWidth) return 1;
        if (x < 12 + ColDateWidth + ColTypeWidth + ColTargetWidth) return 2;
        return 3;
    }

    private int RowIndexAtY(int y)
    {
        if (_rows.Count == 0 || y < BodyTop || y > Height - 6) return -1;
        int idx = (y - BodyTop + _scrollOffset) / RowHeight;
        return idx >= 0 && idx < _rows.Count ? idx : -1;
    }

    private Rectangle GetScrollbarThumbRect()
    {
        if (MaxScroll <= 0) return Rectangle.Empty;
        int trackTop = BodyTop + 4;
        int trackH = Math.Max(20, ViewportHeight - 8);
        int thumbH = Math.Max(28, (int)Math.Round(trackH * (ViewportHeight / (double)TotalContentHeight)));
        int thumbY = trackTop + (int)Math.Round((trackH - thumbH) * (_scrollOffset / (double)MaxScroll));
        return new Rectangle(Width - ScrollbarWidth - 5, thumbY, ScrollbarWidth, thumbH);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxScroll <= 0) return;
        int delta = -(e.Delta / 120) * (RowHeight * 2);
        int next = Math.Clamp(_scrollOffset + delta, 0, MaxScroll);
        if (next != _scrollOffset)
        {
            _scrollOffset = next;
            Invalidate();
        }
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_draggingThumb)
        {
            int trackH = Math.Max(20, ViewportHeight - 8);
            Rectangle thumb = GetScrollbarThumbRect();
            int usable = Math.Max(1, trackH - thumb.Height);
            int dy = e.Y - _dragStartY;
            _scrollOffset = Math.Clamp(_dragStartScroll + (int)Math.Round(dy * (MaxScroll / (double)usable)), 0, MaxScroll);
            Invalidate();
            return;
        }

        int hdr = e.Y <= HeaderHeight ? HeaderColAtX(e.X) : -1;
        int row = e.Y > HeaderHeight ? RowIndexAtY(e.Y) : -1;
        Cursor = hdr >= 0 ? Cursors.Hand : Cursors.Default;

        if (hdr != _hoverHeader || row != _hoverRow)
        {
            _hoverHeader = hdr;
            _hoverRow = row;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverHeader != -1 || _hoverRow != -1)
        {
            _hoverHeader = -1;
            _hoverRow = -1;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;

        if (e.Y <= HeaderHeight)
        {
            int col = HeaderColAtX(e.X);
            if (col >= 0)
            {
                ToggleSort((HistorySortColumn)col);
            }

            return;
        }

        Rectangle thumb = GetScrollbarThumbRect();
        if (!thumb.IsEmpty && e.X >= thumb.Left - 4 && e.X <= Width && thumb.Contains(e.Location))
        {
            _draggingThumb = true;
            _dragStartY = e.Y;
            _dragStartScroll = _scrollOffset;
        }
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _draggingThumb = false;
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (MaxScroll <= 0) return;

        int next = _scrollOffset;
        if (e.KeyCode == Keys.Down) next += RowHeight;
        else if (e.KeyCode == Keys.Up) next -= RowHeight;
        else if (e.KeyCode == Keys.PageDown) next += ViewportHeight - RowHeight;
        else if (e.KeyCode == Keys.PageUp) next -= ViewportHeight - RowHeight;
        else if (e.KeyCode == Keys.Home) next = 0;
        else if (e.KeyCode == Keys.End) next = MaxScroll;
        else return;

        _scrollOffset = Math.Clamp(next, 0, MaxScroll);
        Invalidate();
        e.Handled = true;
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.BaseSurface);

        Color cardFill = _palette.IsDark ? Color.FromArgb(30, 32, 38) : Color.FromArgb(255, 255, 255);
        Color cardBorder = _palette.IsDark ? Color.FromArgb(46, 50, 60) : Color.FromArgb(226, 232, 240);
        Color headerFill = _palette.IsDark ? Color.FromArgb(26, 28, 34) : Color.FromArgb(248, 250, 252);
        Color dividerColor = _palette.IsDark ? Color.FromArgb(40, 43, 52) : Color.FromArgb(238, 240, 244);

        Rectangle outer = new(0, 0, Width - 1, Height - 1);
        RoundedDraw.FillRounded(g, outer, 10, cardFill);

        // Header bar background clipped inside rounded top
        GraphicsState hdrState = g.Save();
        using (GraphicsPath outerClip = RoundedDraw.CreatePath(new Rectangle(1, 1, Width - 2, Height - 2), 9))
        {
            g.SetClip(outerClip);
            using var hdrBrush = new SolidBrush(headerFill);
            g.FillRectangle(hdrBrush, 0, 0, Width, HeaderHeight);
            using var hdrDivider = new Pen(dividerColor, 1f);
            g.DrawLine(hdrDivider, 0, HeaderHeight, Width, HeaderHeight);
        }

        int xDate = 14;
        int xType = xDate + ColDateWidth;
        int xTarget = xType + ColTypeWidth;
        int xDetails = xTarget + ColTargetWidth;
        int rightInset = MaxScroll > 0 ? 18 : 12;
        int detailsW = Math.Max(80, Width - xDetails - rightInset);

        DrawHeaderCell(g, 0, "Date & Time", new Rectangle(xDate, 0, ColDateWidth - 6, HeaderHeight));
        DrawHeaderCell(g, 1, "Type", new Rectangle(xType, 0, ColTypeWidth - 6, HeaderHeight));
        DrawHeaderCell(g, 2, "Target", new Rectangle(xTarget, 0, ColTargetWidth - 8, HeaderHeight));
        DrawHeaderCell(g, 3, "Details / Outcome", new Rectangle(xDetails, 0, detailsW, HeaderHeight));
        g.Restore(hdrState);

        if (_rows.Count == 0)
        {
            TextRenderer.DrawText(
                g,
                "No history events match this filter yet.\nScreenshots, block/allow changes, guard actions, and setting edits appear here automatically.",
                _detailFont,
                new Rectangle(20, BodyTop, Width - 40, ViewportHeight),
                _palette.TextSub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }
        else
        {
            GraphicsState bodyState = g.Save();
            g.SetClip(new Rectangle(2, BodyTop, Width - 4, ViewportHeight));

            int first = Math.Max(0, _scrollOffset / RowHeight);
            int last = Math.Min(_rows.Count - 1, (_scrollOffset + ViewportHeight) / RowHeight);
            Color rowHover = _palette.IsDark ? Color.FromArgb(38, 41, 49) : Color.FromArgb(245, 247, 250);
            using var divPen = new Pen(dividerColor, 1f);

            for (int i = first; i <= last; i++)
            {
                HistoryEntry row = _rows[i];
                int y = BodyTop + (i * RowHeight) - _scrollOffset;

                if (i == _hoverRow)
                {
                    RoundedDraw.FillRounded(g, new Rectangle(6, y + 2, Width - 12 - (MaxScroll > 0 ? 10 : 0), RowHeight - 4), 6, rowHover);
                }
                else if (i < _rows.Count - 1 && i + 1 != _hoverRow)
                {
                    g.DrawLine(divPen, 14, y + RowHeight - 1, Width - rightInset, y + RowHeight - 1);
                }

                // 1. Date & Time
                string ts = row.Timestamp.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss");
                TextRenderer.DrawText(
                    g,
                    ts,
                    _dateFont,
                    new Rectangle(xDate, y, ColDateWidth - 6, RowHeight),
                    _palette.TextSub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                // 2. Type pill
                (Color pillBg, Color pillFg) = GetTypePillColors(row.Type, _palette.IsDark);
                string typeText = HistoryStore.FormatTypeLabel(row.Type);
                Rectangle pillRect = new(xType, y + ((RowHeight - 22) / 2), 92, 22);
                RoundedDraw.FillRounded(g, pillRect, 6, pillBg);
                TextRenderer.DrawText(
                    g,
                    typeText,
                    _pillFont,
                    pillRect,
                    pillFg,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

                // 3. Target
                TextRenderer.DrawText(
                    g,
                    row.Target,
                    _targetFont,
                    new Rectangle(xTarget, y, ColTargetWidth - 10, RowHeight),
                    _palette.TextPrimary,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

                // 4. Details / Outcome
                TextRenderer.DrawText(
                    g,
                    row.Details,
                    _detailFont,
                    new Rectangle(xDetails, y, detailsW, RowHeight),
                    _palette.TextSub,
                    TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            }

            g.Restore(bodyState);
        }

        // Outer border drawn cleanly on top
        RoundedDraw.DrawRounded(g, outer, 10, cardBorder, 1f);

        // Custom thin scrollbar thumb
        Rectangle sbThumb = GetScrollbarThumbRect();
        if (!sbThumb.IsEmpty)
        {
            Color thumbColor = _palette.IsDark ? Color.FromArgb(80, 86, 100) : Color.FromArgb(180, 188, 200);
            RoundedDraw.FillRounded(g, sbThumb, ScrollbarWidth / 2, thumbColor);
        }
    }

    private void DrawHeaderCell(Graphics g, int colIndex, string title, Rectangle rect)
    {
        bool active = (int)_sortColumn == colIndex;
        bool hover = _hoverHeader == colIndex;

        if (hover)
        {
            RoundedDraw.FillRounded(g, new Rectangle(rect.X - 4, rect.Y + 4, rect.Width + 4, rect.Height - 8), 5, _palette.HoverFill);
        }

        string label = active ? $"{title} {(_sortAscending ? "▲" : "▼")}" : title;
        Color fg = active || hover ? _palette.TextPrimary : _palette.TextSub;
        TextRenderer.DrawText(
            g,
            label,
            _headerFont,
            rect,
            fg,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    private static (Color Bg, Color Fg) GetTypePillColors(HistoryEventType type, bool isDark)
    {
        Color accent = type switch
        {
            HistoryEventType.Screenshot => Color.FromArgb(139, 92, 246),  // Violet
            HistoryEventType.Guard => Color.FromArgb(245, 158, 11),       // Amber
            HistoryEventType.BlockAllow => Color.FromArgb(239, 68, 68),   // Coral
            HistoryEventType.Setting => Color.FromArgb(59, 130, 246),     // Blue
            HistoryEventType.Taskbar => Color.FromArgb(20, 184, 166),     // Teal
            HistoryEventType.Verify => Color.FromArgb(34, 197, 94),       // Emerald
            HistoryEventType.Snooze => Color.FromArgb(249, 115, 22),      // Orange
            HistoryEventType.Update => Color.FromArgb(14, 165, 233),      // Sky
            _ => Color.FromArgb(148, 163, 184)                            // Slate
        };

        if (isDark)
        {
            Color bg = DesignTokens.Blend(accent, Color.FromArgb(30, 32, 38), 0.22);
            Color fg = DesignTokens.Blend(Color.White, accent, 0.50);
            return (bg, fg);
        }
        else
        {
            Color bg = DesignTokens.Blend(accent, Color.White, 0.16);
            Color fg = DesignTokens.Blend(Color.Black, accent, 0.35);
            return (bg, fg);
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _headerFont.Dispose();
            _dateFont.Dispose();
            _pillFont.Dispose();
            _targetFont.Dispose();
            _detailFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
