using System.Drawing.Drawing2D;
using System.IO;
using DiscordShield.Core;
using DiscordShield.Native;
using Microsoft.Win32;

namespace DiscordShield.UI;

/// <summary>
/// ScreenShield — Minimalist, ultra-snappy 4-tab Windows privacy shield:
///   All Apps & Sites | Allowed | Blocked | Settings
/// - Any app/site not in Blocked is automatically Allowed.
/// - Supports 1-click and bulk Block / Allow.
/// - Hides ScreenShield itself via WDA_EXCLUDEFROMCAPTURE (0x11).
/// - Auto-hides Windows taskbar via SHAppBarMessage(ABS_AUTOHIDE) when blocked items are open.
/// </summary>
public sealed class MainForm : Form
{
    private const int HotkeyId = 0x51D0;
    private const uint VkH = 0x48;

    private readonly ICaptureExclusionService _captureService;
    private readonly IWindowInspector _windowInspector;
    private readonly IUpworkAutomationHelper _upworkAutomation;
    private readonly ITaskbarShieldService _taskbarShield;
    private readonly IUpworkScreenshotWatcher _screenshotWatcher;
    private readonly WindowGuardEngine _guardEngine;

    private AppSettings _settings;
    private ThemePalette _palette;
    private ShieldFilterTab _activeTab = ShieldFilterTab.All;
    private IReadOnlyList<CatalogEntry> _allCatalogEntries = [];
    private IReadOnlyList<CatalogEntry> _visibleEntries = [];
    private bool _lastUpworkRunning;
    private int _lastExposedCount;

    private bool _forceExit;
    private bool _hotkeyRegistered;
    private bool _isDraggingOrResizing;
    private int _guardInFlight;
    private int _refreshInFlight;
    private bool _suppressSettingsEvents;
    private readonly bool _autoRefreshOnShow;
    private readonly Icon _shieldIcon;
    private readonly Font _headerColumnFont = DesignTokens.CreateUiFont(8.75f, semibold: true);
    private IntPtr _foregroundHook;
    private IntPtr _minimizeEndHook;
    private readonly Win32Interop.WinEventDelegate _winEventCallback;
    private CancellationTokenSource? _saveDebounceCts;
    private FormWindowState _lastWindowState = FormWindowState.Normal;

    // Header & Tabs
    private readonly TableLayoutPanel _headerBar;
    private readonly Panel _brandLogo;
    private readonly Label _lblBrandName;
    private readonly MinimalButton _btnShieldPill;
    private readonly Label _lblGuardStatus;
    private readonly MinimalButton _btnRefresh;
    private readonly MinimalButton _btnVerify;
    private ToolStripMenuItem? _trayLastSsItem;

    private readonly FlowLayoutPanel _tabBar;
    private readonly MinimalButton _tabAll;
    private readonly MinimalButton _tabAllowed;
    private readonly MinimalButton _tabBlocked;
    private readonly MinimalButton _tabSettings;

    // Catalog page (shared across All / Allowed / Blocked for sub-ms tab switches)
    private readonly Panel _catalogPage;
    private readonly TextBox _txtSearch;
    private readonly Label _lblSearchPlaceholder;
    private readonly TextBox _txtAddSite;
    private readonly Label _lblAddSitePlaceholder;
    private readonly MinimalButton _btnAddSite;
    private readonly MinimalButton _btnBulkBlock;
    private readonly MinimalButton _btnBulkAllow;
    private readonly DoubleBufferedListView _listView;
    private readonly Label _lblEmptyHint;

    // Inline Settings page
    private readonly Panel _settingsPage;
    private readonly CheckBox _chkExcludeSelf;
    private readonly CheckBox _chkTaskbarHide;
    private readonly CheckBox _chkGuardEnabled;
    private readonly CheckBox _chkOnlyWhenUpwork;
    private readonly ComboBox _cmbAction;
    private readonly TextBox _txtUpworkProcs;
    private readonly NumericUpDown _numCooldown;
    private readonly CheckBox _chkMinimizeToTray;
    private readonly CheckBox _chkGlobalHotkey;
    private readonly TextBox _txtLog;
    private readonly MinimalButton _btnCopyLog;
    private readonly MinimalButton _btnClearLog;
    private readonly List<Label> _settingsSubLabels = [];
    private readonly List<Label> _settingsSectionHeaders = [];
    private readonly List<HelpBadge> _helpBadges = [];

    private readonly ToolTip _toolTip;
    private readonly HelpPopover _helpPopover;
    private readonly Panel _sentinelOverlay;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _affinityWatchdog;

    public IReadOnlyList<Control> InteractiveControls =>
    [
        _btnShieldPill, _btnRefresh, _btnVerify,
        _tabAll, _tabAllowed, _tabBlocked, _tabSettings,
        _btnAddSite, _btnBulkBlock, _btnBulkAllow,
        _btnCopyLog, _btnClearLog
    ];

    public IReadOnlyList<HelpBadge> HelpBadges => _helpBadges;
    public HelpPopover HelpPopover => _helpPopover;
    public void ShowHelpForBadge(HelpBadge badge) => _helpPopover.ShowFor(badge, badge.HelpText, this);

    public ThemePalette CurrentPalette => _palette;
    public ShieldFilterTab ActiveTab => _activeTab;
    public IReadOnlyList<CatalogEntry> VisibleCatalogEntries => _visibleEntries;

    public MainForm(
        AppSettings settings,
        ICaptureExclusionService captureService,
        IWindowInspector windowInspector,
        IUpworkAutomationHelper upworkAutomation,
        ITaskbarShieldService? taskbarShield = null,
        ThemePalette? initialPalette = null,
        bool autoRefreshOnShow = true,
        IUpworkScreenshotWatcher? screenshotWatcher = null)
    {
        _settings = settings;
        _captureService = captureService;
        _windowInspector = windowInspector;
        _upworkAutomation = upworkAutomation;
        _taskbarShield = taskbarShield ?? new TaskbarShieldService();
        _screenshotWatcher = screenshotWatcher ?? new UpworkScreenshotWatcher();
        _guardEngine = new WindowGuardEngine();
        _palette = initialPalette ?? DesignTokens.SystemDefault();
        _autoRefreshOnShow = autoRefreshOnShow;
        _winEventCallback = OnWinEventTriggered;

        Text = "ScreenShield";
        Size = new Size(1020, 680);
        MinimumSize = new Size(760, 520);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        Font = DesignTokens.CreateUiFont(9.5f);
        KeyPreview = true;
        DoubleBuffered = true;

        _shieldIcon = LoadOrCreateShieldIcon();
        Icon = _shieldIcon;
        _toolTip = new ToolTip
        {
            InitialDelay = 120,
            ReshowDelay = 80,
            AutoPopDelay = 10000,
            ShowAlways = true
        };

        // 1. Clean Apple/Fluent-inspired Header Bar
        _headerBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 48),
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(16, 10, 16, 6)
        };
        _headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _headerBar.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var brandPanel = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 14, 0)
        };
        _brandLogo = new Panel
        {
            Size = new Size(24, 24),
            Margin = new Padding(0, 2, 7, 0),
            BackColor = Color.Transparent
        };
        _brandLogo.Paint += OnPaintBrandLogo;

        _lblBrandName = new Label
        {
            Text = "ScreenShield",
            AutoSize = true,
            Font = DesignTokens.CreateUiFont(10.5f, semibold: true),
            Padding = new Padding(0, 3, 0, 0),
            Margin = new Padding(0)
        };
        brandPanel.Controls.Add(_brandLogo);
        brandPanel.Controls.Add(_lblBrandName);

        _btnShieldPill = new MinimalButton("● Protected", ButtonTone.ShieldOn, pill: true);
        _btnShieldPill.Click += (_, _) => ToggleCaptureExclusion();
        _toolTip.SetToolTip(_btnShieldPill, "Toggle screenshot exclusion on ScreenShield (Ctrl+Shift+S)");

        _lblGuardStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(12, 0, 8, 0),
            Font = DesignTokens.CreateUiFont(8.75f),
            Text = "Upwork off · Last SS: none yet"
        };

        var headerRight = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };

        _btnRefresh = new MinimalButton("↻ Refresh", ButtonTone.Quiet);
        _btnRefresh.Click += (_, _) => RefreshCatalogAsync();
        _toolTip.SetToolTip(_btnRefresh, "Scan active desktop apps & browser tabs (F5)");

        _btnVerify = new MinimalButton("✓ Verify", ButtonTone.Quiet);
        _btnVerify.Click += async (_, _) => await RunLiveCaptureVerificationAsync();
        _toolTip.SetToolTip(_btnVerify, "Run live Windows screenshot test (Ctrl+Shift+V)");

        headerRight.Controls.Add(_btnRefresh);
        headerRight.Controls.Add(_btnVerify);

        _headerBar.Controls.Add(brandPanel, 0, 0);
        _headerBar.Controls.Add(_btnShieldPill, 1, 0);
        _headerBar.Controls.Add(_lblGuardStatus, 2, 0);
        _headerBar.Controls.Add(headerRight, 3, 0);

        // 2. 4-Tab Navigation Bar with crisp icons
        _tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(14, 2, 14, 8)
        };

        _tabAll = new MinimalButton("⊞ All Apps & Sites", ButtonTone.Quiet) { Selected = true };
        _tabAll.Click += (_, _) => SelectTab(ShieldFilterTab.All);

        _tabAllowed = new MinimalButton("✓ Allowed", ButtonTone.Quiet);
        _tabAllowed.Click += (_, _) => SelectTab(ShieldFilterTab.Allowed);

        _tabBlocked = new MinimalButton("⊘ Blocked", ButtonTone.Quiet);
        _tabBlocked.Click += (_, _) => SelectTab(ShieldFilterTab.Blocked);

        _tabSettings = new MinimalButton("⚙ Settings", ButtonTone.Quiet);
        _tabSettings.Click += (_, _) => SelectTab(ShieldFilterTab.Settings);

        _tabBar.Controls.Add(_tabAll);
        _tabBar.Controls.Add(_tabAllowed);
        _tabBar.Controls.Add(_tabBlocked);
        _tabBar.Controls.Add(_tabSettings);

        // 3. Catalog Page (All / Allowed / Blocked)
        _catalogPage = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(16, 12, 16, 16)
        };

        var actionBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 4,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 12)
        };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var searchBoxWrap = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 1,
            RowCount = 2,
            Margin = new Padding(0, 0, 10, 0)
        };
        searchBoxWrap.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        _lblSearchPlaceholder = new Label
        {
            Text = "Search apps or websites",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
            Font = DesignTokens.CreateUiFont(8.25f)
        };
        _txtSearch = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "Type to filter...",
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0)
        };
        _txtSearch.TextChanged += (_, _) => RenderFilteredCatalog();
        searchBoxWrap.Controls.Add(_lblSearchPlaceholder, 0, 0);
        searchBoxWrap.Controls.Add(_txtSearch, 0, 1);

        var addSitePanel = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            ColumnCount = 2,
            RowCount = 2,
            Margin = new Padding(0, 0, 10, 0)
        };
        addSitePanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        addSitePanel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _lblAddSitePlaceholder = new Label
        {
            Text = "Add domain to block",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 2),
            Font = DesignTokens.CreateUiFont(8.25f)
        };
        _txtAddSite = new TextBox
        {
            Dock = DockStyle.Fill,
            PlaceholderText = "whatsapp.com",
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 6, 0)
        };
        _txtAddSite.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.SuppressKeyPress = true;
                AddCustomBlockedSite(_txtAddSite.Text);
            }
        };

        _btnAddSite = new MinimalButton("+ Block Site", ButtonTone.Quiet) { Anchor = AnchorStyles.Bottom };
        _btnAddSite.Click += (_, _) => AddCustomBlockedSite(_txtAddSite.Text);

        addSitePanel.Controls.Add(_lblAddSitePlaceholder, 0, 0);
        addSitePanel.SetColumnSpan(_lblAddSitePlaceholder, 2);
        addSitePanel.Controls.Add(_txtAddSite, 0, 1);
        addSitePanel.Controls.Add(_btnAddSite, 1, 1);

        _btnBulkBlock = new MinimalButton("⊘ Block Selected", ButtonTone.Accent) { Anchor = AnchorStyles.Bottom };
        _btnBulkBlock.Click += (_, _) => BulkUpdateSelected(block: true);

        _btnBulkAllow = new MinimalButton("✓ Allow Selected", ButtonTone.Quiet) { Anchor = AnchorStyles.Bottom };
        _btnBulkAllow.Click += (_, _) => BulkUpdateSelected(block: false);

        actionBar.Controls.Add(searchBoxWrap, 0, 0);
        actionBar.Controls.Add(addSitePanel, 1, 0);
        actionBar.Controls.Add(_btnBulkBlock, 2, 0);
        actionBar.Controls.Add(_btnBulkAllow, 3, 0);

        _listView = new DoubleBufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            CheckBoxes = true,
            FullRowSelect = true,
            OwnerDraw = true,
            HeaderStyle = ColumnHeaderStyle.Nonclickable,
            BorderStyle = BorderStyle.None,
            MultiSelect = true,
            Font = DesignTokens.CreateUiFont(9.5f)
        };
        _listView.DrawColumnHeader += OnDrawListColumnHeader;
        _listView.DrawItem += (_, e) => e.DrawDefault = true;
        _listView.DrawSubItem += (_, e) => e.DrawDefault = true;
        _listView.Columns.Add("App / Site", 220);
        _listView.Columns.Add("Type", 76);
        _listView.Columns.Add("Status", 104);
        _listView.Columns.Add("Details", 420);
        _listView.DoubleClick += (_, _) => ToggleFocusedListItem();
        _listView.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                e.Handled = true;
                ToggleFocusedListItem();
            }
        };
        _listView.Resize += (_, _) =>
        {
            if (!_isDraggingOrResizing)
            {
                FitListColumns();
            }
        };

        _lblEmptyHint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 26,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(2, 4, 2, 0),
            Font = DesignTokens.CreateUiFont(8.75f),
            Text = "Unblocked items are automatically Allowed. Check rows to bulk block/allow, or double-click any row."
        };

        _catalogPage.Controls.Add(_listView);
        _catalogPage.Controls.Add(_lblEmptyHint);
        _catalogPage.Controls.Add(actionBar);

        // 4. Inline Settings Page (3 organized sections + (?) plain-English help + full Activity Log)
        _settingsPage = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(20, 10, 20, 12),
            AutoScroll = false,
            Visible = false
        };

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 7,
            BackColor = Color.Transparent
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Section 1 header
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Protection toggles
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Section 2 header
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Behavior fields + tray/hotkey
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Tray + hotkey toggles
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize)); // Section 3 header + Copy/Clear
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f)); // Full Activity Log

        // Section 1: Protection
        settingsLayout.Controls.Add(CreateSectionHeader("Protection"), 0, 0);

        var protectionGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 8)
        };
        protectionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        protectionGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _chkExcludeSelf = CreateSettingsCheckBox("Hide ScreenShield from screenshots", _settings.ExcludeWrapperFromCapture);
        _chkTaskbarHide = CreateSettingsCheckBox("Auto-hide Windows taskbar", _settings.AlwaysHideTaskbarWhenBlockedOpen);
        _chkGuardEnabled = CreateSettingsCheckBox("Watch for blocked apps & sites", _settings.NativeGuardEnabled);
        _chkOnlyWhenUpwork = CreateSettingsCheckBox("Only protect while Upwork is running", _settings.OnlyActWhenUpworkRunning);

        protectionGrid.Controls.Add(WrapWithHelp(_chkExcludeSelf, "Makes the ScreenShield window itself invisible to Upwork and Windows screenshots while staying visible to you."), 0, 0);
        protectionGrid.Controls.Add(WrapWithHelp(_chkTaskbarHide, "Slides your bottom Windows taskbar out of sight while a blocked app or site is open so Upwork screenshots can't see its icon. Restores automatically when closed."), 1, 0);
        protectionGrid.Controls.Add(WrapWithHelp(_chkGuardEnabled, "Runs quietly in the background and reacts whenever any app or website on your Blocked list is open."), 0, 1);
        protectionGrid.Controls.Add(WrapWithHelp(_chkOnlyWhenUpwork, "Stays completely quiet when Upwork isn't open, so your apps and taskbar behave normally outside work hours."), 1, 1);
        settingsLayout.Controls.Add(protectionGrid, 0, 1);

        // Section 2: Behavior & Shortcuts
        settingsLayout.Controls.Add(CreateSectionHeader("Behavior & Shortcuts"), 0, 2);

        var fieldsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 4)
        };
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 46f));
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 32f));
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 22f));

        var lblAction = CreateLabelWithHelp("When a blocked app or site is open", "Choose what ScreenShield does if a blocked app or website is open while Upwork is running.");
        var lblUpwork = CreateLabelWithHelp("Time tracker app", "Process name of your time tracker (default: Upwork). Add multiple separated by commas.");
        var lblCooldown = CreateLabelWithHelp("Cooldown (sec)", "Seconds to wait before alerting again for the same window so you aren't spammed.");

        _cmbAction = new ComboBox
        {
            Dock = DockStyle.Fill,
            DropDownStyle = ComboBoxStyle.DropDownList,
            DrawMode = DrawMode.OwnerDrawFixed,
            FlatStyle = FlatStyle.Flat,
            ItemHeight = 22,
            Margin = new Padding(0, 2, 12, 0)
        };
        _cmbAction.DrawItem += OnDrawActionComboItem;
        _cmbAction.Items.AddRange(new object[]
        {
            "Warn me + hide taskbar (Recommended)",
            "Auto-minimize blocked window + hide taskbar",
            "Move blocked window to 2nd monitor + hide taskbar",
            "Pause Upwork Time Tracker + warn me"
        });
        _cmbAction.SelectedIndex = (int)_settings.NativeGuardAction;
        _cmbAction.SelectedIndexChanged += (_, _) => OnInlineSettingsChanged();

        _txtUpworkProcs = new TextBox
        {
            Dock = DockStyle.Fill,
            Text = string.Join(", ", _settings.UpworkProcessNames),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 12, 0)
        };
        _txtUpworkProcs.TextChanged += (_, _) => OnInlineSettingsChanged();

        _numCooldown = new NumericUpDown
        {
            Dock = DockStyle.Fill,
            Minimum = 1,
            Maximum = 300,
            Value = Math.Clamp(_settings.GuardCooldownSeconds, 1, 300),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 0, 0)
        };
        _numCooldown.ValueChanged += (_, _) => OnInlineSettingsChanged();

        fieldsGrid.Controls.Add(lblAction, 0, 0);
        fieldsGrid.Controls.Add(lblUpwork, 1, 0);
        fieldsGrid.Controls.Add(lblCooldown, 2, 0);
        fieldsGrid.Controls.Add(_cmbAction, 0, 1);
        fieldsGrid.Controls.Add(_txtUpworkProcs, 1, 1);
        fieldsGrid.Controls.Add(_numCooldown, 2, 1);
        settingsLayout.Controls.Add(fieldsGrid, 0, 3);

        var shortcutGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 8)
        };
        shortcutGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        shortcutGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _chkMinimizeToTray = CreateSettingsCheckBox("Keep running in system tray when closed", _settings.MinimizeToTrayOnClose);
        _chkGlobalHotkey = CreateSettingsCheckBox("Quick show/hide shortcut (Ctrl + Shift + H)", _settings.EnableGlobalHotkey);

        shortcutGrid.Controls.Add(WrapWithHelp(_chkMinimizeToTray, "Clicking X hides ScreenShield into the tray by your clock instead of turning off protection."), 0, 0);
        shortcutGrid.Controls.Add(WrapWithHelp(_chkGlobalHotkey, "Press Ctrl + Shift + H from anywhere on your PC to instantly show or hide ScreenShield."), 1, 0);
        settingsLayout.Controls.Add(shortcutGrid, 0, 4);

        // Section 3: Full Activity Log
        var logHeaderRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 4)
        };
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _btnCopyLog = new MinimalButton("⧉ Copy Log", ButtonTone.Quiet);
        _btnCopyLog.Click += (_, _) => CopyActivityLog();

        _btnClearLog = new MinimalButton("✕ Clear", ButtonTone.Quiet);
        _btnClearLog.Click += (_, _) => ClearActivityLog();

        logHeaderRow.Controls.Add(CreateSectionHeader("Activity Log"), 0, 0);
        logHeaderRow.Controls.Add(_btnCopyLog, 1, 0);
        logHeaderRow.Controls.Add(_btnClearLog, 2, 0);
        settingsLayout.Controls.Add(logHeaderRow, 0, 5);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.75f),
            BorderStyle = BorderStyle.FixedSingle,
            Margin = new Padding(0, 2, 0, 0)
        };
        settingsLayout.Controls.Add(_txtLog, 0, 6);
        _settingsPage.Controls.Add(settingsLayout);

        AppendLog("ScreenShield started · Shield active · Taskbar guard ready.");

        _sentinelOverlay = new Panel
        {
            Size = new Size(160, 80),
            Location = new Point(40, 96),
            BackColor = Color.Magenta,
            Visible = false
        };

        _helpPopover = new HelpPopover();

        Controls.Add(_sentinelOverlay);
        Controls.Add(_catalogPage);
        Controls.Add(_settingsPage);
        Controls.Add(_tabBar);
        Controls.Add(_headerBar);
        _sentinelOverlay.BringToFront();

        _trayIcon = new NotifyIcon
        {
            Icon = _shieldIcon,
            Text = "ScreenShield — Active",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => ToggleWindowVisibility();

        // Event-driven SetWinEventHook handles instant (<5ms) foreground/restore reactions;
        // 8s timer is a lightweight fallback for silent Upwork start/stop or browser title changes.
        _guardTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _guardTimer.Tick += (_, _) => TriggerNativeGuardSweepAsync();

        _affinityWatchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _affinityWatchdog.Tick += (_, _) => EnforceAffinityWatchdog();

        _screenshotWatcher.ScreenshotDetected += OnUpworkScreenshotDetected;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        ApplyTheme(_palette);
    }

    private void OnUpworkScreenshotDetected(UpworkScreenshotEvent evt)
    {
        string tbState = _taskbarShield.IsTaskbarCurrentlyShielded ? "Taskbar hidden" : "Taskbar normal";
        string shieldState = _settings.ExcludeWrapperFromCapture ? "Shield ACTIVE" : "Shield PAUSED";
        string msg = $"[Upwork SS #{evt.SessionCount}] Screenshot detected at {evt.DetectedAt.LocalDateTime:HH:mm:ss} ({evt.FileName}) · {shieldState} · {tbState}";

        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            AppendLog(msg);
            RefreshStatusLine();
        });
    }

    public void LoadCatalogSnapshot(IReadOnlyList<WindowSnapshot> windows, bool includeBrowserUia = false)
    {
        _allCatalogEntries = ActiveCatalogService.BuildCatalog(windows, _settings, Environment.ProcessId, includeBrowserUia);
        UpdateTabCounts();
        RenderFilteredCatalog();
    }

    public void SelectTab(ShieldFilterTab tab)
    {
        _helpPopover.HidePopover();
        _activeTab = tab;
        _tabAll.Selected = tab == ShieldFilterTab.All;
        _tabAllowed.Selected = tab == ShieldFilterTab.Allowed;
        _tabBlocked.Selected = tab == ShieldFilterTab.Blocked;
        _tabSettings.Selected = tab == ShieldFilterTab.Settings;

        bool isSettings = tab == ShieldFilterTab.Settings;
        _settingsPage.Visible = isSettings;
        _catalogPage.Visible = !isSettings;

        if (!isSettings)
        {
            RenderFilteredCatalog();
        }
    }

    public void SetSearchQuery(string query)
    {
        if (_txtSearch.Text != query)
        {
            _txtSearch.Text = query;
        }
        else
        {
            RenderFilteredCatalog();
        }
    }

    public bool AddCustomBlockedSite(string rawDomain)
    {
        string normalized = AppSettings.NormalizeDomainKey(rawDomain);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        bool changed = ActiveCatalogService.SetBlocked(_settings, ShieldItemKind.Site, normalized, block: true);
        _txtAddSite.Clear();
        if (changed)
        {
            RebuildCatalogBlockedFlags();
            ScheduleDebouncedSave();
            AppendLog($"Blocked site: {normalized}");
        }

        return changed;
    }

    public int BulkUpdateSelected(bool block)
    {
        var targets = new List<(ShieldItemKind, string)>();
        foreach (ListViewItem item in _listView.CheckedItems)
        {
            if (item.Tag is CatalogEntry entry)
            {
                targets.Add((entry.Kind, entry.Key));
            }
        }

        if (targets.Count == 0 && _listView.SelectedItems.Count > 0)
        {
            foreach (ListViewItem item in _listView.SelectedItems)
            {
                if (item.Tag is CatalogEntry entry)
                {
                    targets.Add((entry.Kind, entry.Key));
                }
            }
        }

        int changed = ActiveCatalogService.BulkSetBlocked(_settings, targets, block);
        if (changed > 0)
        {
            RebuildCatalogBlockedFlags();
            ScheduleDebouncedSave();
            AppendLog(block ? $"Blocked {changed} item(s)." : $"Allowed {changed} item(s).");
        }

        return changed;
    }

    public bool ToggleSingleItem(CatalogEntry entry)
    {
        bool nextBlocked = !entry.IsBlocked;
        bool changed = ActiveCatalogService.SetBlocked(_settings, entry.Kind, entry.Key, nextBlocked);
        if (changed)
        {
            RebuildCatalogBlockedFlags();
            ScheduleDebouncedSave();
            AppendLog(nextBlocked ? $"Blocked {entry.DisplayName}." : $"Allowed {entry.DisplayName}.");
        }

        return changed;
    }

    private void ToggleFocusedListItem()
    {
        if (_listView.FocusedItem?.Tag is CatalogEntry entry)
        {
            ToggleSingleItem(entry);
        }
    }

    private void RebuildCatalogBlockedFlags()
    {
        var blockedApps = new HashSet<string>(_settings.ProtectedProcessNames, StringComparer.OrdinalIgnoreCase);
        var blockedSites = new HashSet<string>(_settings.BlockedDomains, StringComparer.OrdinalIgnoreCase);

        var updated = new List<CatalogEntry>(_allCatalogEntries.Count + 4);
        var seenApps = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenSites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (CatalogEntry e in _allCatalogEntries)
        {
            bool isBlocked = e.Kind == ShieldItemKind.App
                ? blockedApps.Contains(e.Key)
                : blockedSites.Contains(e.Key) || ActiveCatalogService.MatchesBlockedSite(e.Detail, _settings.BlockedDomains);

            if (!e.IsActiveNow && !isBlocked)
            {
                continue; // Unblocked inactive entry drops cleanly out of list
            }

            updated.Add(e with { IsBlocked = isBlocked });
            if (e.Kind == ShieldItemKind.App) seenApps.Add(e.Key);
            else seenSites.Add(e.Key);
        }

        foreach (string domain in _settings.BlockedDomains)
        {
            if (seenSites.Add(domain))
            {
                updated.Add(new CatalogEntry(domain, domain, ShieldItemKind.Site, "Blocked domain", false, true));
            }
        }

        _allCatalogEntries = updated;
        UpdateTabCounts();
        RenderFilteredCatalog();
    }

    private void UpdateTabCounts()
    {
        int total = _allCatalogEntries.Count;
        int blocked = _allCatalogEntries.Count(static e => e.IsBlocked);
        int allowed = total - blocked;

        _tabAll.Text = $"⊞ All Apps & Sites ({total})";
        _tabAllowed.Text = $"✓ Allowed ({allowed})";
        _tabBlocked.Text = $"⊘ Blocked ({blocked})";
    }

    private void RenderFilteredCatalog()
    {
        _visibleEntries = ActiveCatalogService.Filter(_allCatalogEntries, _activeTab, _txtSearch.Text);

        var rows = new ListViewItem[_visibleEntries.Count];
        for (int i = 0; i < _visibleEntries.Count; i++)
        {
            CatalogEntry entry = _visibleEntries[i];
            var row = new ListViewItem(entry.DisplayName)
            {
                Tag = entry,
                UseItemStyleForSubItems = false
            };
            row.SubItems.Add(entry.Kind == ShieldItemKind.App ? "▢ App" : "◎ Site");
            var statusSub = row.SubItems.Add(entry.IsBlocked ? "● Blocked" : "✓ Allowed");
            statusSub.ForeColor = entry.IsBlocked ? _palette.DangerText : _palette.ShieldActiveText;
            var detailSub = row.SubItems.Add(entry.Detail);
            detailSub.ForeColor = _palette.TextSub;
            rows[i] = row;
        }

        _listView.BeginUpdate();
        try
        {
            _listView.Items.Clear();
            if (rows.Length > 0)
            {
                _listView.Items.AddRange(rows);
            }
        }
        finally
        {
            _listView.EndUpdate();
        }
    }

    private void FitListColumns()
    {
        if (_listView.Columns.Count < 4) return;
        int fixedWidth = _listView.Columns[0].Width + _listView.Columns[1].Width + _listView.Columns[2].Width;
        _listView.Columns[3].Width = Math.Max(180, _listView.ClientSize.Width - fixedWidth);
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.ContentSurface;
        ForeColor = palette.TextPrimary;

        _headerBar.BackColor = palette.BaseSurface;
        _tabBar.BackColor = palette.BaseSurface;
        _lblBrandName.ForeColor = palette.TextPrimary;
        _lblGuardStatus.ForeColor = palette.TextSub;
        _brandLogo.Invalidate();

        _catalogPage.BackColor = palette.ContentSurface;
        _settingsPage.BackColor = palette.ContentSurface;
        _listView.BackColor = palette.ContentSurface;
        _listView.ForeColor = palette.TextPrimary;
        _lblEmptyHint.ForeColor = palette.TextSub;

        _txtSearch.BackColor = palette.InputSurface;
        _txtSearch.ForeColor = palette.TextPrimary;
        _lblSearchPlaceholder.BackColor = palette.ContentSurface;
        _lblSearchPlaceholder.ForeColor = palette.TextSub;
        _txtAddSite.BackColor = palette.InputSurface;
        _txtAddSite.ForeColor = palette.TextPrimary;
        _lblAddSitePlaceholder.BackColor = palette.ContentSurface;
        _lblAddSitePlaceholder.ForeColor = palette.TextSub;

        _chkExcludeSelf.ForeColor = palette.TextPrimary;
        _chkTaskbarHide.ForeColor = palette.TextPrimary;
        _chkGuardEnabled.ForeColor = palette.TextPrimary;
        _chkOnlyWhenUpwork.ForeColor = palette.TextPrimary;
        _chkMinimizeToTray.ForeColor = palette.TextPrimary;
        _chkGlobalHotkey.ForeColor = palette.TextPrimary;
        foreach (Label lbl in _settingsSubLabels) lbl.ForeColor = palette.TextSub;
        foreach (Label hdr in _settingsSectionHeaders) hdr.ForeColor = palette.TextPrimary;
        foreach (HelpBadge badge in _helpBadges) badge.ApplyTheme(palette);
        _helpPopover.ApplyTheme(palette);

        _cmbAction.BackColor = palette.InputSurface;
        _cmbAction.ForeColor = palette.TextPrimary;
        _txtUpworkProcs.BackColor = palette.InputSurface;
        _txtUpworkProcs.ForeColor = palette.TextPrimary;
        _numCooldown.BackColor = palette.InputSurface;
        _numCooldown.ForeColor = palette.TextPrimary;
        _txtLog.BackColor = palette.BaseSurface;
        _txtLog.ForeColor = palette.TextPrimary;

        foreach (Control ctrl in InteractiveControls)
        {
            if (ctrl is MinimalButton btn)
            {
                btn.SurfaceOverride = btn == _btnAddSite || btn == _btnBulkBlock || btn == _btnBulkAllow || btn == _btnCopyLog || btn == _btnClearLog
                    ? palette.ContentSurface
                    : palette.BaseSurface;
                btn.ApplyTheme(palette);
            }
        }

        RenderFilteredCatalog();

        if (IsHandleCreated)
        {
            Win32Interop.ApplyWindows11Chrome(Handle, palette.IsDark, palette.BaseSurface, palette.TextPrimary);
            Win32Interop.ApplyDarkScrollbarTheme(_listView.Handle, palette.IsDark);
            Win32Interop.ApplyDarkScrollbarTheme(_txtLog.Handle, palette.IsDark);
        }
    }

    private void OnSystemPreferenceChanged(object? sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category != UserPreferenceCategory.General || IsDisposed) return;
        ThemePalette next = DesignTokens.SystemDefault();
        if (next.IsDark != _palette.IsDark)
        {
            BeginInvoke(() => ApplyTheme(next));
        }
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open ScreenShield (Ctrl+Shift+H)", null, (_, _) => ToggleWindowVisibility());
        _trayLastSsItem = new ToolStripMenuItem(_screenshotWatcher.FormatLastScreenshot(DateTimeOffset.UtcNow)) { Enabled = false };
        menu.Items.Add(_trayLastSsItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Toggle Shield (Ctrl+Shift+S)", null, (_, _) => ToggleCaptureExclusion());
        menu.Items.Add("Verify Protection Now (Ctrl+Shift+V)", null, async (_, _) => await RunLiveCaptureVerificationAsync());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit ScreenShield", null, (_, _) =>
        {
            _forceExit = true;
            Close();
        });
        return menu;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        Win32Interop.ApplyWindows11Chrome(Handle, _palette.IsDark, _palette.BaseSurface, _palette.TextPrimary);
        Win32Interop.ApplyDarkScrollbarTheme(_listView.Handle, _palette.IsDark);
        Win32Interop.ApplyDarkScrollbarTheme(_txtLog.Handle, _palette.IsDark);
        ApplyCaptureExclusionState();
        SyncGlobalHotkey();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        ApplyCaptureExclusionState();
        InstallWinEventHooks();
        if (_autoRefreshOnShow)
        {
            RefreshCatalogAsync();
        }

        _guardTimer.Start();
        _affinityWatchdog.Start();
    }

    protected override void OnResizeBegin(EventArgs e)
    {
        _isDraggingOrResizing = true;
        _helpPopover.HidePopover();
        SuspendLayout();
        base.OnResizeBegin(e);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);
        _isDraggingOrResizing = false;
        ResumeLayout(performLayout: true);
        FitListColumns();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (WindowState != _lastWindowState)
        {
            _lastWindowState = WindowState;
            _helpPopover.HidePopover();
            if (WindowState is FormWindowState.Maximized or FormWindowState.Normal)
            {
                FitListColumns();
            }
            else if (WindowState == FormWindowState.Minimized)
            {
                Win32Interop.TrimWorkingSetMemory();
            }
        }
    }

    private void InstallWinEventHooks()
    {
        if (_foregroundHook == IntPtr.Zero)
        {
            _foregroundHook = Win32Interop.SetWinEventHook(
                Win32Interop.EVENT_SYSTEM_FOREGROUND,
                Win32Interop.EVENT_SYSTEM_FOREGROUND,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                Win32Interop.WINEVENT_OUTOFCONTEXT);
        }

        if (_minimizeEndHook == IntPtr.Zero)
        {
            _minimizeEndHook = Win32Interop.SetWinEventHook(
                Win32Interop.EVENT_SYSTEM_MINIMIZEEND,
                Win32Interop.EVENT_SYSTEM_MINIMIZEEND,
                IntPtr.Zero,
                _winEventCallback,
                0,
                0,
                Win32Interop.WINEVENT_OUTOFCONTEXT);
        }
    }

    private void UninstallWinEventHooks()
    {
        if (_foregroundHook != IntPtr.Zero)
        {
            _ = Win32Interop.UnhookWinEvent(_foregroundHook);
            _foregroundHook = IntPtr.Zero;
        }

        if (_minimizeEndHook != IntPtr.Zero)
        {
            _ = Win32Interop.UnhookWinEvent(_minimizeEndHook);
            _minimizeEndHook = IntPtr.Zero;
        }
    }

    private void OnWinEventTriggered(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (IsDisposed || _isDraggingOrResizing || hwnd == Handle)
        {
            return;
        }

        TriggerNativeGuardSweepAsync();
    }

    private void OnDrawListColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bgBrush = new SolidBrush(_palette.BaseSurface);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        Rectangle textBounds = new(e.Bounds.Left + 6, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            e.Header?.Text ?? string.Empty,
            _headerColumnFont,
            textBounds,
            _palette.TextSub,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.D1)) { SelectTab(ShieldFilterTab.All); return true; }
        if (keyData == (Keys.Control | Keys.D2)) { SelectTab(ShieldFilterTab.Allowed); return true; }
        if (keyData == (Keys.Control | Keys.D3)) { SelectTab(ShieldFilterTab.Blocked); return true; }
        if (keyData == (Keys.Control | Keys.D4)) { SelectTab(ShieldFilterTab.Settings); return true; }
        if (keyData == (Keys.Control | Keys.F)) { SelectTab(ShieldFilterTab.All); _txtSearch.Focus(); return true; }
        if (keyData == Keys.F5) { RefreshCatalogAsync(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.S)) { ToggleCaptureExclusion(); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.V)) { _ = RunLiveCaptureVerificationAsync(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Win32Interop.WM_HOTKEY && m.WParam.ToInt32() == HotkeyId)
        {
            ToggleWindowVisibility();
            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_forceExit && _settings.MinimizeToTrayOnClose && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            _helpPopover.HidePopover();
            Hide();
            Win32Interop.TrimWorkingSetMemory();
            _trayIcon.ShowBalloonTip(
                2000,
                "ScreenShield is running in the tray",
                "Press Ctrl+Shift+H or double-click the tray icon to reopen.",
                ToolTipIcon.Info);
            return;
        }

        UninstallWinEventHooks();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _screenshotWatcher.ScreenshotDetected -= OnUpworkScreenshotDetected;
        _screenshotWatcher.Dispose();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _guardTimer.Stop();
        _guardTimer.Dispose();
        _affinityWatchdog.Stop();
        _affinityWatchdog.Dispose();
        UnregisterGlobalHotkey();
        _taskbarShield.Dispose(); // Always restore user's original Windows taskbar setting
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _helpPopover.Dispose();
        _toolTip.Dispose();
        _shieldIcon.Dispose();
        _headerColumnFont.Dispose();
        base.OnFormClosing(e);
    }

    public AffinityResult ApplyCaptureExclusionState()
    {
        AffinityResult result = _settings.ExcludeWrapperFromCapture
            ? _captureService.EnableExcludeFromCapture(Handle)
            : _captureService.DisableExcludeFromCapture(Handle);

        bool active = _settings.ExcludeWrapperFromCapture
            && result.Success
            && result.ActualAffinity == Win32Interop.WDA_EXCLUDEFROMCAPTURE;

        _helpPopover.SetCaptureExclusion(active);

        if (active)
        {
            _btnShieldPill.Text = "● Protected";
            _btnShieldPill.Tone = ButtonTone.ShieldOn;
            _trayIcon.Text = "ScreenShield — Protected";
        }
        else if (!_settings.ExcludeWrapperFromCapture)
        {
            _btnShieldPill.Text = "○ Paused";
            _btnShieldPill.Tone = ButtonTone.ShieldOff;
            _trayIcon.Text = "ScreenShield — Paused";
        }
        else
        {
            _btnShieldPill.Text = "✕ Shield Error";
            _btnShieldPill.Tone = ButtonTone.ShieldError;
            _trayIcon.Text = "ScreenShield — Error";
        }

        return result;
    }

    public void ToggleCaptureExclusion()
    {
        _settings.ExcludeWrapperFromCapture = !_settings.ExcludeWrapperFromCapture;
        _suppressSettingsEvents = true;
        _chkExcludeSelf.Checked = _settings.ExcludeWrapperFromCapture;
        _suppressSettingsEvents = false;
        ApplyCaptureExclusionState();
        ScheduleDebouncedSave();
    }

    public void RefreshCatalogAsync()
    {
        if (Interlocked.Exchange(ref _refreshInFlight, 1) == 1) return;

        _ = Task.Run(() =>
        {
            try
            {
                IReadOnlyList<WindowSnapshot> windows = _windowInspector.GetTopLevelWindows();
                IReadOnlyList<CatalogEntry> entries = ActiveCatalogService.BuildCatalog(
                    windows,
                    _settings,
                    Environment.ProcessId,
                    includeBrowserUiaTabs: true);

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() =>
                    {
                        _allCatalogEntries = entries;
                        UpdateTabCounts();
                        RenderFilteredCatalog();
                    });
                }
            }
            finally
            {
                Interlocked.Exchange(ref _refreshInFlight, 0);
            }
        });
    }

    private void EnforceAffinityWatchdog()
    {
        if (!_settings.ExcludeWrapperFromCapture || !IsHandleCreated) return;
        AffinityResult current = _captureService.ReadAffinity(Handle);
        if (!current.Success || current.ActualAffinity != Win32Interop.WDA_EXCLUDEFROMCAPTURE)
        {
            ApplyCaptureExclusionState();
        }
    }

    private async Task RunLiveCaptureVerificationAsync()
    {
        if (!Visible || WindowState == FormWindowState.Minimized)
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
        }

        _btnVerify.Enabled = false;
        try
        {
            AffinityResult affinity = _captureService.ReadAffinity(Handle);
            _sentinelOverlay.Visible = true;
            _sentinelOverlay.BringToFront();
            _sentinelOverlay.Refresh();
            await Task.Delay(120);

            Point topLeft = _sentinelOverlay.PointToScreen(Point.Empty);
            CaptureVerificationResult capture = _captureService.VerifyRegionExcludesColor(
                new Rectangle(topLeft, _sentinelOverlay.Size),
                Color.Magenta);

            _sentinelOverlay.Visible = false;

            bool affinityOk = affinity.Success && affinity.ActualAffinity == Win32Interop.WDA_EXCLUDEFROMCAPTURE;
            bool fullyVerified = affinityOk && capture.Passed;

            string report =
                $"Shield Flag: {(affinityOk ? "ACTIVE (0x11)" : $"OFF (0x{affinity.ActualAffinity:X})")}\n" +
                $"Taskbar Auto-Hide Shield: {(_settings.AlwaysHideTaskbarWhenBlockedOpen ? "ENABLED" : "OFF")}\n" +
                $"Screenshot Check: {capture.Summary}";

            AppendLog($"[Verify] {capture.Summary}");
            MessageBox.Show(
                report,
                fullyVerified ? "ScreenShield — Verified Hidden" : "ScreenShield — Check Result",
                MessageBoxButtons.OK,
                fullyVerified ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
        }
        finally
        {
            _sentinelOverlay.Visible = false;
            _btnVerify.Enabled = true;
        }
    }

    private void TriggerNativeGuardSweepAsync()
    {
        if (_isDraggingOrResizing || Interlocked.Exchange(ref _guardInFlight, 1) == 1) return;

        AppSettings snap = _settings;
        _ = Task.Run(() =>
        {
            try
            {
                DesktopSnapshot desktop = _windowInspector.CaptureDesktopSnapshot();
                GuardEvaluation eval = _guardEngine.Evaluate(
                    desktop.Windows,
                    desktop.RunningProcessNames,
                    desktop.Screens,
                    snap,
                    DateTimeOffset.UtcNow);

                _taskbarShield.ApplyTaskbarShield(eval.ShouldHideTaskbar);

                if (!IsDisposed && IsHandleCreated)
                {
                    BeginInvoke(() => ApplyGuardEvaluationOnUi(eval, desktop.Windows, snap));
                }
            }
            finally
            {
                Interlocked.Exchange(ref _guardInFlight, 0);
            }
        });
    }

    private void ApplyGuardEvaluationOnUi(GuardEvaluation eval, IReadOnlyList<WindowSnapshot> windows, AppSettings settings)
    {
        if (IsDisposed) return;

        if (eval.UpworkRunning != _lastUpworkRunning)
        {
            AppendLog(eval.UpworkRunning
                ? "[Upwork] Upwork Time Tracker detected running."
                : "[Upwork] Upwork Time Tracker stopped.");
        }

        _lastUpworkRunning = eval.UpworkRunning;
        _lastExposedCount = eval.ExposedNativeWindows.Count;
        RefreshStatusLine();

        foreach (GuardActionItem item in eval.Actions)
        {
            AppendLog($"[Guard] {item.Reason}");
            switch (item.EffectiveAction)
            {
                case NativeGuardAction.AutoMinimize:
                    _ = _windowInspector.MinimizeWindow(item.Window.Handle);
                    _trayIcon.ShowBalloonTip(2200, "ScreenShield minimized blocked app", item.Reason, ToolTipIcon.Info);
                    break;
                case NativeGuardAction.MoveToSecondaryMonitor when item.TargetScreen is not null:
                    _ = _windowInspector.MoveWindowToScreen(item.Window.Handle, item.Window.Bounds, item.TargetScreen);
                    break;
                case NativeGuardAction.PauseUpworkAndAlert:
                    UpworkPauseResult pause = _upworkAutomation.TryPauseUpworkTracker(windows, settings.UpworkProcessNames);
                    AppendLog($"[Upwork] {pause.Message}");
                    _trayIcon.ShowBalloonTip(3000, "Blocked app/site open", $"{item.Reason}\n{pause.Message}", ToolTipIcon.Warning);
                    break;
                default:
                    _trayIcon.ShowBalloonTip(3000, "Blocked app/site open", item.Reason, ToolTipIcon.Warning);
                    break;
            }
        }
    }

    private void OnInlineSettingsChanged()
    {
        if (_suppressSettingsEvents) return;

        _settings.ExcludeWrapperFromCapture = _chkExcludeSelf.Checked;
        _settings.AlwaysHideTaskbarWhenBlockedOpen = _chkTaskbarHide.Checked;
        _settings.NativeGuardEnabled = _chkGuardEnabled.Checked;
        _settings.OnlyActWhenUpworkRunning = _chkOnlyWhenUpwork.Checked;
        _settings.NativeGuardAction = (NativeGuardAction)Math.Clamp(_cmbAction.SelectedIndex, 0, 3);
        _settings.UpworkProcessNames = _txtUpworkProcs.Text
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.GuardCooldownSeconds = (int)_numCooldown.Value;
        _settings.MinimizeToTrayOnClose = _chkMinimizeToTray.Checked;
        _settings.EnableGlobalHotkey = _chkGlobalHotkey.Checked;
        _settings.Normalize();

        if (!_settings.AlwaysHideTaskbarWhenBlockedOpen)
        {
            _taskbarShield.RestoreOriginalState();
        }

        _guardEngine.ResetCooldowns();
        ApplyCaptureExclusionState();
        SyncGlobalHotkey();
        ScheduleDebouncedSave();
    }

    public void ToggleWindowVisibility()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            Hide();
        }
        else
        {
            Show();
            WindowState = FormWindowState.Normal;
            Activate();
            ApplyCaptureExclusionState();
        }
    }

    private void SyncGlobalHotkey()
    {
        UnregisterGlobalHotkey();
        if (_settings.EnableGlobalHotkey && IsHandleCreated)
        {
            _hotkeyRegistered = Win32Interop.RegisterHotKey(
                Handle,
                HotkeyId,
                Win32Interop.MOD_CONTROL | Win32Interop.MOD_SHIFT | Win32Interop.MOD_NOREPEAT,
                VkH);
        }
    }

    private void UnregisterGlobalHotkey()
    {
        if (_hotkeyRegistered && IsHandleCreated)
        {
            _ = Win32Interop.UnregisterHotKey(Handle, HotkeyId);
            _hotkeyRegistered = false;
        }
    }

    private void OnDrawActionComboItem(object? sender, DrawItemEventArgs e)
    {
        if (e.Index < 0 || e.Index >= _cmbAction.Items.Count) return;

        bool selected = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
        Color bg = selected ? _palette.SelectedFill : _palette.InputSurface;
        using var bgBrush = new SolidBrush(bg);
        e.Graphics.FillRectangle(bgBrush, e.Bounds);

        string text = _cmbAction.Items[e.Index]?.ToString() ?? string.Empty;
        Rectangle textRect = new(e.Bounds.Left + 6, e.Bounds.Top, Math.Max(0, e.Bounds.Width - 10), e.Bounds.Height);
        TextRenderer.DrawText(
            e.Graphics,
            text,
            _cmbAction.Font,
            textRect,
            _palette.TextPrimary,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
    }

    public void CopyActivityLog()
    {
        if (!string.IsNullOrEmpty(_txtLog.Text))
        {
            try { Clipboard.SetText(_txtLog.Text); }
            catch { /* Ignore clipboard contention in headless tests */ }
            AppendLog("Activity log copied to clipboard.");
        }
    }

    public void ClearActivityLog()
    {
        _txtLog.Clear();
    }

    private Label CreateSectionHeader(string title)
    {
        var lbl = new Label
        {
            Text = title,
            AutoSize = true,
            UseMnemonic = false,
            Font = DesignTokens.CreateUiFont(9.5f, semibold: true),
            Margin = new Padding(0, 2, 0, 4)
        };
        _settingsSectionHeaders.Add(lbl);
        return lbl;
    }

    private FlowLayoutPanel WrapWithHelp(Control control, string helpText)
    {
        var row = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 1, 8, 2)
        };
        var badge = new HelpBadge(
            helpText,
            (b, text) => _helpPopover.ShowFor(b, text, this),
            () => _helpPopover.HidePopover());
        _helpBadges.Add(badge);
        row.Controls.Add(control);
        row.Controls.Add(badge);
        return row;
    }

    private void ScheduleDebouncedSave()
    {
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        var cts = new CancellationTokenSource();
        _saveDebounceCts = cts;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(120, cts.Token).ConfigureAwait(false);
                await _settings.SaveAsync().ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Coalesced by a newer save request.
            }
        });
    }

    private FlowLayoutPanel CreateLabelWithHelp(string text, string helpText)
    {
        var lbl = CreateSettingsSubLabel(text);
        lbl.Margin = new Padding(0, 4, 2, 0);
        return WrapWithHelp(lbl, helpText);
    }

    private CheckBox CreateSettingsCheckBox(string text, bool isChecked)
    {
        var chk = new CheckBox
        {
            Text = text,
            Checked = isChecked,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 3, 2, 2)
        };
        chk.CheckedChanged += (_, _) => OnInlineSettingsChanged();
        return chk;
    }

    private Label CreateSettingsSubLabel(string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Margin = new Padding(0, 6, 0, 2)
        };
        _settingsSubLabels.Add(lbl);
        return lbl;
    }

    private void AppendLog(string message)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {message}";
        if (_txtLog.IsDisposed) return;
        if (_txtLog.InvokeRequired)
        {
            _txtLog.BeginInvoke(() => AppendLog(message));
            return;
        }

        _txtLog.AppendText(line + Environment.NewLine);
    }

    private void RefreshStatusLine()
    {
        string lastSs = _screenshotWatcher.FormatLastScreenshot(DateTimeOffset.UtcNow);
        if (_trayLastSsItem is not null)
        {
            _trayLastSsItem.Text = lastSs;
        }

        string tbText = _taskbarShield.IsTaskbarCurrentlyShielded ? " · Taskbar hidden" : string.Empty;
        if (_lastUpworkRunning && _lastExposedCount > 0)
        {
            _lblGuardStatus.Text = $"Upwork tracking · {_lastExposedCount} blocked open · {lastSs}{tbText}";
            _lblGuardStatus.ForeColor = _palette.DangerText;
        }
        else if (_lastUpworkRunning)
        {
            _lblGuardStatus.Text = $"Upwork tracking · {lastSs}{tbText}";
            _lblGuardStatus.ForeColor = _palette.TextSub;
        }
        else
        {
            _lblGuardStatus.Text = _lastExposedCount > 0
                ? $"Upwork off · {_lastExposedCount} blocked open · {lastSs}"
                : $"Upwork off · {lastSs}";
            _lblGuardStatus.ForeColor = _palette.TextSub;
        }
    }

    private void OnPaintBrandLogo(object? sender, PaintEventArgs e)
    {
        Graphics g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(_palette.BaseSurface);

        using var path = new GraphicsPath();
        path.AddLine(12f, 2f, 21f, 5.5f);
        path.AddBezier(21f, 5.5f, 21f, 14f, 16.5f, 19.5f, 12f, 22f);
        path.AddBezier(12f, 22f, 7.5f, 19.5f, 3f, 14f, 3f, 5.5f);
        path.CloseFigure();

        using (var brush = new LinearGradientBrush(
            new RectangleF(0, 0, 24, 24),
            Color.FromArgb(52, 211, 153),
            Color.FromArgb(16, 185, 129),
            LinearGradientMode.ForwardDiagonal))
        {
            g.FillPath(brush, path);
        }

        using var checkPen = new Pen(Color.White, 2.2f)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };
        g.DrawLines(checkPen, [new PointF(7.8f, 12.2f), new PointF(10.8f, 15.2f), new PointF(16.4f, 9.2f)]);
    }

    private static Icon LoadOrCreateShieldIcon()
    {
        string icoPath = Path.Combine(AppContext.BaseDirectory, "Assets", "screenshield.ico");
        if (File.Exists(icoPath))
        {
            try
            {
                return new Icon(icoPath);
            }
            catch
            {
                // Fall back to GDI+ shield icon below.
            }
        }

        return CreateShieldIcon(active: true);
    }

    private static Icon CreateShieldIcon(bool active)
    {
        using var bmp = new Bitmap(32, 32);
        using (Graphics g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);

            Point[] shield =
            [
                new Point(16, 2),
                new Point(28, 7),
                new Point(26, 20),
                new Point(16, 30),
                new Point(6, 20),
                new Point(4, 7)
            ];

            using var brush = new SolidBrush(active ? Color.FromArgb(34, 197, 94) : Color.FromArgb(248, 113, 113));
            using var pen = new Pen(Color.White, 2f);
            g.FillPolygon(brush, shield);
            g.DrawPolygon(pen, shield);
        }

        IntPtr hIcon = bmp.GetHicon();
        try
        {
            using Icon temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone();
        }
        finally
        {
            _ = Win32Interop.DestroyIcon(hIcon);
        }
    }

    private sealed class DoubleBufferedListView : ListView
    {
        public DoubleBufferedListView()
        {
            DoubleBuffered = true;
        }
    }
}
