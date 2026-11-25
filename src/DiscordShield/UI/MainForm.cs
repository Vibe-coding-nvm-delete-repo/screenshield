using System.Drawing.Drawing2D;
using System.IO;
using DiscordShield.Core;
using DiscordShield.Native;
using Microsoft.Win32;

namespace DiscordShield.UI;

/// <summary>
/// ScreenShield — Tactile, ultra-snappy 5-tab Windows privacy shield:
///   All Apps & Sites | Allowed | Blocked | History | Settings
/// - Custom 144fps double-buffered tactile controls (zero clunky Win32 checkboxes, dropdowns, or listviews).
/// - Persistent, sortable Change & Screenshot History table with CSV export.
/// - Timed Snooze (15m / 30m / 1h) with live countdown & auto-resume.
/// - In-app GitHub Release version check & hot-swap updater.
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
    private readonly IStartupRegistrationService _startupService;
    private readonly HistoryStore _historyStore;
    private readonly bool _ownsHistoryStore;
    private readonly IAppUpdateService _updateService;
    private readonly WindowGuardEngine _guardEngine;
    private readonly bool _startMinimizedToTray;

    private AppSettings _settings;
    private ThemePalette _palette;
    private ShieldFilterTab _activeTab = ShieldFilterTab.All;
    private IReadOnlyList<CatalogEntry> _allCatalogEntries = [];
    private IReadOnlyList<CatalogEntry> _visibleEntries = [];
    private IReadOnlyList<HistoryEntry> _visibleHistoryEntries = [];
    private HistoryFilterCategory _historyFilter = HistoryFilterCategory.All;
    private HistorySortColumn _historySortBy = HistorySortColumn.Timestamp;
    private bool _historySortAsc;

    private bool _lastUpworkRunning;
    private int _lastExposedCount;
    private string _lastExposedNames = string.Empty;
    private bool _lastTaskbarShielded;

    // Timed Snooze state
    private DateTimeOffset? _snoozeUntil;
    private bool _excludeStateBeforeSnooze = true;
    private readonly System.Windows.Forms.Timer _snoozeTimer;
    private readonly ContextMenuStrip _snoozeMenu;

    private bool _forceExit;
    private bool _hotkeyRegistered;
    private bool _isDraggingOrResizing;
    private bool _shownTrayHint;
    private int _guardInFlight;
    private int _refreshInFlight;
    private bool _suppressSettingsEvents;
    private readonly bool _autoRefreshOnShow;
    private readonly Icon _shieldIcon;
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
    private readonly MinimalButton _btnSnooze;
    private readonly Label _lblGuardStatus;
    private readonly MinimalButton _btnRefresh;
    private readonly MinimalButton _btnVerify;
    private ToolStripMenuItem? _trayLastSsItem;

    private readonly FlowLayoutPanel _tabBar;
    private readonly MinimalButton _tabAll;
    private readonly MinimalButton _tabAllowed;
    private readonly MinimalButton _tabBlocked;
    private readonly MinimalButton _tabHistory;
    private readonly MinimalButton _tabSettings;

    // Catalog page (shared across All / Allowed / Blocked for sub-ms tab switches)
    private readonly Panel _catalogPage;
    private readonly RoundedInput _txtSearch;
    private readonly RoundedInput _txtAddSite;
    private readonly MinimalButton _btnAddSite;
    private readonly MinimalButton _btnBulkBlock;
    private readonly MinimalButton _btnBulkAllow;
    private readonly ShieldListControl _shieldList;
    private readonly Label _lblEmptyHint;

    // History page (persistent sortable table + filter pills + search + Export CSV)
    private readonly Panel _historyPage;
    private readonly SegmentedControl _segHistoryFilter;
    private readonly RoundedInput _txtHistorySearch;
    private readonly MinimalButton _btnExportCsv;
    private readonly MinimalButton _btnCopyHistory;
    private readonly MinimalButton _btnClearHistory;
    private readonly HistoryTableControl _historyTable;
    private readonly Label _lblHistoryHint;

    // Inline Settings page (3 tactile SurfaceCards)
    private readonly Panel _settingsPage;
    private readonly SurfaceCard _cardProtection;
    private readonly SurfaceCard _cardBehavior;
    private readonly SurfaceCard _cardLog;
    private readonly Label _lblVersionBadge;
    private readonly MinimalButton _btnCheckUpdate;
    private readonly TactileToggle _chkExcludeSelf;
    private readonly TactileToggle _chkTaskbarHide;
    private readonly TactileToggle _chkGuardEnabled;
    private readonly TactileToggle _chkOnlyWhenUpwork;
    private readonly SegmentedControl _segAction;
    private readonly RoundedInput _txtUpworkProcs;
    private readonly TactileStepper _stepCooldown;
    private readonly TactileToggle _chkMinimizeToTray;
    private readonly TactileToggle _chkGlobalHotkey;
    private readonly TactileToggle _chkRunAtStartup;
    private readonly TextBox _txtLog;
    private readonly MinimalButton _btnCopyLog;
    private readonly MinimalButton _btnClearLog;
    private readonly List<Label> _settingsSubLabels = [];
    private readonly List<Label> _settingsSectionHeaders = [];
    private readonly List<HelpBadge> _helpBadges = [];
    private readonly List<TactileToggle> _settingsToggles = [];

    private readonly ToolTip _toolTip;
    private readonly HelpPopover _helpPopover;
    private readonly Panel _sentinelOverlay;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _guardTimer;
    private readonly System.Windows.Forms.Timer _affinityWatchdog;

    public IReadOnlyList<Control> InteractiveControls =>
    [
        _btnShieldPill, _btnSnooze, _btnRefresh, _btnVerify,
        _tabAll, _tabAllowed, _tabBlocked, _tabHistory, _tabSettings,
        _btnAddSite, _btnBulkBlock, _btnBulkAllow,
        _shieldList,
        _segHistoryFilter, _btnExportCsv, _btnCopyHistory, _btnClearHistory, _historyTable,
        _btnCheckUpdate,
        _chkExcludeSelf, _chkTaskbarHide, _chkGuardEnabled, _chkOnlyWhenUpwork,
        _segAction, _stepCooldown,
        _chkMinimizeToTray, _chkGlobalHotkey, _chkRunAtStartup,
        _btnCopyLog, _btnClearLog
    ];

    public IReadOnlyList<HelpBadge> HelpBadges => _helpBadges;
    public HelpPopover HelpPopover => _helpPopover;
    public void ShowHelpForBadge(HelpBadge badge) => _helpPopover.ShowFor(badge, badge.HelpText, this);

    public ThemePalette CurrentPalette => _palette;
    public ShieldFilterTab ActiveTab => _activeTab;
    public IReadOnlyList<CatalogEntry> VisibleCatalogEntries => _visibleEntries;
    public IReadOnlyList<HistoryEntry> VisibleHistoryEntries => _visibleHistoryEntries;
    public HistoryStore HistoryStore => _historyStore;
    public bool IsSnoozed => _snoozeUntil.HasValue && _snoozeUntil.Value > DateTimeOffset.UtcNow;
    public DateTimeOffset? SnoozeUntil => _snoozeUntil;

    public MainForm(
        AppSettings settings,
        ICaptureExclusionService captureService,
        IWindowInspector windowInspector,
        IUpworkAutomationHelper upworkAutomation,
        ITaskbarShieldService? taskbarShield = null,
        ThemePalette? initialPalette = null,
        bool autoRefreshOnShow = true,
        IUpworkScreenshotWatcher? screenshotWatcher = null,
        IStartupRegistrationService? startupService = null,
        bool startMinimizedToTray = false,
        HistoryStore? historyStore = null,
        IAppUpdateService? updateService = null)
    {
        _settings = settings;
        _captureService = captureService;
        _windowInspector = windowInspector;
        _upworkAutomation = upworkAutomation;
        _taskbarShield = taskbarShield ?? new TaskbarShieldService();
        _screenshotWatcher = screenshotWatcher ?? new UpworkScreenshotWatcher();
        _startupService = startupService ?? new StartupRegistrationService();
        _ownsHistoryStore = historyStore is null;
        _historyStore = historyStore ?? new HistoryStore(HistoryStore.DefaultHistoryPath);
        _updateService = updateService ?? new AppUpdateService();
        _guardEngine = new WindowGuardEngine();
        _palette = initialPalette ?? DesignTokens.SystemDefault();
        _autoRefreshOnShow = autoRefreshOnShow;
        _startMinimizedToTray = startMinimizedToTray;
        _winEventCallback = OnWinEventTriggered;

        Text = "ScreenShield";
        Size = new Size(1080, 740);
        MinimumSize = new Size(860, 600);
        StartPosition = FormStartPosition.CenterScreen;
        ShowInTaskbar = true;
        if (_startMinimizedToTray) Opacity = 0;
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

        // Snooze dropdown menu (15m / 30m / 1h / Resume)
        _snoozeMenu = new ContextMenuStrip();
        _snoozeMenu.Items.Add("Snooze 15 minutes", null, (_, _) => SnoozeProtection(TimeSpan.FromMinutes(15)));
        _snoozeMenu.Items.Add("Snooze 30 minutes", null, (_, _) => SnoozeProtection(TimeSpan.FromMinutes(30)));
        _snoozeMenu.Items.Add("Snooze 1 hour", null, (_, _) => SnoozeProtection(TimeSpan.FromHours(1)));
        _snoozeMenu.Items.Add(new ToolStripSeparator());
        _snoozeMenu.Items.Add("Resume Protection Now", null, (_, _) => ResumeFromSnooze(expired: false));

        _snoozeTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _snoozeTimer.Tick += (_, _) => OnSnoozeTick();

        // 1. Tactile Header Bar
        _headerBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            MinimumSize = new Size(0, 58),
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(24, 16, 24, 8)
        };
        _headerBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
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
            Margin = new Padding(0, 1, 12, 0)
        };
        _brandLogo = new Panel
        {
            Size = new Size(24, 24),
            Margin = new Padding(0, 2, 8, 0),
            BackColor = Color.Transparent
        };
        _brandLogo.Paint += OnPaintBrandLogo;

        _lblBrandName = new Label
        {
            Text = "ScreenShield",
            AutoSize = true,
            Font = DesignTokens.CreateUiFont(11f, semibold: true),
            Padding = new Padding(0, 2, 0, 0),
            Margin = new Padding(0)
        };
        brandPanel.Controls.Add(_brandLogo);
        brandPanel.Controls.Add(_lblBrandName);

        _btnShieldPill = new MinimalButton("● Protected", ButtonTone.ShieldOn, pill: true) { Margin = new Padding(0, 0, 6, 0) };
        _btnShieldPill.Click += (_, _) =>
        {
            if (IsSnoozed) ResumeFromSnooze(expired: false);
            else ToggleCaptureExclusion();
        };
        _toolTip.SetToolTip(_btnShieldPill, "Toggle screenshot exclusion on ScreenShield (Ctrl+Shift+S)");

        _btnSnooze = new MinimalButton("Snooze ▾", ButtonTone.Secondary, pill: true)
        {
            Padding = new Padding(10, 5, 10, 5),
            Margin = new Padding(0, 0, 2, 0)
        };
        _btnSnooze.Click += (_, _) => _snoozeMenu.Show(_btnSnooze, new Point(0, _btnSnooze.Height + 2));
        _toolTip.SetToolTip(_btnSnooze, "Temporarily pause protection for 15m, 30m, or 1h with automatic re-arm");

        _lblGuardStatus = new Label
        {
            Dock = DockStyle.Fill,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(8, 0, 8, 0),
            Font = DesignTokens.CreateUiFont(8.75f),
            Text = "Upwork off · Last SS: none"
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

        _btnRefresh = new MinimalButton("↻ Refresh", ButtonTone.Secondary) { Margin = new Padding(0, 0, 6, 0) };
        _btnRefresh.Click += (_, _) =>
        {
            RefreshCatalogAsync();
            TriggerNativeGuardSweepAsync();
        };
        _toolTip.SetToolTip(_btnRefresh, "Scan active desktop apps & browser tabs (F5)");

        _btnVerify = new MinimalButton("✓ Verify", ButtonTone.Secondary);
        _btnVerify.Click += async (_, _) => await RunLiveCaptureVerificationAsync();
        _toolTip.SetToolTip(_btnVerify, "Run live Windows screenshot test (Ctrl+Shift+V)");

        headerRight.Controls.Add(_btnRefresh);
        headerRight.Controls.Add(_btnVerify);

        _headerBar.Controls.Add(brandPanel, 0, 0);
        _headerBar.Controls.Add(_btnShieldPill, 1, 0);
        _headerBar.Controls.Add(_btnSnooze, 2, 0);
        _headerBar.Controls.Add(_lblGuardStatus, 3, 0);
        _headerBar.Controls.Add(headerRight, 4, 0);

        // 2. 5-Tab Pill Navigation Bar
        _tabBar = new FlowLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            Padding = new Padding(22, 4, 22, 12)
        };

        _tabAll = new MinimalButton("⊞ All Apps & Sites", ButtonTone.Quiet) { Selected = true, Margin = new Padding(0, 0, 6, 0) };
        _tabAll.Click += (_, _) => SelectTab(ShieldFilterTab.All);

        _tabAllowed = new MinimalButton("✓ Allowed", ButtonTone.Quiet) { Margin = new Padding(0, 0, 6, 0) };
        _tabAllowed.Click += (_, _) => SelectTab(ShieldFilterTab.Allowed);

        _tabBlocked = new MinimalButton("⊘ Blocked", ButtonTone.Quiet) { Margin = new Padding(0, 0, 6, 0) };
        _tabBlocked.Click += (_, _) => SelectTab(ShieldFilterTab.Blocked);

        _tabHistory = new MinimalButton("⏱ History", ButtonTone.Quiet) { Margin = new Padding(0, 0, 6, 0) };
        _tabHistory.Click += (_, _) => SelectTab(ShieldFilterTab.History);

        _tabSettings = new MinimalButton("⚙ Settings", ButtonTone.Quiet) { Margin = new Padding(0) };
        _tabSettings.Click += (_, _) => SelectTab(ShieldFilterTab.Settings);

        _tabBar.Controls.Add(_tabAll);
        _tabBar.Controls.Add(_tabAllowed);
        _tabBar.Controls.Add(_tabBlocked);
        _tabBar.Controls.Add(_tabHistory);
        _tabBar.Controls.Add(_tabSettings);

        // 3. Catalog Page (All / Allowed / Blocked)
        _catalogPage = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 4, 24, 18)
        };

        var actionBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 14)
        };
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        actionBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _txtSearch = new RoundedInput("⌕", "Search apps or websites...")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 0, 12, 0)
        };
        _txtSearch.InputTextChanged += (_, _) => RenderFilteredCatalog();

        _txtAddSite = new RoundedInput("+", "Add domain to block (e.g. whatsapp.com)")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 0, 8, 0)
        };
        _txtAddSite.EnterPressed += (_, _) => AddCustomBlockedSite(_txtAddSite.InputText);

        _btnAddSite = new MinimalButton("+ Block Site", ButtonTone.Secondary)
        {
            Height = 36,
            Margin = new Padding(0, 0, 12, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnAddSite.Click += (_, _) => AddCustomBlockedSite(_txtAddSite.InputText);

        _btnBulkBlock = new MinimalButton("⊘ Block Selected", ButtonTone.Accent)
        {
            Height = 36,
            Margin = new Padding(0, 0, 8, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnBulkBlock.Click += (_, _) => BulkUpdateSelected(block: true);

        _btnBulkAllow = new MinimalButton("✓ Allow Selected", ButtonTone.Secondary)
        {
            Height = 36,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnBulkAllow.Click += (_, _) => BulkUpdateSelected(block: false);

        actionBar.Controls.Add(_txtSearch, 0, 0);
        actionBar.Controls.Add(_txtAddSite, 1, 0);
        actionBar.Controls.Add(_btnAddSite, 2, 0);
        actionBar.Controls.Add(_btnBulkBlock, 3, 0);
        actionBar.Controls.Add(_btnBulkAllow, 4, 0);

        _shieldList = new ShieldListControl
        {
            Dock = DockStyle.Fill
        };
        _shieldList.ItemToggleRequested += entry => ToggleSingleItem(entry);

        _lblEmptyHint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 24,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 4, 4, 0),
            Font = DesignTokens.CreateUiFont(8.5f),
            Text = "Click any switch to Block or Allow instantly · Check rows for bulk actions · Unblocked items stay Allowed."
        };

        _catalogPage.Controls.Add(_shieldList);
        _catalogPage.Controls.Add(_lblEmptyHint);
        _catalogPage.Controls.Add(actionBar);

        // 4. Persistent Sortable History Page (Screenshots, Guard, Block/Allow, Settings)
        _historyPage = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 4, 24, 18),
            Visible = false
        };

        var historyBar = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 5,
            RowCount = 1,
            Padding = new Padding(0, 0, 0, 14)
        };
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        historyBar.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _segHistoryFilter = new SegmentedControl(["All", "Screenshots", "Guard", "Changes"], 0)
        {
            Width = 428,
            Height = 36,
            Margin = new Padding(0, 0, 12, 0)
        };
        _segHistoryFilter.SelectedIndexChanged += (_, _) =>
        {
            SetHistoryFilter((HistoryFilterCategory)_segHistoryFilter.SelectedIndex);
        };

        _txtHistorySearch = new RoundedInput("⌕", "Search history...")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 0, 12, 0)
        };
        _txtHistorySearch.InputTextChanged += (_, _) => RenderHistoryTable();

        _btnExportCsv = new MinimalButton("↓ Export CSV", ButtonTone.Secondary)
        {
            Height = 36,
            Margin = new Padding(0, 0, 8, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnExportCsv.Click += (_, _) => ExportHistoryCsvWithDialog();

        _btnCopyHistory = new MinimalButton("⧉ Copy", ButtonTone.Secondary)
        {
            Height = 36,
            Margin = new Padding(0, 0, 8, 0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnCopyHistory.Click += (_, _) => CopyFilteredHistoryCsv();

        _btnClearHistory = new MinimalButton("✕ Clear", ButtonTone.Secondary)
        {
            Height = 36,
            Margin = new Padding(0),
            Anchor = AnchorStyles.Left | AnchorStyles.Right
        };
        _btnClearHistory.Click += (_, _) => ClearHistory();

        historyBar.Controls.Add(_segHistoryFilter, 0, 0);
        historyBar.Controls.Add(_txtHistorySearch, 1, 0);
        historyBar.Controls.Add(_btnExportCsv, 2, 0);
        historyBar.Controls.Add(_btnCopyHistory, 3, 0);
        historyBar.Controls.Add(_btnClearHistory, 4, 0);

        _historyTable = new HistoryTableControl
        {
            Dock = DockStyle.Fill
        };
        _historyTable.SortChanged += (col, asc) =>
        {
            _historySortBy = col;
            _historySortAsc = asc;
            RenderHistoryTable();
        };

        _lblHistoryHint = new Label
        {
            Dock = DockStyle.Bottom,
            AutoSize = false,
            Height = 24,
            AutoEllipsis = true,
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(4, 4, 4, 0),
            Font = DesignTokens.CreateUiFont(8.5f),
            Text = "Click any column header to sort · Saved automatically to disk · Tracks screenshots, guard actions, and changes."
        };

        _historyPage.Controls.Add(_historyTable);
        _historyPage.Controls.Add(_lblHistoryHint);
        _historyPage.Controls.Add(historyBar);

        // 5. Inline Settings Page (3 tactile SurfaceCards + Version/Update badge + 10 (?) help popovers)
        _settingsPage = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 4, 24, 18),
            AutoScroll = true,
            Visible = false
        };

        var settingsLayout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        settingsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // Card 1: Protection
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.AutoSize));     // Card 2: Behavior & Shortcuts
        settingsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));// Card 3: Activity Log

        // Card 1: Protection + Version/Update bar
        _cardProtection = new SurfaceCard
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };
        var protectionInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        protectionInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        protectionInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        var protectionHdrRow = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 0, 0, 2)
        };
        protectionHdrRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        protectionHdrRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        protectionHdrRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        Label hdrProtection = CreateSectionHeader("Protection");
        _lblVersionBadge = new Label
        {
            Text = _updateService.CurrentVersion,
            AutoSize = true,
            Font = DesignTokens.CreateUiFont(8.25f, semibold: true),
            Padding = new Padding(0, 5, 8, 0),
            Margin = new Padding(0)
        };
        _settingsSubLabels.Add(_lblVersionBadge);

        _btnCheckUpdate = new MinimalButton("↑ Check Update", ButtonTone.Secondary) { Margin = new Padding(0) };
        _btnCheckUpdate.Click += async (_, _) => await CheckForUpdatesInteractiveAsync();

        protectionHdrRow.Controls.Add(hdrProtection, 0, 0);
        protectionHdrRow.Controls.Add(_lblVersionBadge, 1, 0);
        protectionHdrRow.Controls.Add(_btnCheckUpdate, 2, 0);
        protectionInner.Controls.Add(protectionHdrRow, 0, 0);
        protectionInner.SetColumnSpan(protectionHdrRow, 2);

        _chkExcludeSelf = CreateSettingsToggle("Hide ScreenShield from screenshots", _settings.ExcludeWrapperFromCapture);
        _chkTaskbarHide = CreateSettingsToggle("Auto-hide Windows taskbar", _settings.AlwaysHideTaskbarWhenBlockedOpen);
        _chkGuardEnabled = CreateSettingsToggle("Watch for blocked apps & sites", _settings.NativeGuardEnabled);
        _chkOnlyWhenUpwork = CreateSettingsToggle("Only protect while Upwork is running", _settings.OnlyActWhenUpworkRunning);

        protectionInner.Controls.Add(WrapWithHelp(_chkExcludeSelf, "Makes the ScreenShield window itself invisible to Upwork and Windows screenshots while staying visible to you."), 0, 1);
        protectionInner.Controls.Add(WrapWithHelp(_chkTaskbarHide, "Slides your bottom Windows taskbar out of sight while a blocked app or site is open so Upwork screenshots can't see its icon. Restores automatically when closed."), 1, 1);
        protectionInner.Controls.Add(WrapWithHelp(_chkGuardEnabled, "Runs quietly in the background and reacts whenever any app or website on your Blocked list is open."), 0, 2);
        protectionInner.Controls.Add(WrapWithHelp(_chkOnlyWhenUpwork, "Stays completely quiet when Upwork isn't open, so your apps and taskbar behave normally outside work hours."), 1, 2);
        _cardProtection.Controls.Add(protectionInner);
        settingsLayout.Controls.Add(_cardProtection, 0, 0);

        // Card 2: Behavior & Shortcuts
        _cardBehavior = new SurfaceCard
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Margin = new Padding(0, 0, 0, 12)
        };
        var behaviorInner = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Color.Transparent
        };
        behaviorInner.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        behaviorInner.Controls.Add(CreateSectionHeader("Behavior & Shortcuts"), 0, 0);

        var fieldsGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0, 2, 0, 8)
        };
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58f));
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 24f));
        fieldsGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 18f));

        var lblAction = CreateLabelWithHelp("When a blocked app or site is open", "Choose what ScreenShield does if a blocked app or website is open while Upwork is running.");
        var lblUpwork = CreateLabelWithHelp("Time tracker app", "Process name of your time tracker (default: Upwork). Add multiple separated by commas.");
        var lblCooldown = CreateLabelWithHelp("Cooldown", "Seconds to wait before alerting again for the same window so you aren't spammed.");

        _segAction = new SegmentedControl(["Warn", "Minimize", "2nd Screen", "Pause"], (int)_settings.NativeGuardAction)
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 4, 14, 0)
        };
        _segAction.SelectedIndexChanged += (_, _) => OnInlineSettingsChanged();

        _txtUpworkProcs = new RoundedInput(string.Empty, "Upwork")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            InputText = string.Join(", ", _settings.UpworkProcessNames),
            Margin = new Padding(0, 4, 14, 0)
        };
        _txtUpworkProcs.InputTextChanged += (_, _) => OnInlineSettingsChanged();

        _stepCooldown = new TactileStepper(Math.Clamp(_settings.GuardCooldownSeconds, 1, 300), 1, 300, 5, "s")
        {
            Dock = DockStyle.Fill,
            Height = 36,
            Margin = new Padding(0, 4, 0, 0)
        };
        _stepCooldown.ValueChanged += (_, _) => OnInlineSettingsChanged();

        fieldsGrid.Controls.Add(lblAction, 0, 0);
        fieldsGrid.Controls.Add(lblUpwork, 1, 0);
        fieldsGrid.Controls.Add(lblCooldown, 2, 0);
        fieldsGrid.Controls.Add(_segAction, 0, 1);
        fieldsGrid.Controls.Add(_txtUpworkProcs, 1, 1);
        fieldsGrid.Controls.Add(_stepCooldown, 2, 1);
        behaviorInner.Controls.Add(fieldsGrid, 0, 1);

        var shortcutGrid = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 2,
            RowCount = 2,
            BackColor = Color.Transparent,
            Margin = new Padding(0)
        };
        shortcutGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));
        shortcutGrid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50f));

        _chkMinimizeToTray = CreateSettingsToggle("Keep running in system tray when closed", _settings.MinimizeToTrayOnClose);
        _chkGlobalHotkey = CreateSettingsToggle("Quick show/hide shortcut (Ctrl + Shift + H)", _settings.EnableGlobalHotkey);
        _chkRunAtStartup = CreateSettingsToggle("Start quietly in tray with Windows", _settings.RunAtWindowsStartup || _startupService.IsEnabled());

        shortcutGrid.Controls.Add(WrapWithHelp(_chkMinimizeToTray, "Clicking X hides ScreenShield into the tray by your clock instead of turning off protection."), 0, 0);
        shortcutGrid.Controls.Add(WrapWithHelp(_chkGlobalHotkey, "Press Ctrl + Shift + H from anywhere on your PC to instantly show or hide ScreenShield."), 1, 0);
        shortcutGrid.Controls.Add(WrapWithHelp(_chkRunAtStartup, "Automatically launches ScreenShield quietly in your system tray whenever you sign into Windows so you never forget to turn protection on."), 0, 1);
        behaviorInner.Controls.Add(shortcutGrid, 0, 2);

        _cardBehavior.Controls.Add(behaviorInner);
        settingsLayout.Controls.Add(_cardBehavior, 0, 1);

        // Card 3: Activity Log
        _cardLog = new SurfaceCard
        {
            Dock = DockStyle.Fill,
            Margin = new Padding(0)
        };
        var logHeaderRow = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            ColumnCount = 3,
            RowCount = 1,
            BackColor = Color.Transparent,
            Padding = new Padding(0, 0, 0, 6)
        };
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100f));
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        logHeaderRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        _btnCopyLog = new MinimalButton("⧉ Copy Log", ButtonTone.Secondary) { Margin = new Padding(0, 0, 6, 0) };
        _btnCopyLog.Click += (_, _) => CopyActivityLog();

        _btnClearLog = new MinimalButton("✕ Clear", ButtonTone.Secondary);
        _btnClearLog.Click += (_, _) => ClearActivityLog();

        logHeaderRow.Controls.Add(CreateSectionHeader("Activity Log"), 0, 0);
        logHeaderRow.Controls.Add(_btnCopyLog, 1, 0);
        logHeaderRow.Controls.Add(_btnClearLog, 2, 0);

        _txtLog = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            Font = new Font("Consolas", 8.75f),
            BorderStyle = BorderStyle.None
        };
        _cardLog.Controls.Add(_txtLog);
        _cardLog.Controls.Add(logHeaderRow);
        settingsLayout.Controls.Add(_cardLog, 0, 2);

        _settingsPage.Controls.Add(settingsLayout);

        RecordHistoryEvent(HistoryEventType.System, "ScreenShield", $"Started ({_updateService.CurrentVersion}) · Shield active · Taskbar guard ready.");

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
        Controls.Add(_historyPage);
        Controls.Add(_settingsPage);
        Controls.Add(_tabBar);
        Controls.Add(_headerBar);
        _sentinelOverlay.BringToFront();

        _trayIcon = new NotifyIcon
        {
            Icon = _shieldIcon,
            Text = "ScreenShield — Click to open",
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left)
            {
                if (Visible && WindowState != FormWindowState.Minimized && Win32Interop.GetForegroundWindow() == Handle)
                {
                    _helpPopover.HidePopover();
                    Hide();
                }
                else
                {
                    InstantShowFromTray();
                }
            }
        };

        _guardTimer = new System.Windows.Forms.Timer { Interval = 8000 };
        _guardTimer.Tick += (_, _) => TriggerNativeGuardSweepAsync();

        _affinityWatchdog = new System.Windows.Forms.Timer { Interval = 5000 };
        _affinityWatchdog.Tick += (_, _) => EnforceAffinityWatchdog();

        _screenshotWatcher.ScreenshotDetected += OnUpworkScreenshotDetected;
        SystemEvents.UserPreferenceChanged += OnSystemPreferenceChanged;
        ApplyTheme(_palette);
        UpdateHistoryTabCount();
        RenderHistoryTable();
    }

    private void OnUpworkScreenshotDetected(UpworkScreenshotEvent evt)
    {
        if (IsDisposed || !IsHandleCreated) return;
        BeginInvoke(() =>
        {
            string tbState = _taskbarShield.IsTaskbarCurrentlyShielded ? "Taskbar hidden" : "Taskbar normal";
            string shieldState = IsSnoozed
                ? "Shield SNOOZED"
                : (_settings.ExcludeWrapperFromCapture ? "Shield ACTIVE" : "Shield PAUSED");
            string exposure = _lastExposedCount > 0
                ? $"{_lastExposedCount} blocked open ({_lastExposedNames})"
                : "0 blocked exposed";
            string details = $"SS #{evt.SessionCount} ({evt.FileName}) · {shieldState} · {tbState} · {exposure}";

            RecordHistoryEvent(HistoryEventType.Screenshot, "Upwork", details, evt.DetectedAt);
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
        _tabHistory.Selected = tab == ShieldFilterTab.History;
        _tabSettings.Selected = tab == ShieldFilterTab.Settings;

        bool isHistory = tab == ShieldFilterTab.History;
        bool isSettings = tab == ShieldFilterTab.Settings;
        _historyPage.Visible = isHistory;
        _settingsPage.Visible = isSettings;
        _catalogPage.Visible = !isHistory && !isSettings;

        if (isHistory)
        {
            RenderHistoryTable();
        }
        else if (!isSettings)
        {
            RenderFilteredCatalog();
        }
    }

    public void SetHistoryFilter(HistoryFilterCategory category)
    {
        _historyFilter = category;
        if (_segHistoryFilter.SelectedIndex != (int)category)
        {
            _segHistoryFilter.SelectedIndex = (int)category;
        }

        RenderHistoryTable();
    }

    public void SetHistorySearch(string query)
    {
        if (_txtHistorySearch.InputText != query)
        {
            _txtHistorySearch.InputText = query;
        }
        else
        {
            RenderHistoryTable();
        }
    }

    public void SortHistoryBy(HistorySortColumn column, bool ascending)
    {
        _historySortBy = column;
        _historySortAsc = ascending;
        _historyTable.SetSort(column, ascending);
        RenderHistoryTable();
    }

    public string ExportFilteredHistoryCsv() => HistoryStore.ExportCsv(_visibleHistoryEntries);

    public void CopyFilteredHistoryCsv()
    {
        string csv = ExportFilteredHistoryCsv();
        try { Clipboard.SetText(csv); }
        catch { /* Ignore clipboard contention in headless tests */ }
        AppendLog($"Copied {_visibleHistoryEntries.Count} history row(s) as CSV.");
    }

    public void ClearHistory()
    {
        _historyStore.Clear();
        UpdateHistoryTabCount();
        RenderHistoryTable();
        AppendLog("History cleared.");
    }

    private void ExportHistoryCsvWithDialog()
    {
        using var sfd = new SaveFileDialog
        {
            Title = "Export ScreenShield History",
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            FileName = $"screenshield-history-{DateTime.Now:yyyyMMdd-HHmm}.csv"
        };
        if (sfd.ShowDialog(this) == DialogResult.OK)
        {
            File.WriteAllText(sfd.FileName, ExportFilteredHistoryCsv());
            RecordHistoryEvent(HistoryEventType.System, "Export", $"Exported {_visibleHistoryEntries.Count} row(s) to {Path.GetFileName(sfd.FileName)}");
        }
    }

    public void SnoozeProtection(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return;

        if (!IsSnoozed)
        {
            _excludeStateBeforeSnooze = _settings.ExcludeWrapperFromCapture;
        }

        _snoozeUntil = DateTimeOffset.UtcNow.Add(duration);
        _settings.ExcludeWrapperFromCapture = false;
        _suppressSettingsEvents = true;
        _chkExcludeSelf.Checked = false;
        _suppressSettingsEvents = false;

        _taskbarShield.RestoreOriginalState();
        ApplyCaptureExclusionState();
        UpdateSnoozePillText();
        _snoozeTimer.Start();

        int mins = Math.Max(1, (int)Math.Round(duration.TotalMinutes));
        RecordHistoryEvent(HistoryEventType.Snooze, "Protection", $"Snoozed for {mins} min (auto-resumes at {_snoozeUntil.Value.LocalDateTime:HH:mm:ss})");
    }

    public void ResumeFromSnooze(bool expired = false)
    {
        if (!_snoozeUntil.HasValue && _settings.ExcludeWrapperFromCapture) return;

        _snoozeUntil = null;
        _snoozeTimer.Stop();

        _settings.ExcludeWrapperFromCapture = _excludeStateBeforeSnooze || true;
        _suppressSettingsEvents = true;
        _chkExcludeSelf.Checked = _settings.ExcludeWrapperFromCapture;
        _suppressSettingsEvents = false;

        ApplyCaptureExclusionState();
        RecordHistoryEvent(
            HistoryEventType.Snooze,
            "Protection",
            expired ? "Snooze expired — Shield automatically re-armed" : "Snooze cancelled — Shield re-armed");
    }

    private void OnSnoozeTick()
    {
        if (!_snoozeUntil.HasValue)
        {
            _snoozeTimer.Stop();
            return;
        }

        if (DateTimeOffset.UtcNow >= _snoozeUntil.Value)
        {
            ResumeFromSnooze(expired: true);
            return;
        }

        UpdateSnoozePillText();
    }

    private void UpdateSnoozePillText()
    {
        if (!IsSnoozed || !_snoozeUntil.HasValue) return;
        TimeSpan rem = _snoozeUntil.Value - DateTimeOffset.UtcNow;
        if (rem < TimeSpan.Zero) rem = TimeSpan.Zero;
        string countdown = rem.TotalHours >= 1
            ? $"{(int)rem.TotalHours}h {rem.Minutes:D2}m"
            : $"{rem.Minutes:D2}:{rem.Seconds:D2}";
        _btnShieldPill.Text = $"⏸ Snoozed {countdown}";
        _btnShieldPill.Tone = ButtonTone.ShieldOff;
        _trayIcon.Text = $"ScreenShield — Snoozed ({countdown})";
    }

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        UpdateCheckResult result = await _updateService.CheckForUpdateAsync(ct);
        RecordHistoryEvent(HistoryEventType.Update, result.AssetName, result.Message);
        return result;
    }

    private async Task CheckForUpdatesInteractiveAsync()
    {
        _btnCheckUpdate.Enabled = false;
        _btnCheckUpdate.Text = "Checking...";
        try
        {
            UpdateCheckResult res = await CheckForUpdatesAsync();
            if (res.UpdateAvailable && !string.IsNullOrWhiteSpace(res.DownloadUrl))
            {
                DialogResult answer = MessageBox.Show(
                    $"{res.Message}\n\nDownload and restart ScreenShield now?",
                    "ScreenShield Update Available",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Information);

                if (answer == DialogResult.Yes)
                {
                    _btnCheckUpdate.Text = "Updating...";
                    bool staged = await _updateService.DownloadAndApplyUpdateAsync(res.DownloadUrl);
                    if (staged)
                    {
                        RecordHistoryEvent(HistoryEventType.Update, res.AssetName, "Update downloaded — restarting ScreenShield.");
                        _forceExit = true;
                        Close();
                        return;
                    }

                    MessageBox.Show("Download failed or verification did not pass. Please try again.", "ScreenShield Update", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
            }
            else
            {
                MessageBox.Show(res.Message, $"ScreenShield {_updateService.CurrentVersion}", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
        }
        finally
        {
            if (!IsDisposed)
            {
                _btnCheckUpdate.Text = "↑ Check Update";
                _btnCheckUpdate.Enabled = true;
            }
        }
    }

    public void SetSearchQuery(string query)
    {
        if (_txtSearch.InputText != query)
        {
            _txtSearch.InputText = query;
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
        _txtAddSite.ClearInput();
        if (changed)
        {
            RebuildCatalogBlockedFlags();
            ScheduleDebouncedSave();
            RecordHistoryEvent(HistoryEventType.BlockAllow, normalized, "Added custom domain to Blocked list");
        }

        return changed;
    }

    public int BulkUpdateSelected(bool block)
    {
        IReadOnlyList<CatalogEntry> selected = _shieldList.GetSelectedOrCheckedItems();
        if (selected.Count == 0) return 0;

        var targets = new List<(ShieldItemKind, string)>(selected.Count);
        foreach (CatalogEntry entry in selected)
        {
            targets.Add((entry.Kind, entry.Key));
        }

        int changed = ActiveCatalogService.BulkSetBlocked(_settings, targets, block);
        _shieldList.ClearChecks();
        if (changed > 0)
        {
            RebuildCatalogBlockedFlags();
            ScheduleDebouncedSave();
            string summary = string.Join(", ", selected.Take(4).Select(static s => s.DisplayName));
            if (selected.Count > 4) summary += $" (+{selected.Count - 4} more)";
            RecordHistoryEvent(
                HistoryEventType.BlockAllow,
                $"{changed} item(s)",
                block ? $"Bulk Blocked: {summary}" : $"Bulk Allowed: {summary}");
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
            RecordHistoryEvent(
                HistoryEventType.BlockAllow,
                entry.DisplayName,
                nextBlocked ? "Changed: Allowed -> Blocked" : "Changed: Blocked -> Allowed");
        }

        return changed;
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
                continue;
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

    private void UpdateHistoryTabCount()
    {
        _tabHistory.Text = $"⏱ History ({_historyStore.Count})";
    }

    private void RenderFilteredCatalog()
    {
        _visibleEntries = ActiveCatalogService.Filter(_allCatalogEntries, _activeTab, _txtSearch.InputText);
        _shieldList.SetItems(_visibleEntries);
    }

    private void RenderHistoryTable()
    {
        _visibleHistoryEntries = _historyStore.Query(_historyFilter, _txtHistorySearch.InputText, _historySortBy, _historySortAsc);
        _historyTable.SetRows(_visibleHistoryEntries);
    }

    private void RecordHistoryEvent(HistoryEventType type, string target, string details, DateTimeOffset? timestamp = null)
    {
        _historyStore.Record(type, target, details, timestamp);
        AppendLog($"[{HistoryStore.FormatTypeLabel(type)}] {target}: {details}");
        if (!IsDisposed)
        {
            UpdateHistoryTabCount();
            if (_activeTab == ShieldFilterTab.History)
            {
                RenderHistoryTable();
            }
        }
    }

    public void ApplyTheme(ThemePalette palette)
    {
        _palette = palette;
        BackColor = palette.BaseSurface;
        ForeColor = palette.TextPrimary;

        _headerBar.BackColor = palette.BaseSurface;
        _tabBar.BackColor = palette.BaseSurface;
        _lblBrandName.ForeColor = palette.TextPrimary;
        _lblGuardStatus.ForeColor = palette.TextSub;
        _brandLogo.Invalidate();

        _catalogPage.BackColor = palette.BaseSurface;
        _historyPage.BackColor = palette.BaseSurface;
        _settingsPage.BackColor = palette.BaseSurface;
        _lblEmptyHint.ForeColor = palette.TextSub;
        _lblHistoryHint.ForeColor = palette.TextSub;

        _txtSearch.SurfaceOverride = palette.BaseSurface;
        _txtSearch.ApplyTheme(palette);
        _txtAddSite.SurfaceOverride = palette.BaseSurface;
        _txtAddSite.ApplyTheme(palette);
        _shieldList.ApplyTheme(palette);

        _segHistoryFilter.SurfaceOverride = palette.BaseSurface;
        _segHistoryFilter.ApplyTheme(palette);
        _txtHistorySearch.SurfaceOverride = palette.BaseSurface;
        _txtHistorySearch.ApplyTheme(palette);
        _historyTable.ApplyTheme(palette);

        _cardProtection.ApplyTheme(palette);
        _cardBehavior.ApplyTheme(palette);
        _cardLog.ApplyTheme(palette);
        Color cardSurface = _cardProtection.CardFill;

        foreach (TactileToggle toggle in _settingsToggles)
        {
            toggle.SurfaceOverride = cardSurface;
            toggle.ApplyTheme(palette);
        }

        foreach (Label lbl in _settingsSubLabels)
        {
            lbl.BackColor = cardSurface;
            lbl.ForeColor = palette.TextSub;
        }

        foreach (Label hdr in _settingsSectionHeaders)
        {
            hdr.BackColor = cardSurface;
            hdr.ForeColor = palette.TextPrimary;
        }

        foreach (HelpBadge badge in _helpBadges)
        {
            badge.SurfaceOverride = cardSurface;
            badge.ApplyTheme(palette);
        }

        _helpPopover.ApplyTheme(palette);

        _segAction.SurfaceOverride = cardSurface;
        _segAction.ApplyTheme(palette);
        _txtUpworkProcs.SurfaceOverride = cardSurface;
        _txtUpworkProcs.ApplyTheme(palette);
        _stepCooldown.SurfaceOverride = cardSurface;
        _stepCooldown.ApplyTheme(palette);

        _txtLog.BackColor = cardSurface;
        _txtLog.ForeColor = palette.TextPrimary;

        foreach (Control ctrl in InteractiveControls)
        {
            if (ctrl is MinimalButton btn)
            {
                btn.SurfaceOverride = btn == _btnCopyLog || btn == _btnClearLog || btn == _btnCheckUpdate
                    ? cardSurface
                    : palette.BaseSurface;
                btn.ApplyTheme(palette);
            }
        }

        RenderFilteredCatalog();
        RenderHistoryTable();

        if (IsHandleCreated)
        {
            Win32Interop.ApplyWindows11Chrome(Handle, palette.IsDark, palette.BaseSurface, palette.TextPrimary);
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

        var snoozeSub = new ToolStripMenuItem("Snooze Protection");
        snoozeSub.DropDownItems.Add("15 minutes", null, (_, _) => SnoozeProtection(TimeSpan.FromMinutes(15)));
        snoozeSub.DropDownItems.Add("30 minutes", null, (_, _) => SnoozeProtection(TimeSpan.FromMinutes(30)));
        snoozeSub.DropDownItems.Add("1 hour", null, (_, _) => SnoozeProtection(TimeSpan.FromHours(1)));
        snoozeSub.DropDownItems.Add(new ToolStripSeparator());
        snoozeSub.DropDownItems.Add("Resume Now", null, (_, _) => ResumeFromSnooze(expired: false));
        menu.Items.Add(snoozeSub);

        menu.Items.Add("Verify Protection Now (Ctrl+Shift+V)", null, async (_, _) => await RunLiveCaptureVerificationAsync());
        menu.Items.Add($"Check for Updates ({_updateService.CurrentVersion})", null, async (_, _) => await CheckForUpdatesInteractiveAsync());
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

        if (_startMinimizedToTray)
        {
            Hide();
            Opacity = 1;
        }
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
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        if (WindowState != _lastWindowState)
        {
            _lastWindowState = WindowState;
            _helpPopover.HidePopover();
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

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == Keys.Escape && _helpPopover.Visible) { _helpPopover.HidePopover(); return true; }
        if (keyData == (Keys.Control | Keys.D1)) { SelectTab(ShieldFilterTab.All); return true; }
        if (keyData == (Keys.Control | Keys.D2)) { SelectTab(ShieldFilterTab.Allowed); return true; }
        if (keyData == (Keys.Control | Keys.D3)) { SelectTab(ShieldFilterTab.Blocked); return true; }
        if (keyData == (Keys.Control | Keys.D4)) { SelectTab(ShieldFilterTab.History); return true; }
        if (keyData == (Keys.Control | Keys.D5)) { SelectTab(ShieldFilterTab.Settings); return true; }
        if (keyData == (Keys.Control | Keys.F))
        {
            if (_activeTab == ShieldFilterTab.History) _txtHistorySearch.FocusInput();
            else { SelectTab(ShieldFilterTab.All); _txtSearch.FocusInput(); }
            return true;
        }
        if (keyData == Keys.F5) { RefreshCatalogAsync(); TriggerNativeGuardSweepAsync(); return true; }
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
            if (!_shownTrayHint)
            {
                _shownTrayHint = true;
                _trayIcon.ShowBalloonTip(
                    1800,
                    "ScreenShield is running in the tray",
                    "Left-click the tray icon or press Ctrl+Shift+H to open instantly.",
                    ToolTipIcon.Info);
            }

            return;
        }

        UninstallWinEventHooks();
        _saveDebounceCts?.Cancel();
        _saveDebounceCts?.Dispose();
        _screenshotWatcher.ScreenshotDetected -= OnUpworkScreenshotDetected;
        _screenshotWatcher.Dispose();
        SystemEvents.UserPreferenceChanged -= OnSystemPreferenceChanged;
        _snoozeTimer.Stop();
        _snoozeTimer.Dispose();
        _snoozeMenu.Dispose();
        _guardTimer.Stop();
        _guardTimer.Dispose();
        _affinityWatchdog.Stop();
        _affinityWatchdog.Dispose();
        UnregisterGlobalHotkey();
        _taskbarShield.Dispose();
        if (_windowInspector is IDisposable disposableInspector)
        {
            disposableInspector.Dispose();
        }
        if (_ownsHistoryStore)
        {
            _historyStore.Dispose();
        }
        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _helpPopover.Dispose();
        _toolTip.Dispose();
        _shieldIcon.Dispose();
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

        if (IsSnoozed)
        {
            UpdateSnoozePillText();
            return result;
        }

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
        _snoozeUntil = null;
        _snoozeTimer.Stop();
        bool prev = _settings.ExcludeWrapperFromCapture;
        _settings.ExcludeWrapperFromCapture = !prev;
        _suppressSettingsEvents = true;
        _chkExcludeSelf.Checked = _settings.ExcludeWrapperFromCapture;
        _suppressSettingsEvents = false;
        ApplyCaptureExclusionState();
        ScheduleDebouncedSave();
        RecordHistoryEvent(
            HistoryEventType.Setting,
            "Capture Shield",
            _settings.ExcludeWrapperFromCapture ? "Changed: Paused -> Protected" : "Changed: Protected -> Paused");
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
        if (!_settings.ExcludeWrapperFromCapture || IsSnoozed || !IsHandleCreated) return;
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

            RecordHistoryEvent(HistoryEventType.Verify, "Capture Check", capture.Summary);
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
        if (_isDraggingOrResizing || IsSnoozed || Interlocked.Exchange(ref _guardInFlight, 1) == 1) return;

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
            RecordHistoryEvent(
                HistoryEventType.Guard,
                "Upwork",
                eval.UpworkRunning ? "Upwork Time Tracker detected running" : "Upwork Time Tracker stopped");
        }

        if (_taskbarShield.IsTaskbarCurrentlyShielded != _lastTaskbarShielded)
        {
            _lastTaskbarShielded = _taskbarShield.IsTaskbarCurrentlyShielded;
            RecordHistoryEvent(
                HistoryEventType.Taskbar,
                "Windows Taskbar",
                _lastTaskbarShielded ? "Auto-hidden (blocked app/site open)" : "Restored to normal");
        }

        _lastUpworkRunning = eval.UpworkRunning;
        _lastExposedCount = eval.ExposedNativeWindows.Count;
        _lastExposedNames = _lastExposedCount == 0
            ? string.Empty
            : string.Join(", ", eval.ExposedNativeWindows.Select(static w => w.ProcessName).Distinct(StringComparer.OrdinalIgnoreCase).Take(3));
        RefreshStatusLine();

        foreach (GuardActionItem item in eval.Actions)
        {
            RecordHistoryEvent(HistoryEventType.Guard, item.Window.ProcessName, item.Reason);
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
                    RecordHistoryEvent(HistoryEventType.Guard, "Upwork", pause.Message);
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

        bool prevExclude = _settings.ExcludeWrapperFromCapture;
        bool prevTaskbar = _settings.AlwaysHideTaskbarWhenBlockedOpen;
        bool prevGuard = _settings.NativeGuardEnabled;
        bool prevOnlyUpwork = _settings.OnlyActWhenUpworkRunning;
        NativeGuardAction prevAction = _settings.NativeGuardAction;
        int prevCooldown = _settings.GuardCooldownSeconds;
        bool prevTray = _settings.MinimizeToTrayOnClose;
        bool prevHotkey = _settings.EnableGlobalHotkey;
        bool prevStartup = _settings.RunAtWindowsStartup;

        _settings.ExcludeWrapperFromCapture = _chkExcludeSelf.Checked;
        _settings.AlwaysHideTaskbarWhenBlockedOpen = _chkTaskbarHide.Checked;
        _settings.NativeGuardEnabled = _chkGuardEnabled.Checked;
        _settings.OnlyActWhenUpworkRunning = _chkOnlyWhenUpwork.Checked;
        _settings.NativeGuardAction = (NativeGuardAction)Math.Clamp(_segAction.SelectedIndex, 0, 3);
        _settings.UpworkProcessNames = _txtUpworkProcs.InputText
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        _settings.GuardCooldownSeconds = _stepCooldown.Value;
        _settings.MinimizeToTrayOnClose = _chkMinimizeToTray.Checked;
        _settings.EnableGlobalHotkey = _chkGlobalHotkey.Checked;
        _settings.RunAtWindowsStartup = _chkRunAtStartup.Checked;
        _settings.Normalize();

        if (prevExclude != _settings.ExcludeWrapperFromCapture)
            RecordHistoryEvent(HistoryEventType.Setting, "Hide ScreenShield", $"{OnOff(prevExclude)} -> {OnOff(_settings.ExcludeWrapperFromCapture)}");
        if (prevTaskbar != _settings.AlwaysHideTaskbarWhenBlockedOpen)
            RecordHistoryEvent(HistoryEventType.Setting, "Auto-hide Taskbar", $"{OnOff(prevTaskbar)} -> {OnOff(_settings.AlwaysHideTaskbarWhenBlockedOpen)}");
        if (prevGuard != _settings.NativeGuardEnabled)
            RecordHistoryEvent(HistoryEventType.Setting, "Watch Blocked", $"{OnOff(prevGuard)} -> {OnOff(_settings.NativeGuardEnabled)}");
        if (prevOnlyUpwork != _settings.OnlyActWhenUpworkRunning)
            RecordHistoryEvent(HistoryEventType.Setting, "Only When Upwork", $"{OnOff(prevOnlyUpwork)} -> {OnOff(_settings.OnlyActWhenUpworkRunning)}");
        if (prevAction != _settings.NativeGuardAction)
            RecordHistoryEvent(HistoryEventType.Setting, "Guard Action", $"{prevAction} -> {_settings.NativeGuardAction}");
        if (prevCooldown != _settings.GuardCooldownSeconds)
            RecordHistoryEvent(HistoryEventType.Setting, "Cooldown", $"{prevCooldown}s -> {_settings.GuardCooldownSeconds}s");
        if (prevTray != _settings.MinimizeToTrayOnClose)
            RecordHistoryEvent(HistoryEventType.Setting, "Minimize to Tray", $"{OnOff(prevTray)} -> {OnOff(_settings.MinimizeToTrayOnClose)}");
        if (prevHotkey != _settings.EnableGlobalHotkey)
            RecordHistoryEvent(HistoryEventType.Setting, "Global Hotkey", $"{OnOff(prevHotkey)} -> {OnOff(_settings.EnableGlobalHotkey)}");

        if (prevStartup != _settings.RunAtWindowsStartup)
        {
            bool ok = _startupService.SetEnabled(_settings.RunAtWindowsStartup);
            RecordHistoryEvent(
                HistoryEventType.Setting,
                "Windows Startup",
                _settings.RunAtWindowsStartup
                    ? (ok ? "Enabled (quiet tray launch)" : "Failed to register startup entry")
                    : "Disabled");
        }

        if (!_settings.AlwaysHideTaskbarWhenBlockedOpen)
        {
            _taskbarShield.RestoreOriginalState();
        }

        _guardEngine.ResetCooldowns();
        ApplyCaptureExclusionState();
        SyncGlobalHotkey();
        ScheduleDebouncedSave();
    }

    private static string OnOff(bool v) => v ? "On" : "Off";

    public void InstantShowFromTray()
    {
        if (!Visible)
        {
            Show();
        }

        if (IsHandleCreated)
        {
            if (Win32Interop.IsIconic(Handle))
            {
                _ = Win32Interop.ShowWindow(Handle, Win32Interop.SW_RESTORE);
            }

            _ = Win32Interop.SetForegroundWindow(Handle);
        }
    }

    public void ToggleWindowVisibility()
    {
        if (Visible && WindowState != FormWindowState.Minimized)
        {
            _helpPopover.HidePopover();
            Hide();
        }
        else
        {
            InstantShowFromTray();
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
            Margin = new Padding(0, 0, 0, 3)
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
            Margin = new Padding(0, 1, 8, 1)
        };
        var badge = new HelpBadge(
            helpText,
            (b, text) => _helpPopover.ShowFor(b, text, this),
            () => _helpPopover.HidePopover())
        {
            Margin = new Padding(2, 1, 4, 0)
        };
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

    private TactileToggle CreateSettingsToggle(string text, bool isChecked)
    {
        var toggle = new TactileToggle(text, isChecked);
        toggle.CheckedChanged += (_, _) => OnInlineSettingsChanged();
        _settingsToggles.Add(toggle);
        return toggle;
    }

    private Label CreateSettingsSubLabel(string text)
    {
        var lbl = new Label
        {
            Text = text,
            AutoSize = true,
            UseMnemonic = false,
            Font = DesignTokens.CreateUiFont(8.5f, semibold: true),
            Margin = new Padding(0, 4, 0, 2)
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
}
