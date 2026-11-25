using System.Drawing.Drawing2D;
using DiscordShield.Core;

namespace DiscordShield.UI;

/// <summary>
/// High-performance, 144fps double-buffered, virtualized tactile list control replacing Win32 ListView.
/// Renders each app/site as a crisp modern row with:
///   - Custom rounded multi-select checkbox (for bulk Block/Allow)
///   - Color-coded rounded-square app/site icon badge with crisp monogram
///   - Two-line typography (Name + App/Site pill badge on line 1, Detail on line 2)
///   - 1-click inline tactile pill switch (Blocked coral ON / Allowed neutral OFF)
///   - Thin custom rounded scrollbar + full keyboard navigation (Up/Down/Space/Enter)
/// </summary>
public sealed class ShieldListControl : Control
{
    private const int RowHeight = 60;
    private const int OuterPad = 8;
    private const int ScrollbarWidth = 6;
    private const int CheckboxZoneWidth = 42;
    private const int SwitchZoneWidth = 124;

    private IReadOnlyList<CatalogEntry> _items = [];
    private readonly HashSet<string> _checkedKeys = new(StringComparer.OrdinalIgnoreCase);
    private int _scrollOffset;
    private int _hoverIndex = -1;
    private int _focusedIndex = -1;
    private bool _hoverOnSwitch;
    private bool _draggingThumb;
    private int _dragStartY;
    private int _dragStartScroll;

    private ThemePalette _palette = DesignTokens.Dark;
    private readonly Font _nameFont;
    private readonly Font _detailFont;
    private readonly Font _pillFont;
    private readonly Font _badgeFont;
    private readonly Font _statusFont;

    public event Action<CatalogEntry>? ItemToggleRequested;
    public event Action? SelectionChanged;

    public ShieldListControl()
    {
        SetStyle(
            ControlStyles.UserPaint
            | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer
            | ControlStyles.ResizeRedraw
            | ControlStyles.Selectable,
            true);

        TabStop = true;
        MinimumSize = new Size(320, 120);
        _nameFont = DesignTokens.CreateUiFont(9.5f, semibold: true);
        _detailFont = DesignTokens.CreateUiFont(8.25f);
        _pillFont = DesignTokens.CreateUiFont(7.5f, semibold: true);
        _badgeFont = DesignTokens.CreateUiFont(9.5f, semibold: true);
        _statusFont = DesignTokens.CreateUiFont(8.25f, semibold: true);
    }

    public int ItemCount => _items.Count;
    public int CheckedCount => _checkedKeys.Count;

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        Invalidate();
    }

    public void SetItems(IReadOnlyList<CatalogEntry> items)
    {
        _items = items;
        if (_checkedKeys.Count > 0)
        {
            var valid = new HashSet<string>(items.Select(EntryId), StringComparer.OrdinalIgnoreCase);
            _checkedKeys.IntersectWith(valid);
        }

        if (_focusedIndex >= _items.Count) _focusedIndex = _items.Count - 1;
        if (_hoverIndex >= _items.Count) _hoverIndex = -1;

        ClampScroll();
        Invalidate();
    }

    public IReadOnlyList<CatalogEntry> GetSelectedOrCheckedItems()
    {
        if (_checkedKeys.Count > 0)
        {
            var result = new List<CatalogEntry>(_checkedKeys.Count);
            foreach (CatalogEntry item in _items)
            {
                if (_checkedKeys.Contains(EntryId(item))) result.Add(item);
            }

            return result;
        }

        if (_focusedIndex >= 0 && _focusedIndex < _items.Count)
        {
            return [_items[_focusedIndex]];
        }

        return [];
    }

    public void ClearChecks()
    {
        if (_checkedKeys.Count > 0)
        {
            _checkedKeys.Clear();
            Invalidate();
            SelectionChanged?.Invoke();
        }
    }

    private static string EntryId(CatalogEntry e) => $"{(int)e.Kind}:{e.Key}";

    private int ViewportHeight => Math.Max(1, Height - (OuterPad * 2));
    private int TotalContentHeight => _items.Count * RowHeight;
    private int MaxScroll => Math.Max(0, TotalContentHeight - ViewportHeight);

    private void ClampScroll()
    {
        _scrollOffset = Math.Clamp(_scrollOffset, 0, MaxScroll);
    }

    private int RowIndexAtY(int y)
    {
        if (_items.Count == 0 || y < OuterPad || y > Height - OuterPad) return -1;
        int idx = (y - OuterPad + _scrollOffset) / RowHeight;
        return idx >= 0 && idx < _items.Count ? idx : -1;
    }

    private Rectangle GetScrollbarThumbRect()
    {
        if (MaxScroll <= 0) return Rectangle.Empty;
        int trackTop = OuterPad + 4;
        int trackH = Math.Max(20, Height - (OuterPad * 2) - 8);
        int thumbH = Math.Max(28, (int)Math.Round(trackH * (ViewportHeight / (double)TotalContentHeight)));
        int thumbY = trackTop + (int)Math.Round((trackH - thumbH) * (_scrollOffset / (double)MaxScroll));
        return new Rectangle(Width - ScrollbarWidth - 5, thumbY, ScrollbarWidth, thumbH);
    }

    protected override void OnMouseWheel(MouseEventArgs e)
    {
        base.OnMouseWheel(e);
        if (MaxScroll <= 0) return;
        int delta = -(e.Delta / 120) * (RowHeight * 2 / 3);
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
            int trackH = Math.Max(20, Height - (OuterPad * 2) - 8);
            Rectangle thumb = GetScrollbarThumbRect();
            int usable = Math.Max(1, trackH - thumb.Height);
            int dy = e.Y - _dragStartY;
            _scrollOffset = Math.Clamp(_dragStartScroll + (int)Math.Round(dy * (MaxScroll / (double)usable)), 0, MaxScroll);
            Invalidate();
            return;
        }

        int idx = RowIndexAtY(e.Y);
        bool onSwitch = idx >= 0 && e.X >= Width - SwitchZoneWidth - (MaxScroll > 0 ? 12 : 0);
        bool onCheck = idx >= 0 && e.X <= CheckboxZoneWidth + OuterPad;

        Cursor = (onSwitch || onCheck || idx >= 0) ? Cursors.Hand : Cursors.Default;

        if (idx != _hoverIndex || onSwitch != _hoverOnSwitch)
        {
            _hoverIndex = idx;
            _hoverOnSwitch = onSwitch;
            Invalidate();
        }
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (_hoverIndex != -1 || _hoverOnSwitch)
        {
            _hoverIndex = -1;
            _hoverOnSwitch = false;
            Invalidate();
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        Focus();
        if (e.Button != MouseButtons.Left) return;

        Rectangle thumb = GetScrollbarThumbRect();
        if (!thumb.IsEmpty && e.X >= thumb.Left - 4 && e.X <= Width)
        {
            if (thumb.Contains(e.Location))
            {
                _draggingThumb = true;
                _dragStartY = e.Y;
                _dragStartScroll = _scrollOffset;
                return;
            }
        }

        int idx = RowIndexAtY(e.Y);
        if (idx < 0) return;

        _focusedIndex = idx;
        CatalogEntry entry = _items[idx];
        int rightMargin = MaxScroll > 0 ? 14 : 4;

        if (e.X >= Width - SwitchZoneWidth - rightMargin)
        {
            // 1-click inline Block / Allow switch
            ItemToggleRequested?.Invoke(entry);
            return;
        }

        if (e.X <= CheckboxZoneWidth + OuterPad)
        {
            ToggleCheck(entry);
            return;
        }

        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        _draggingThumb = false;
    }

    protected override void OnMouseDoubleClick(MouseEventArgs e)
    {
        base.OnMouseDoubleClick(e);
        if (e.Button != MouseButtons.Left) return;
        int idx = RowIndexAtY(e.Y);
        if (idx < 0) return;

        // Avoid double-triggering if user double-clicked directly on the toggle switch or checkbox
        int rightMargin = MaxScroll > 0 ? 14 : 4;
        if (e.X >= Width - SwitchZoneWidth - rightMargin || e.X <= CheckboxZoneWidth + OuterPad) return;

        ItemToggleRequested?.Invoke(_items[idx]);
    }

    protected override bool IsInputKey(Keys keyData) =>
        keyData is Keys.Up or Keys.Down or Keys.PageUp or Keys.PageDown or Keys.Home or Keys.End
        || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (_items.Count == 0) return;

        if (e.KeyCode == Keys.Down)
        {
            _focusedIndex = Math.Min(_items.Count - 1, _focusedIndex + 1);
            EnsureVisible(_focusedIndex);
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Up)
        {
            _focusedIndex = Math.Max(0, _focusedIndex - 1);
            EnsureVisible(_focusedIndex);
            Invalidate();
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Space && _focusedIndex >= 0 && _focusedIndex < _items.Count)
        {
            ToggleCheck(_items[_focusedIndex]);
            e.Handled = true;
        }
        else if (e.KeyCode == Keys.Enter && _focusedIndex >= 0 && _focusedIndex < _items.Count)
        {
            ItemToggleRequested?.Invoke(_items[_focusedIndex]);
            e.Handled = true;
        }
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        ClampScroll();
    }

    private void ToggleCheck(CatalogEntry entry)
    {
        string id = EntryId(entry);
        if (!_checkedKeys.Add(id)) _checkedKeys.Remove(id);
        Invalidate();
        SelectionChanged?.Invoke();
    }

    private void EnsureVisible(int index)
    {
        int rowTop = index * RowHeight;
        int rowBottom = rowTop + RowHeight;
        if (rowTop < _scrollOffset) _scrollOffset = rowTop;
        else if (rowBottom > _scrollOffset + ViewportHeight) _scrollOffset = rowBottom - ViewportHeight;
        ClampScroll();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.BaseSurface);

        Color cardFill = _palette.IsDark ? Color.FromArgb(30, 32, 39) : Color.FromArgb(255, 255, 255);
        Color cardBorder = _palette.IsDark ? Color.FromArgb(48, 53, 65) : Color.FromArgb(222, 228, 238);
        Color shadowColor = _palette.IsDark ? Color.FromArgb(45, 0, 0, 0) : Color.FromArgb(16, 15, 23, 42);
        RoundedDraw.FillRounded(g, new Rectangle(0, 1, Width - 1, Height - 2), 12, shadowColor);
        Rectangle outerRect = new(0, 0, Width - 1, Height - 2);
        RoundedDraw.FillRounded(g, outerRect, 12, cardFill);
        RoundedDraw.DrawRounded(g, outerRect, 12, cardBorder, 1f);

        if (_items.Count == 0)
        {
            TextRenderer.DrawText(
                g,
                "No matching apps or websites right now.\nTry another search, switch tabs, or add a domain above.",
                _detailFont,
                outerRect,
                _palette.TextSub,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }

        Rectangle clip = new(2, OuterPad, Width - 4, ViewportHeight);
        GraphicsState savedState = g.Save();
        g.SetClip(clip);

        int firstIndex = Math.Max(0, _scrollOffset / RowHeight);
        int lastIndex = Math.Min(_items.Count - 1, (_scrollOffset + ViewportHeight) / RowHeight);
        int rightInset = MaxScroll > 0 ? 18 : 10;

        Color dividerColor = _palette.IsDark ? Color.FromArgb(40, 43, 52) : Color.FromArgb(238, 240, 244);
        Color rowHoverFill = _palette.IsDark ? Color.FromArgb(38, 41, 49) : Color.FromArgb(245, 247, 250);
        Color rowCheckedFill = _palette.IsDark ? Color.FromArgb(34, 44, 62) : Color.FromArgb(239, 246, 255);
        Color pillFill = _palette.IsDark ? Color.FromArgb(43, 46, 56) : Color.FromArgb(241, 245, 249);

        using var dividerPen = new Pen(dividerColor, 1f);
        using var checkPen = new Pen(Color.White, 2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        for (int i = firstIndex; i <= lastIndex; i++)
        {
            CatalogEntry entry = _items[i];
            int rowY = OuterPad + (i * RowHeight) - _scrollOffset;
            Rectangle rowRect = new(6, rowY + 2, Width - 12 - (MaxScroll > 0 ? 10 : 0), RowHeight - 4);

            bool isChecked = _checkedKeys.Contains(EntryId(entry));
            bool isHover = i == _hoverIndex;
            bool isFocused = i == _focusedIndex && Focused;

            if (isChecked)
            {
                RoundedDraw.FillRounded(g, rowRect, 8, rowCheckedFill);
            }
            else if (isHover)
            {
                RoundedDraw.FillRounded(g, rowRect, 8, rowHoverFill);
            }

            if (isFocused)
            {
                RoundedDraw.DrawRounded(g, rowRect, 8, _palette.AccentFill, 1f);
            }

            // Subtle inset divider below row
            if (i < _items.Count - 1 && !isHover && !isChecked && i + 1 != _hoverIndex)
            {
                g.DrawLine(dividerPen, 54, rowY + RowHeight - 1, Width - rightInset - 4, rowY + RowHeight - 1);
            }

            // 1. Custom rounded multi-select checkbox (18x18)
            int cbY = rowY + (RowHeight - 18) / 2;
            Rectangle cbRect = new(16, cbY, 18, 18);
            if (isChecked)
            {
                RoundedDraw.FillRounded(g, cbRect, 5, _palette.AccentFill);
                g.DrawLines(checkPen,
                [
                    new PointF(cbRect.X + 4.5f, cbRect.Y + 9.2f),
                    new PointF(cbRect.X + 7.8f, cbRect.Y + 12.6f),
                    new PointF(cbRect.X + 13.5f, cbRect.Y + 6.0f)
                ]);
            }
            else
            {
                Color cbBorder = isHover ? _palette.TextSub : _palette.InputBorder;
                RoundedDraw.DrawRounded(g, cbRect, 5, cbBorder, 1.3f);
            }

            // 2. Vibrant color-coded icon badge (34x34 rounded square)
            int iconY = rowY + (RowHeight - 34) / 2;
            Rectangle iconRect = new(46, iconY, 34, 34);
            (Color badgeBg, Color badgeFg, string glyph) = GetBadgeVisual(entry, _palette.IsDark);
            RoundedDraw.FillRounded(g, iconRect, 9, badgeBg);
            TextRenderer.DrawText(
                g,
                glyph,
                _badgeFont,
                iconRect,
                badgeFg,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // 3. Right-side 1-click inline tactile toggle switch + status label
            int switchW = 40;
            int switchH = 22;
            int switchX = Width - rightInset - switchW - 6;
            int switchY = rowY + (RowHeight - switchH) / 2;
            Rectangle switchRect = new(switchX, switchY, switchW, switchH);

            Color blockedTrack = _palette.IsDark ? Color.FromArgb(239, 68, 68) : Color.FromArgb(220, 38, 38);
            Color allowedTrack = _palette.IsDark ? Color.FromArgb(60, 65, 78) : Color.FromArgb(203, 213, 225);
            Color trackColor = entry.IsBlocked ? blockedTrack : allowedTrack;
            if (isHover && _hoverOnSwitch) trackColor = DesignTokens.Blend(Color.White, trackColor, 0.12);

            RoundedDraw.FillRounded(g, switchRect, switchH / 2, trackColor);
            int thumbSize = 16;
            int thumbX = entry.IsBlocked ? (switchRect.Right - thumbSize - 3) : (switchRect.Left + 3);
            int thumbY = switchY + (switchH - thumbSize) / 2;
            using (var shadowBrush = new SolidBrush(Color.FromArgb(55, 0, 0, 0)))
            {
                g.FillEllipse(shadowBrush, thumbX, thumbY + 1, thumbSize, thumbSize);
            }
            using (var thumbBrush = new SolidBrush(Color.White))
            {
                g.FillEllipse(thumbBrush, thumbX, thumbY, thumbSize, thumbSize);
            }

            Rectangle statusRect = new(switchX - 96, rowY, 86, RowHeight);
            TextRenderer.DrawText(
                g,
                entry.IsBlocked ? "Blocked" : "Allowed",
                _statusFont,
                statusRect,
                entry.IsBlocked ? _palette.DangerText : _palette.ShieldActiveText,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);

            // 4. Two-line typography: DisplayName + Kind pill on top, Detail below
            int textX = 92;
            int textMaxW = Math.Max(80, statusRect.Left - textX - 10);

            Size nameSize = TextRenderer.MeasureText(
                g,
                entry.DisplayName,
                _nameFont,
                new Size(Math.Max(40, textMaxW - 56), 22),
                TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            int clampedNameW = Math.Min(nameSize.Width + 6, Math.Max(40, textMaxW - 52));

            Rectangle nameRect = new(textX, rowY + 9, clampedNameW, 20);
            TextRenderer.DrawText(
                g,
                entry.DisplayName,
                _nameFont,
                nameRect,
                _palette.TextPrimary,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);

            string kindText = entry.Kind == ShieldItemKind.App ? "App" : "Site";
            Rectangle pillRect = new(nameRect.Right + 8, rowY + 10, 38, 17);
            if (pillRect.Right < statusRect.Left - 6)
            {
                RoundedDraw.FillRounded(g, pillRect, 5, pillFill);
                TextRenderer.DrawText(
                    g,
                    kindText,
                    _pillFont,
                    pillRect,
                    _palette.TextSub,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
            }

            Rectangle detailRect = new(textX, rowY + 29, textMaxW, 21);
            TextRenderer.DrawText(
                g,
                entry.Detail,
                _detailFont,
                detailRect,
                _palette.TextSub,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }

        g.Restore(savedState);

        // 5. Thin rounded custom scrollbar thumb
        Rectangle sbThumb = GetScrollbarThumbRect();
        if (!sbThumb.IsEmpty)
        {
            Color thumbColor = _palette.IsDark ? Color.FromArgb(80, 86, 100) : Color.FromArgb(180, 188, 200);
            RoundedDraw.FillRounded(g, sbThumb, ScrollbarWidth / 2, thumbColor);
        }
    }

    private static (Color Bg, Color Fg, string Glyph) GetBadgeVisual(CatalogEntry entry, bool isDark)
    {
        string key = entry.Key.ToLowerInvariant();
        string name = entry.DisplayName.Trim();
        string glyph = entry.Kind == ShieldItemKind.Site
            ? (name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "W")
            : (name.Length > 0 ? char.ToUpperInvariant(name[0]).ToString() : "A");

        Color accent = key switch
        {
            var k when k.Contains("discord") => Color.FromArgb(88, 101, 242),
            var k when k.Contains("whatsapp") => Color.FromArgb(34, 197, 94),
            var k when k.Contains("spotify") => Color.FromArgb(29, 185, 84),
            var k when k.Contains("slack") => Color.FromArgb(224, 30, 90),
            var k when k.Contains("telegram") => Color.FromArgb(14, 165, 233),
            var k when k.Contains("youtube") => Color.FromArgb(239, 68, 68),
            var k when k.Contains("reddit") => Color.FromArgb(249, 115, 22),
            var k when k.Contains("upwork") => Color.FromArgb(20, 168, 0),
            var k when k.Contains("chrome") || k.Contains("msedge") || k.Contains("brave") || k.Contains("firefox") => Color.FromArgb(59, 130, 246),
            var k when k.Contains("code") => Color.FromArgb(0, 122, 204),
            var k when k.Contains("notion") => Color.FromArgb(148, 163, 184),
            _ => DeterministicAccent(key)
        };

        if (isDark)
        {
            Color bg = DesignTokens.Blend(accent, Color.FromArgb(30, 32, 38), 0.24);
            Color fg = DesignTokens.Blend(Color.White, accent, 0.45);
            return (bg, fg, glyph);
        }
        else
        {
            Color bg = DesignTokens.Blend(accent, Color.White, 0.16);
            Color fg = DesignTokens.Blend(Color.Black, accent, 0.25);
            return (bg, fg, glyph);
        }
    }

    private static Color DeterministicAccent(string key)
    {
        ReadOnlySpan<Color> palette =
        [
            Color.FromArgb(99, 102, 241),
            Color.FromArgb(14, 165, 233),
            Color.FromArgb(16, 185, 129),
            Color.FromArgb(245, 158, 11),
            Color.FromArgb(236, 72, 153),
            Color.FromArgb(139, 92, 246),
            Color.FromArgb(20, 184, 166)
        ];

        uint hash = 2166136261u;
        foreach (char c in key)
        {
            hash = (hash ^ c) * 16777619u;
        }

        return palette[(int)(hash % (uint)palette.Length)];
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _nameFont.Dispose();
            _detailFont.Dispose();
            _pillFont.Dispose();
            _badgeFont.Dispose();
            _statusFont.Dispose();
        }

        base.Dispose(disposing);
    }
}
