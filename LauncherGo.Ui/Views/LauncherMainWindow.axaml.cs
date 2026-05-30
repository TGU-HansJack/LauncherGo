using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LauncherGo.Abstractions.Services;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;
using LauncherGo.Ui.Platform;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow : Window
{
    private const int RealtimeRangeSeconds = 60;
    private const int NetworkRangeCount = 144;
    private const double ChartWidth = 640;
    private const double ChartHeight = 248;
    private const double ThumbnailWidth = 76;
    private const double ThumbnailHeight = 50;
    private const double OsqEndpointColumnWidth = 420;
    private const double OsqEndpointColumnSpacing = 10;
    private const string DefaultServerDownloadCatalogUrl = "https://api.vintagestory.at/stable-unstable.json";
    private const string GitHubContributorsApiUrl = "https://api.github.com/repos/TGU-HansJack/LauncherGo/contributors?per_page=100";
    private const string SponsorApiUrl = "https://vscn.studio/api/afdian/sponsors";
    private const string LaunchStartIconData =
        "M187.2 100.9C174.8 94.1 159.8 94.4 147.6 101.6C135.4 108.8 128 121.9 128 136L128 504C128 518.1 135.5 531.2 147.6 538.4C159.7 545.6 174.8 545.9 187.2 539.1L523.2 355.1C536 348.1 544 334.6 544 320C544 305.4 536 291.9 523.2 284.9L187.2 100.9z";
    private const string LaunchStopIconData =
        "M160 96L480 96C515.3 96 544 124.7 544 160L544 480C544 515.3 515.3 544 480 544L160 544C124.7 544 96 515.3 96 480L96 160C96 124.7 124.7 96 160 96z";
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    private static readonly (string Zh, string En)[] HomeSlogans =
    [
        ("Launcher Go !", "Launcher Go !"),
        ("极速启动服务，高自定义功能", "Fast startup, highly customizable"),
        ("24*7小时测试环境，追求0漏洞", "24*7 tested, aiming for zero defects"),
        ("极致开服体验，从Launcher Go开始", "Start the best server experience with Launcher Go")
    ];

    private static readonly (string Code, string Zh, string En)[] AppearanceLanguageOptions =
    [
        ("zh-CN", "中文", "Chinese"),
        ("en-US", "英文", "English")
    ];

    private static readonly (ThemeMode Mode, string Zh, string En)[] AppearanceThemeOptions =
    [
        (ThemeMode.Light, "亮色主题", "Light Theme"),
        (ThemeMode.Dark, "暗色主题", "Dark Theme"),
        (ThemeMode.System, "跟随系统", "Follow System")
    ];

    private static readonly string[] ConfigServerLanguageOptions =
    [
        "en", "ar", "be", "cs", "da", "de", "es-es", "fr", "hu", "is", "it", "ja", "ko",
        "nl", "no", "pl", "pt-br", "pt-pt", "ru", "sr", "zh-cn", "zh-tw"
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigPlayStyleDefinitions =
    [
        ("surviveandbuild", "标准", "Standard"),
        ("exploration", "探索", "Exploration"),
        ("wildernesssurvival", "荒野求生", "Wilderness Survival"),
        ("homosapiens", "智人", "Homo sapiens"),
        ("creativebuilding", "超平坦创造模式", "Creative Building")
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigWorldTypeDefinitions =
    [
        ("standard", "标准地形", "Standard"),
        ("superflat", "超平坦", "Superflat")
    ];

    private static readonly (int Value, string Zh, string En)[] ConfigWhitelistModeDefinitions =
    [
        (0, "默认（专用服务器启用白名单）", "Default (on for dedicated servers)"),
        (1, "关闭", "Off"),
        (2, "开启", "On")
    ];

    private static readonly (string Value, string Zh, string En)[] ConfigRoleDefinitions =
    [
        ("suplayer", "生存玩家", "Survival Player"),
        ("sumod", "生存管理员", "Survival Moderator"),
        ("suadmin", "生存服主", "Survival Admin"),
        ("crplayer", "创造玩家", "Creative Player"),
        ("crmod", "创造管理员", "Creative Moderator"),
        ("cradmin", "创造服主", "Creative Admin")
    ];

    private static readonly HashSet<string> ConfigOnlyDuringWorldCreateRuleKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "startingClimate",
        "graceTimer",
        "worldClimate",
        "landcover",
        "oceanscale",
        "upheavelCommonness",
        "geologicActivity",
        "landformScale",
        "worldWidth",
        "worldLength",
        "polarEquatorDistance",
        "storyStructuresDistScaling",
        "globalTemperature",
        "globalPrecipitation",
        "globalForestation"
    };

    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IServerPackageService _serverPackageService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceSaveService _saveService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IServerProcessService _serverProcessService;
    private readonly IOpenServerQueryService _openServerQueryService;
    private readonly IRobotService _robotService;
    private readonly ILogTailService _logTailService;
    private readonly IAutomationService _automationService;
    private readonly IAutomationSettingsService _automationSettingsService;
    private readonly IFrpService _frpService;
    private readonly IThirdPartyFrpcService _thirdPartyFrpcService;
    private readonly IInstanceModService _instanceModService;
    private readonly IServerAuthService _serverAuthService;
    private readonly DispatcherTimer _dataTimer;
    private readonly DispatcherTimer _tickerTimer;
    private readonly DispatcherTimer _homeSloganTimer;

    private readonly List<double> _serverCpuSamples = [];
    private readonly List<double> _serverMemoryMbSamples = [];
    private readonly List<double> _robotCpuSamples = [];
    private readonly List<double> _robotMemoryMbSamples = [];
    private readonly List<double> _playersSamples = [];
    private readonly List<double> _networkLatencySamples = [];
    private readonly List<string> _playerEvents = [];

    private readonly List<string> _consoleLines = [];
    private readonly ObservableCollection<ProfileListItem> _profileItems = [];
    private readonly ObservableCollection<SaveListItem> _saveItems = [];
    private readonly ObservableCollection<DownloadVersionListItem> _downloadVersionItems = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configWhitelistModeOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configDefaultRoleOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configPlayStyleOptions = [];
    private readonly ObservableCollection<ConfigChoiceOption> _configWorldTypeOptions = [];
    private readonly ObservableCollection<ConfigSaveFileItem> _configSaveItems = [];
    private readonly ObservableCollection<ConfigWorldRuleItem> _configWorldRuleItems = [];
    private readonly ObservableCollection<ConfigChoiceOption> _thirdPartyFrpcModeOptions = [];
    private readonly ObservableCollection<SettingsContributorItem> _settingsContributorItems = [];
    private readonly ObservableCollection<SettingsSponsorItem> _settingsSponsorItems = [];
    private readonly ObservableCollection<InstanceProfile> _automationProfileItems = [];
    private readonly ObservableCollection<AutomationActionWindowItem> _automationActionWindowItems = [];
    private readonly ObservableCollection<AutomationTimeItem> _automationBackupTimeItems = [];
    private readonly ObservableCollection<ScheduledBroadcastItem> _automationBroadcastItems = [];
    private readonly ObservableCollection<AutomationTimeItem> _automationExportTimeItems = [];
    private readonly ObservableCollection<string> _automationRuntimeLogItems = [];
    private readonly ObservableCollection<InstanceProfile> _modProfileItems = [];
    private readonly ObservableCollection<ModListItem> _modItems = [];
    private readonly ObservableCollection<InstanceProfile> _authProfileItems = [];
    private readonly ObservableCollection<AuthPlayerListItem> _authPlayerItems = [];
    private readonly List<ServerDownloadEntry> _catalogEntries = [];
    private readonly List<OsqEndpointEditorRow> _osqEndpointEditors = [];

    private MainTab _selectedTab = MainTab.Home;
    private HomeMetric _selectedMetric = HomeMetric.Server;
    private InstanceManageTab _selectedInstanceManageTab = InstanceManageTab.Profiles;
    private SettingsTab _selectedSettingsTab = SettingsTab.Server;
    private ConnectionTab _selectedConnectionTab = ConnectionTab.Frp;
    private int _tickerIndex;
    private int _homeSloganIndex;
    private bool _tickerAnimating;
    private bool _homeSloganVisible = true;
    private bool _isChinese;
    private bool _isApplyingAppearanceSettings;
    private bool _downloadCatalogLoaded;
    private bool _isStoppingOrStarting;
    private bool _isRefreshingSaves;
    private bool _isRefreshingConfigProfiles;
    private bool _isLoadingConfig;
    private bool _isApplyingServerSettings;
    private bool _isApplyingNetworkSettings;
    private bool _isApplyingConnectionSettings;
    private bool _aboutMarkdownLoaded;
    private bool _contributorsLoaded;
    private bool _sponsorsLoaded;
    private bool _isFrpRunning;
    private bool _isThirdPartyFrpcRunning;
    private bool _isExitRequested;
    private bool _isRefreshingAutomation;
    private bool _isRefreshingMods;
    private bool _isRefreshingAuth;
    private string _tailedProfileId = string.Empty;
    private TimeSpan _robotLastProcessorTime;
    private DateTimeOffset _robotLastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _robotLastCpuPercent;
    private string _configSaveFileLocation = string.Empty;
    public LauncherMainWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>(),
            ServiceLocator.GetRequiredService<IInstanceProfileService>(),
            ServiceLocator.GetRequiredService<IInstanceSaveService>(),
            ServiceLocator.GetRequiredService<IInstanceServerConfigService>(),
            ServiceLocator.GetRequiredService<IServerProcessService>(),
            ServiceLocator.GetRequiredService<IOpenServerQueryService>(),
            ServiceLocator.GetRequiredService<IRobotService>(),
            ServiceLocator.GetRequiredService<ILogTailService>(),
            ServiceLocator.GetRequiredService<IAutomationService>(),
            ServiceLocator.GetRequiredService<IAutomationSettingsService>(),
            ServiceLocator.GetRequiredService<IFrpService>(),
            ServiceLocator.GetRequiredService<IThirdPartyFrpcService>(),
            ServiceLocator.GetRequiredService<IInstanceModService>(),
            ServiceLocator.GetRequiredService<IServerAuthService>())
    {
    }

    public LauncherMainWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService,
        IInstanceProfileService profileService,
        IInstanceSaveService saveService,
        IInstanceServerConfigService instanceServerConfigService,
        IServerProcessService serverProcessService,
        IOpenServerQueryService openServerQueryService,
        IRobotService robotService,
        ILogTailService logTailService,
        IAutomationService automationService,
        IAutomationSettingsService automationSettingsService,
        IFrpService frpService,
        IThirdPartyFrpcService thirdPartyFrpcService,
        IInstanceModService instanceModService,
        IServerAuthService serverAuthService)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;
        _profileService = profileService;
        _saveService = saveService;
        _instanceServerConfigService = instanceServerConfigService;
        _serverProcessService = serverProcessService;
        _openServerQueryService = openServerQueryService;
        _robotService = robotService;
        _logTailService = logTailService;
        _automationService = automationService;
        _automationSettingsService = automationSettingsService;
        _frpService = frpService;
        _thirdPartyFrpcService = thirdPartyFrpcService;
        _instanceModService = instanceModService;
        _serverAuthService = serverAuthService;

        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataTimer.Tick += OnDataTimerTick;

        _tickerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.3) };
        _tickerTimer.Tick += OnTickerTimerTick;

        _homeSloganTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _homeSloganTimer.Tick += OnHomeSloganTimerTick;

        _serverProcessService.OutputReceived += OnServerOutputReceived;
        _serverProcessService.StatusChanged += OnServerStatusChanged;
        _logTailService.LogLineReceived += OnLogTailLineReceived;
        _automationService.RuntimeLogReceived += OnAutomationRuntimeLogReceived;
        _frpService.StatusChanged += OnFrpStatusChanged;
        _thirdPartyFrpcService.StatusChanged += OnThirdPartyFrpcStatusChanged;
        _openServerQueryService.OutputReceived += OnOpenServerQueryOutputReceived;

        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
        InitializeSeries();
        InitializeCollections();
        RegisterAutoSaveHandlers();
        RefreshProfiles();
        _ = RefreshSavesAsync();
        _ = RefreshDownloadVersionsAsync(forceReload: false);

        SelectTab(MainTab.Home);
        SelectMetric(HomeMetric.Server);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
        SelectSettingsTab(SettingsTab.Server);
        SelectConnectionTab(ConnectionTab.Frp);

        _dataTimer.Start();
        _tickerTimer.Start();
        _homeSloganTimer.Start();

        Opened += OnWindowOpened;
        Closing += OnWindowClosing;

        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _tickerTimer.Stop();
            _homeSloganTimer.Stop();
            _serverProcessService.OutputReceived -= OnServerOutputReceived;
            _serverProcessService.StatusChanged -= OnServerStatusChanged;
            _logTailService.LogLineReceived -= OnLogTailLineReceived;
            _automationService.RuntimeLogReceived -= OnAutomationRuntimeLogReceived;
            _frpService.StatusChanged -= OnFrpStatusChanged;
            _thirdPartyFrpcService.StatusChanged -= OnThirdPartyFrpcStatusChanged;
            _openServerQueryService.OutputReceived -= OnOpenServerQueryOutputReceived;
            _ = _logTailService.StopAsync();
            _ = _openServerQueryService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _robotService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _frpService.StopAsync(TimeSpan.FromSeconds(2));
            _ = _thirdPartyFrpcService.StopAsync(TimeSpan.FromSeconds(2));
        };
    }

    private async void OnWindowOpened(object? sender, EventArgs e)
    {
        Opened -= OnWindowOpened;
        WindowsDwmWindowEffects.Apply(this);

        var preferences = _preferencesService.Load();
        if (preferences.StartHiddenOnLaunch)
        {
            ShowInTaskbar = false;
            Hide();
        }

        await StartConfiguredConnectionServicesAsync(preferences);
    }

    public void RequestExit()
    {
        _isExitRequested = true;
        Close();
    }

    private void OnWindowClosing(object? sender, WindowClosingEventArgs e)
    {
        var preferences = _preferencesService.Load();
        if (!_isExitRequested &&
            e.CloseReason is not WindowCloseReason.ApplicationShutdown and not WindowCloseReason.OSShutdown &&
            preferences.CloseToTrayOnExit)
        {
            e.Cancel = true;
            ShowInTaskbar = false;
            Hide();
        }
    }

    private async Task StartConfiguredConnectionServicesAsync(LauncherPreferences preferences)
    {
        if (preferences.AutoStartOpenServerQueryOnLaunch && preferences.OpenServerQuery.Enabled)
        {
            try
            {
                await _openServerQueryService.StartAsync(ToOpenServerQueryRuntimeSettings(preferences.OpenServerQuery));
            }
            catch (Exception ex)
            {
                SetConnectionStatus(T($"开放信息自启动失败：{ex.Message}", $"Open Info auto-start failed: {ex.Message}"));
            }
        }

        if (preferences.AutoStartRobotOnLaunch)
        {
            try
            {
                await _robotService.StartAsync(ToRobotSettings(preferences.Robot, preferences.OpenServerQuery));
            }
            catch (Exception ex)
            {
                SetConnectionStatus(T($"QQ机器人自启动失败：{ex.Message}", $"QQ robot auto-start failed: {ex.Message}"));
            }
        }

        if (preferences.AutoStartFrpOnLaunch)
        {
            await StartConnectionProcessAsync(ConnectionProcessKind.Frp);
        }

        if (preferences.AutoStartThirdPartyFrpcOnLaunch)
        {
            await StartConnectionProcessAsync(ConnectionProcessKind.ThirdPartyFrpc);
        }

        RefreshConnectionRuntimeStatus();
    }

    private void InitializeStaticTexts()
    {
        HomeNavButton.Content = T("主页", "Home");
        MonitorNavButton.Content = T("监控", "Monitor");
        ConsoleNavButton.Content = T("控制台", "Console");
        InstanceManageNavButton.Content = T("实例", "Instance");
        ConnectionNavButton.Content = T("连接", "Connection");
        SettingsNavButton.Content = T("设置", "Settings");
        HomeSloganTextBlock.Text = T(HomeSlogans[0].Zh, HomeSlogans[0].En);

        LaunchActionTextBlock.Text = T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(LaunchStartIconData);
        CommandTextBox.PlaceholderText = T("输入服务器命令，回车发送", "Enter server command, press Enter to send");
        QuickCommandComboBox.PlaceholderText = T("快捷命令", "Quick command");
        SendCommandButton.Content = T("发送", "Send");

        ServerStatusCardTitleText.Text = T("服务器状态", "Server Status");
        RobotStatusCardTitleText.Text = T("机器人状态", "Robot Status");
        OnlinePlayersCardTitleText.Text = T("在线玩家", "Online Players");
        NetworkStatusCardTitleText.Text = T("网络状态", "Network Status");

        ProfilesTabButton.Content = T("档案列表", "Profiles");
        ConfigTabButton.Content = T("配置", "Config");
        SavesTabButton.Content = T("存档管理", "Saves");
        AutomationTabButton.Content = T("自动化", "Automation");
        ModsTabButton.Content = T("模组管理", "Mods");
        DownloadVersionsTabButton.Content = T("下载版本", "Downloads");
        ProfileNameTextBox.PlaceholderText = T("档案名称", "Profile name");
        CreateProfileButton.Content = T("创建", "Create");
        ImportProfileButton.Content = T("导入", "Import");
        DeleteProfileButton.Content = T("删除", "Delete");
        RefreshProfilesButton.Content = T("刷新", "Refresh");
        NewSaveNameTextBox.PlaceholderText = T("新存档名称", "New save name");
        CreateSaveButton.Content = T("创建存档", "Create Save");
        ImportSaveButton.Content = T("导入", "Import");
        DeleteSaveButton.Content = T("删除", "Delete");
        RefreshSavesButton.Content = T("刷新", "Refresh");
        InitializeAutomationStaticTexts();
        InitializeModStaticTexts();
        DownloadVersionSearchTextBox.PlaceholderText = T("搜索版本号", "Search version");
        ImportServerPackageButton.Content = T("导入", "Import");
        RefreshDownloadVersionsButton.Content = T("刷新", "Refresh");
        InitializeConfigStaticTexts();

        ServerSettingsTabButton.Content = T("服务器设置", "Server");
        AppearanceSettingsTabButton.Content = T("外观", "Appearance");
        NetworkSettingsTabButton.Content = T("网络", "Network");
        AdvancedSettingsTabButton.Content = T("高级", "Advanced");
        AboutSettingsTabButton.Content = T("关于", "About");
        SponsorsSettingsTabButton.Content = T("赞助者", "Sponsors");
        ContributorsSettingsTabButton.Content = T("贡献者", "Contributors");
        SettingsLanguageLabelTextBlock.Text = T("语言", "Language");
        SettingsThemeLabelTextBlock.Text = T("主题", "Theme");
        InitializeServerSettingsStaticTexts();
        InitializeNetworkSettingsStaticTexts();
        InitializeAdvancedSettingsStaticTexts();
        InitializeAboutSettingsStaticTexts();
        InitializeSponsorSettingsStaticTexts();
        InitializeContributorSettingsStaticTexts();
        InitializeConnectionStaticTexts();

        Title = T("LauncherGo 主窗口", "LauncherGo Main Window");
        ToolTip.SetTip(RepositoryButton, T("仓库", "Repository"));
        ToolTip.SetTip(FeedbackButton, T("反馈", "Feedback"));
        ToolTip.SetTip(SponsorButton, T("赞助", "Sponsor"));
    }

    private void InitializeAutomationStaticTexts()
    {
        AutomationSaveButton.Content = T("保存", "Save");
        AutomationRefreshButton.Content = T("刷新", "Refresh");
        AutomationRestartEnabledLabelTextBlock.Text = T("启用定时开关服", "Enable scheduled start/stop");
        AutomationBackupEnabledLabelTextBlock.Text = T("启用定时备份", "Enable scheduled backup");
        AutomationBackupBeforeShutdownLabelTextBlock.Text = T("关服前备份", "Backup before shutdown");
        AutomationBroadcastEnabledLabelTextBlock.Text = T("启用定时广播", "Enable scheduled broadcast");
        AutomationExportEnabledLabelTextBlock.Text = T("启用日志导出", "Enable log export");
        AutomationExportBeforeShutdownLabelTextBlock.Text = T("关服前导出日志", "Export before shutdown");
        AutomationExportIncludeChatLabelTextBlock.Text = T("导出聊天", "Export chat");
        AutomationExportIncludeServerLabelTextBlock.Text = T("导出服务端信息", "Export server info");
        AutomationActionTitleTextBlock.Text = T("定时开关服", "Scheduled Start/Stop");
        AutomationAddActionButton.Content = T("添加", "Add");
        AutomationAddBackupTimeButton.Content = T("添加", "Add");
        AutomationAddExportTimeButton.Content = T("添加", "Add");
        AutomationAddBroadcastButton.Content = T("添加", "Add");
    }

    private void InitializeModStaticTexts()
    {
        ModZipPathTextBox.PlaceholderText = T("Mod ZIP 路径", "Mod ZIP path");
        BrowseModZipButton.Content = T("浏览", "Browse");
        ImportModZipButton.Content = T("导入", "Import");
        DeleteSelectedModsButton.Content = T("删除", "Delete");
        RefreshModsButton.Content = T("刷新", "Refresh");
    }

    private void InitializeConfigStaticTexts()
    {
        ConfigRefreshButton.Content = T("刷新", "Refresh");
        ConfigImportButton.Content = T("导入", "Import");
        ConfigSaveButton.Content = T("保存", "Save");
        ConfigBasicInfoTitleTextBlock.Text = T("基础信息", "Basic Info");
        ConfigServerNameLabelTextBlock.Text = T("服务器名称", "Server Name");
        ConfigServerLanguageLabelTextBlock.Text = T("服务器语言", "Server Language");
        ConfigDefaultRoleCodeLabelTextBlock.Text = T("默认角色代码", "Default Role Code");
        ConfigServerDescriptionLabelTextBlock.Text = T("服务器描述", "Server Description");
        ConfigWelcomeMessageLabelTextBlock.Text = T("进服提示", "Welcome Message");
        ConfigNetworkTitleTextBlock.Text = T("网络与公开", "Network & Listing");
        ConfigIpLabelTextBlock.Text = T("IP", "IP");
        ConfigPortLabelTextBlock.Text = T("端口", "Port");
        ConfigMaxClientsLabelTextBlock.Text = T("最大玩家数", "Max Players");
        ConfigMaxClientsInQueueLabelTextBlock.Text = T("排队人数上限", "Queue Limit");
        ConfigServerUrlLabelTextBlock.Text = T("服务器网址", "Server URL");
        ConfigAdvertiseServerToggleLabelTextBlock.Text = T("公开到服务器列表", "List on Public Server Browser");
        ConfigUpnpToggleLabelTextBlock.Text = T("启用 UPnP 自动端口映射", "Enable UPnP Port Mapping");
        ConfigSecurityTitleTextBlock.Text = T("安全与维护", "Security & Maintenance");
        ConfigPasswordLabelTextBlock.Text = T("加入密码", "Join Password");
        ConfigPasswordHintTextBlock.Text = T("留空表示不设置密码。", "Leave empty to disable password.");
        ConfigWhitelistModeLabelTextBlock.Text = T("白名单模式", "Whitelist Mode");
        ConfigWarnAfkSecondsLabelTextBlock.Text = T("AFK 警告秒数", "AFK Warning Seconds");
        ConfigKickAfkSecondsLabelTextBlock.Text = T("AFK 踢出秒数", "AFK Kick Seconds");
        ConfigClientConnectionTimeoutLabelTextBlock.Text = T("连接超时秒数", "Connection Timeout Seconds");
        ConfigMaxChunkRadiusLabelTextBlock.Text = T("最大区块视距半径", "Max Chunk View Radius");
        ConfigDieBelowDiskSpaceMbLabelTextBlock.Text = T("低于磁盘空间时关闭（MB）", "Shutdown Below Disk Space (MB)");
        ConfigVerifyPlayerAuthToggleLabelTextBlock.Text = T("启用官方账号验证", "Enable Official Auth");
        ConfigAllowPvPToggleLabelTextBlock.Text = T("允许PvP", "Allow PvP");
        ConfigAllowFireSpreadToggleLabelTextBlock.Text = T("允许火势蔓延", "Allow Fire Spread");
        ConfigAllowFallingBlocksToggleLabelTextBlock.Text = T("允许方块掉落", "Allow Falling Blocks");
        ConfigPassTimeWhenEmptyToggleLabelTextBlock.Text = T("无人在线时继续流逝时间", "Pass Time When Empty");
        ConfigCorruptionProtectionToggleLabelTextBlock.Text = T("启用存档损坏保护", "Enable Corruption Protection");
        ConfigRegenerateCorruptChunksToggleLabelTextBlock.Text = T("重新生成损坏区块", "Regenerate Corrupt Chunks");
        ConfigStartupCommandsLabelTextBlock.Text = T("启动后执行命令", "Startup Commands");
        ConfigWorldTitleTextBlock.Text = T("世界", "World");
        ConfigSeedLabelTextBlock.Text = T("种子", "Seed");
        ConfigWorldNameLabelTextBlock.Text = T("世界名称", "World Name");
        ConfigSaveFileLabelTextBlock.Text = T("存档文件", "Save File");
        ConfigPlayStyleLabelTextBlock.Text = T("游玩风格", "Play Style");
        ConfigWorldTypeLabelTextBlock.Text = T("世界类型", "World Type");
        ConfigWorldHeightLabelTextBlock.Text = T("世界高度", "World Height");
        ConfigWorldGeneratedNoticeTextBlock.Text = T(
            "当前存档已生成世界：种子、游玩风格、世界类型、世界高度，以及仅限建档阶段的世界规则（如世界宽度/长度）已锁定。",
            "This save already has a generated world: seed, play style, world type, world height, and world-creation-only rules are locked.");
        ConfigWorldRulesTitleTextBlock.Text = T("世界规则", "World Rules");
        ConfigNoProfileTextBlock.Text = T("暂无档案，请先创建档案。", "No profile found. Create a profile first.");
        RebuildConfigChoiceOptions();
        RefreshConfigWorldRuleLabels();
    }

    private void InitializeServerSettingsStaticTexts()
    {
        SettingsServerDirectoryTitleTextBlock.Text = T("目录路径", "Directory Path");
        SettingsWorkspaceDirectoryLabelTextBlock.Text = T("工作目录", "Workspace");
        SettingsBrowseWorkspaceDirectoryButton.Content = T("浏览", "Browse");
        SettingsQuickCommandsTitleTextBlock.Text = T("快捷命令", "Quick Commands");
        SettingsServerAutomationTitleTextBlock.Text = T("启动与托盘", "Startup & Tray");
        SettingsStartWithWindowsLabelTextBlock.Text = T("开机启动启动器", "Start launcher with Windows");
        SettingsCloseToTrayLabelTextBlock.Text = T("关闭时隐藏到托盘，不直接退出", "Hide to tray on close instead of exiting");
        SettingsStartHiddenLabelTextBlock.Text = T("启动时隐藏到托盘", "Start hidden to tray");
        SettingsAutoStartOsqLabelTextBlock.Text = T("启动时自动启动开放信息", "Auto-start Open Info on launch");
        SettingsAutoStartRobotLabelTextBlock.Text = T("启动时自动启动QQ机器人", "Auto-start QQ robot on launch");
        SettingsAutoStartFrpLabelTextBlock.Text = T("启动时自动开启内网穿透（常规）", "Auto-start FRP (regular) on launch");
        SettingsAutoStartThirdPartyFrpcLabelTextBlock.Text = T("启动时自动开启第三方内网穿透", "Auto-start third-party FRPC on launch");
    }

    private void InitializeNetworkSettingsStaticTexts()
    {
        SettingsNetworkDownloadTitleTextBlock.Text = T("下载网络", "Download Network");
        SettingsThirdPartyServerLabelTextBlock.Text = T("第三方服务端", "Third-party Server");
        SettingsDownloadChunkCountLabelTextBlock.Text = T("分片数量", "Chunk Count");
        SettingsChunkedDownloadLabelTextBlock.Text = T("大文件分片下载", "Chunked large-file downloads");
    }

    private void InitializeAdvancedSettingsStaticTexts()
    {
        SettingsAdvancedActionsTitleTextBlock.Text = T("维护", "Maintenance");
        SettingsOpenLogButton.Content = T("打开软件日志", "Open App Logs");
        SettingsResetAllButton.Content = T("重置所有设置", "Reset All Settings");
        SettingsClearDownloadCacheButton.Content = T("清空下载缓存", "Clear Download Cache");
    }

    private void InitializeAboutSettingsStaticTexts()
    {
        SetAboutFallbackText(T("正在加载 README.md ...", "Loading README_en.md ..."));
    }

    private void InitializeSponsorSettingsStaticTexts()
    {
    }

    private void InitializeContributorSettingsStaticTexts()
    {
    }

    private void InitializeConnectionStaticTexts()
    {
        ConnectionFrpTabButton.Content = T("内网穿透", "FRP");
        ConnectionOpenInfoTabButton.Content = T("开放信息", "Open Info");
        ConnectionRobotTabButton.Content = T("QQ机器人", "QQ Robot");
        ConnectionAuthTabButton.Content = T("认证", "Auth");

        ConnectionFrpImportButton.Content = T("导入frpc", "Import frpc");
        ConnectionThirdPartyFrpcImportButton.Content = T("导入第三方frpc", "Import third-party frpc");
        ConnectionFrpEditTomlButton.Content = T("编辑常规TOML", "Edit Regular TOML");
        ConnectionThirdPartyFrpcEditTomlButton.Content = T("编辑第三方TOML", "Edit Third-party TOML");
        UpdateConnectionFrpActionButtons();
        ConnectionFrpTitleTextBlock.Text = T("内网穿透配置", "FRP Configuration");
        ConnectionFrpCommandLabelTextBlock.Text = T("常规启动命令", "Regular Launch Command");
        ConnectionThirdPartyFrpcModeLabelTextBlock.Text = T("第三方模式", "Third-party Mode");
        ConnectionThirdPartyFrpcCommandLabelTextBlock.Text = T("第三方启动命令", "Third-party Launch Command");

        UpdateOsqToggleButtonText();
        OsqTitleTextBlock.Text = T("开放信息（OpenServerQuery）", "Open Info (OpenServerQuery)");
        OsqEnabledLabelTextBlock.Text = T("启用开放信息", "Enable Open Info");
        OsqAllowInsecureHttpLabelTextBlock.Text = T("允许 HTTP 外发", "Allow HTTP outbound");
        OsqListenPrefixLabelTextBlock.Text = T("监听地址", "Listen Prefix");
        OsqRequestTimeoutLabelTextBlock.Text = T("请求超时秒数", "Request Timeout Seconds");
        OsqIncludeServerInfoLabelTextBlock.Text = T("服务器信息", "Server Info");
        OsqIncludePlayersLabelTextBlock.Text = T("玩家列表", "Players");
        OsqIncludeEventsLabelTextBlock.Text = T("玩家事件", "Player Events");
        OsqIncludeChatsLabelTextBlock.Text = T("聊天", "Chats");
        OsqIncludeNotificationsLabelTextBlock.Text = T("通知", "Notifications");
        OsqIncludeMapLabelTextBlock.Text = T("地图数据", "Map Data");
        OsqEndpointHostLabelTextBlock.Text = T("上报端点", "Report Endpoint");
        OsqEndpointTokenLabelTextBlock.Text = T("端点令牌", "Endpoint Token");
        OsqEndpointAddButton.Content = T("添加", "Add");

        UpdateRobotToggleButtonText();
        RobotConfigTitleTextBlock.Text = T("QQ机器人配置", "QQ Robot Configuration");
        RobotOneBotLabelTextBlock.Text = T("OneBot WebSocket", "OneBot WebSocket");
        RobotAccessTokenLabelTextBlock.Text = T("访问令牌", "Access Token");
        RobotReconnectLabelTextBlock.Text = T("重连间隔秒数", "Reconnect Interval Seconds");
        RobotPollIntervalLabelTextBlock.Text = T("轮询间隔秒数", "Poll Interval Seconds");
        RobotDatabasePathLabelTextBlock.Text = T("数据库路径", "Database Path");
        RobotDefaultEncodingLabelTextBlock.Text = T("默认编码", "Default Encoding");
        RobotFallbackEncodingLabelTextBlock.Text = T("回退编码", "Fallback Encoding");
        RobotOsqPollLabelTextBlock.Text = T("OSQ轮询秒数", "OSQ Poll Seconds");
        RobotOsqTimeoutLabelTextBlock.Text = T("OSQ超时秒数", "OSQ Timeout Seconds");
        RobotSuperUsersLabelTextBlock.Text = T("超级管理员 QQ", "Super Admin QQ IDs");
        RobotOsqSourceHintTextBlock.Text = T(
            "OSQ 来源由“开放信息”页面统一接收，机器人不再单独监听端口。",
            "OSQ source is received by Open Info; the robot does not listen on its own port.");
        AuthSaveButton.Content = T("保存", "Save");
        AuthRefreshButton.Content = T("刷新", "Refresh");
        AuthDeployButton.Content = T("部署认证模组", "Deploy Auth Mod");
        AuthEnabledLabelTextBlock.Text = T("启用认证", "Enable Auth");
        AuthLoginTimeoutLabelTextBlock.Text = T("登录超时秒数", "Login Timeout Seconds");
        AuthRememberSessionLabelTextBlock.Text = T("会话记住分钟", "Remember Session Minutes");
        AuthDiscourseEnabledLabelTextBlock.Text = T("启用 Discourse 登录", "Enable Discourse Login");
        AuthDiscourseBaseUrlLabelTextBlock.Text = T("Discourse 地址", "Discourse URL");
        AuthDiscourseSecretLabelTextBlock.Text = T("共享密钥", "Shared Secret");
        AuthDiscoursePublicCallbackLabelTextBlock.Text = T("公开回调地址", "Public Callback URL");
        AuthDiscourseListenPrefixLabelTextBlock.Text = T("本地监听地址", "Local Listen Prefix");
        AuthPlayersTitleTextBlock.Text = T("玩家认证数据", "Player Auth Data");
        AuthRefreshPlayersButton.Content = T("刷新玩家", "Refresh Players");
        RebuildThirdPartyFrpcModeOptions();
    }

    private void InitializeSeries()
    {
        FillWithZero(_serverCpuSamples, RealtimeRangeSeconds);
        FillWithZero(_serverMemoryMbSamples, RealtimeRangeSeconds);
        FillWithZero(_robotCpuSamples, RealtimeRangeSeconds);
        FillWithZero(_robotMemoryMbSamples, RealtimeRangeSeconds);
        FillWithZero(_playersSamples, RealtimeRangeSeconds);
        FillWithZero(_networkLatencySamples, NetworkRangeCount);

        EventTickerCurrentText.Text = T("暂无玩家事件", "No player events");
        EventTickerNextText.Text = EventTickerCurrentText.Text;
        UpdateCardValues(_serverProcessService.GetCurrentStatus());
    }

    private void InitializeCollections()
    {
        ConsoleOutputTextBlock.Text = string.Empty;
        RefreshQuickCommandItems(_preferencesService.Load().QuickCommands);
        ProfilesListBox.ItemsSource = _profileItems;
        SavesListBox.ItemsSource = _saveItems;
        DownloadVersionsListBox.ItemsSource = _downloadVersionItems;
        ConfigServerLanguageComboBox.ItemsSource = ConfigServerLanguageOptions;
        ConfigWhitelistModeComboBox.ItemsSource = _configWhitelistModeOptions;
        ConfigDefaultRoleComboBox.ItemsSource = _configDefaultRoleOptions;
        ConfigPlayStyleComboBox.ItemsSource = _configPlayStyleOptions;
        ConfigWorldTypeComboBox.ItemsSource = _configWorldTypeOptions;
        ConfigSaveFileComboBox.ItemsSource = _configSaveItems;
        ConfigWorldRulesItemsControl.ItemsSource = _configWorldRuleItems;
        ConnectionThirdPartyFrpcModeComboBox.ItemsSource = _thirdPartyFrpcModeOptions;
        SettingsContributorsItemsControl.ItemsSource = _settingsContributorItems;
        SettingsSponsorsItemsControl.ItemsSource = _settingsSponsorItems;
        AutomationProfileComboBox.ItemsSource = _automationProfileItems;
        AutomationActionsItemsControl.ItemsSource = _automationActionWindowItems;
        AutomationBackupTimesItemsControl.ItemsSource = _automationBackupTimeItems;
        AutomationBroadcastsItemsControl.ItemsSource = _automationBroadcastItems;
        AutomationExportTimesItemsControl.ItemsSource = _automationExportTimeItems;
        AutomationRuntimeLogsListBox.ItemsSource = _automationRuntimeLogItems;
        ModProfileComboBox.ItemsSource = _modProfileItems;
        ModsListBox.ItemsSource = _modItems;
        AuthProfileComboBox.ItemsSource = _authProfileItems;
        AuthPlayersListBox.ItemsSource = _authPlayerItems;
        RebuildConfigChoiceOptions();
        RebuildThirdPartyFrpcModeOptions();
    }

    private static void FillWithZero(List<double> target, int count)
    {
        target.Clear();
        for (var i = 0; i < count; i++)
        {
            target.Add(0);
        }
    }

    private void OnDataTimerTick(object? sender, EventArgs e)
    {
        var status = _serverProcessService.GetCurrentStatus();
        var robotStatus = _robotService.GetCurrentStatus();
        var robotResources = SampleRobotResources(robotStatus);
        PushNextSample(_serverCpuSamples, status.IsRunning ? status.CpuPercent : 0);
        PushNextSample(_serverMemoryMbSamples, status.IsRunning ? BytesToMb(status.MemoryBytes) : 0);
        PushNextSample(_playersSamples, status.IsRunning ? status.OnlinePlayers : 0);

        PushNextSample(_robotCpuSamples, robotStatus.IsRunning ? robotResources.CpuPercent : 0);
        PushNextSample(_robotMemoryMbSamples, robotStatus.IsRunning ? BytesToMb(robotResources.MemoryBytes) : 0);
        if (DateTime.UtcNow.Second % 5 == 0)
        {
            var networkActive = _openServerQueryService.GetRuntimeStatus().IsListening ||
                                _frpService.GetCurrentStatus().IsRunning ||
                                _thirdPartyFrpcService.GetCurrentStatus().IsRunning;
            PushNextSample(_networkLatencySamples, networkActive ? 1 : 0, NetworkRangeCount);
        }

        UpdateCardValues(status);

        if (_selectedTab == MainTab.Monitor)
        {
            RenderSelectedMetricChart(status);
        }
    }

    private async void OnTickerTimerTick(object? sender, EventArgs e)
    {
        if (_selectedTab != MainTab.Monitor || _selectedMetric != HomeMetric.Players || _playerEvents.Count == 0 || _tickerAnimating)
        {
            return;
        }

        _tickerAnimating = true;
        _tickerIndex = (_tickerIndex + 1) % _playerEvents.Count;
        var nextText = _playerEvents[_tickerIndex];

        EventTickerNextText.Text = nextText;
        EventTickerCurrentText.RenderTransform = TransformOperations.Parse("translate(0px,-24px)");
        EventTickerNextText.RenderTransform = TransformOperations.Parse("translate(0px,0px)");

        await Task.Delay(260);

        EventTickerCurrentText.Text = nextText;
        EventTickerCurrentText.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
        EventTickerNextText.RenderTransform = TransformOperations.Parse("translate(0px,24px)");

        _tickerAnimating = false;
    }

    private void OnHomeSloganTimerTick(object? sender, EventArgs e)
    {
        if (!HomePanel.IsVisible)
        {
            return;
        }

        if (_homeSloganVisible)
        {
            HomeSloganTextBlock.Opacity = 0;
            _homeSloganVisible = false;
            return;
        }

        _homeSloganIndex = (_homeSloganIndex + 1) % HomeSlogans.Length;
        HomeSloganTextBlock.Text = T(HomeSlogans[_homeSloganIndex].Zh, HomeSlogans[_homeSloganIndex].En);
        HomeSloganTextBlock.Opacity = 1;
        _homeSloganVisible = true;
    }

    private void UpdateCardValues(ServerRuntimeStatus status)
    {
        var serverCpu = _serverCpuSamples[^1];
        var serverMemMb = _serverMemoryMbSamples[^1];
        ServerStatusCardValueText.Text = status.IsRunning
            ? T($"CPU {serverCpu:F1}%  内存 {serverMemMb:F0} MB", $"CPU {serverCpu:F1}%  Mem {serverMemMb:F0} MB")
            : T("未启动", "Stopped");

        var robotStatus = _robotService.GetCurrentStatus();
        var robotCpu = _robotCpuSamples[^1];
        var robotMemMb = _robotMemoryMbSamples[^1];
        RobotStatusCardValueText.Text = robotStatus.IsRunning
            ? T($"运行中  CPU {robotCpu:F1}%  内存 {robotMemMb:F0} MB", $"Running  CPU {robotCpu:F1}%  Mem {robotMemMb:F0} MB")
            : T("未启动", "Stopped");

        var currentPlayers = (int)Math.Round(_playersSamples[^1]);
        var peakPlayers = Math.Max(status.PeakOnlinePlayers, (int)Math.Round(_playersSamples.Max()));
        OnlinePlayersCardValueText.Text = T(
            $"在线 {currentPlayers}  最高 {peakPlayers}",
            $"Online {currentPlayers}  Peak {peakPlayers}");

        NetworkStatusCardValueText.Text = _isFrpRunning || _isThirdPartyFrpcRunning || _openServerQueryService.GetRuntimeStatus().IsListening
            ? T("连接服务已启动", "Connection service started")
            : T("未启动", "Stopped");
        LaunchActionTextBlock.Text = status.IsRunning ? T("停止服务器", "Stop Server") : T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(status.IsRunning ? LaunchStopIconData : LaunchStartIconData);
        LaunchServerButton.Classes.Set("running", status.IsRunning);
        RefreshLaunchButtonSummary(status.IsRunning);

        RenderThumbnailCharts();
    }

    private (double CpuPercent, long MemoryBytes) SampleRobotResources(RobotRuntimeStatus status)
    {
        if (!status.IsRunning || status.ProcessId is null)
        {
            return (0, 0);
        }

        try
        {
            using var process = Process.GetProcessById(status.ProcessId.Value);
            process.Refresh();
            var now = DateTimeOffset.UtcNow;
            var elapsedMs = Math.Max(1, (now - _robotLastCpuSampleUtc).TotalMilliseconds);
            if (_robotLastProcessorTime == TimeSpan.Zero)
            {
                _robotLastProcessorTime = process.TotalProcessorTime;
                _robotLastCpuSampleUtc = now;
                return (0, process.WorkingSet64);
            }

            if (elapsedMs >= 700)
            {
                var currentProcessorTime = process.TotalProcessorTime;
                var processorElapsedMs = Math.Max(0, (currentProcessorTime - _robotLastProcessorTime).TotalMilliseconds);
                _robotLastCpuPercent = Math.Clamp(
                    processorElapsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0,
                    0,
                    100);
                _robotLastProcessorTime = currentProcessorTime;
                _robotLastCpuSampleUtc = now;
            }

            return (_robotLastCpuPercent, process.WorkingSet64);
        }
        catch
        {
            return (0, 0);
        }
    }

    private void RenderSelectedMetricChart(ServerRuntimeStatus? statusOverride = null)
    {
        var status = statusOverride ?? _serverProcessService.GetCurrentStatus();
        switch (_selectedMetric)
        {
            case HomeMetric.Server:
                RenderServerChart(status);
                break;
            case HomeMetric.Robot:
                RenderRobotChart();
                break;
            case HomeMetric.Players:
                RenderPlayersChart(status);
                break;
            case HomeMetric.Network:
                RenderNetworkChart();
                break;
        }
    }

    private void RenderServerChart(ServerRuntimeStatus status)
    {
        var cpu = _serverCpuSamples[^1];
        var memoryMb = _serverMemoryMbSamples[^1];
        var yMax = NiceCeiling(Math.Max(100, Math.Max(_serverMemoryMbSamples.Max(), _serverCpuSamples.Max())));
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderDualLineChart(
            title: T("服务器状态", "Server Status"),
            topValue: status.IsRunning ? $"{cpu:F1}% / {memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为服务端进程 CPU%，绿线为服务端进程内存 MB", "60-second range. Blue is server process CPU%, green is memory MB."),
            primary: _serverCpuSamples,
            secondary: _serverMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            details:
            [
                (T("CPU", "CPU"), $"{cpu:F1}%"),
                (T("内存", "Memory"), $"{memoryMb:F0} MB"),
                (T("PID", "PID"), status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"),
                (T("运行时间", "Uptime"), uptime)
            ]);
    }

    private void RenderRobotChart()
    {
        var status = _robotService.GetCurrentStatus();
        var cpu = _robotCpuSamples[^1];
        var memoryMb = _robotMemoryMbSamples[^1];
        var yMax = NiceCeiling(Math.Max(100, Math.Max(_robotMemoryMbSamples.Max(), _robotCpuSamples.Max())));
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderDualLineChart(
            title: T("机器人状态", "Robot Status"),
            topValue: status.IsRunning ? $"{cpu:F1}% / {memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为 QQ 机器人 CPU%，绿线为内存 MB。", "60-second range. Blue is QQ robot CPU%, green is memory MB."),
            primary: _robotCpuSamples,
            secondary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            details:
            [
                (T("CPU", "CPU"), $"{cpu:F1}%"),
                (T("内存", "Memory"), $"{memoryMb:F0} MB"),
                (T("状态", "Status"), status.IsRunning ? T("运行中", "Running") : T("未启动", "Stopped")),
                (T("运行时间", "Uptime"), uptime)
            ]);
    }

    private void RenderPlayersChart(ServerRuntimeStatus status)
    {
        var currentPlayers = (int)Math.Round(_playersSamples[^1]);
        var peakPlayers = Math.Max(status.PeakOnlinePlayers, (int)Math.Round(_playersSamples.Max()));
        RenderSingleLineChart(
            title: T("在线玩家", "Online Players"),
            topValue: T($"{currentPlayers} 人", $"{currentPlayers} players"),
            summary: T("60 秒区间，数据来自服务端输出解析。", "60-second range parsed from server output."),
            primary: _playersSamples,
            yMin: 0,
            yMax: NiceCeiling(Math.Max(4, _playersSamples.Max() + 1)),
            yAxisFormatter: value => $"{Math.Round(value):F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: true,
            details:
            [
                (T("当前人数", "Current"), currentPlayers.ToString(CultureInfo.InvariantCulture)),
                (T("最高人数", "Peak"), peakPlayers.ToString(CultureInfo.InvariantCulture)),
                (T("事件数量", "Events"), _playerEvents.Count.ToString(CultureInfo.InvariantCulture)),
                (T("来源", "Source"), T("服务端输出", "Server output"))
            ]);
    }

    private void RenderNetworkChart()
    {
        RenderSingleLineChart(
            title: T("网络状态", "Network Status"),
            topValue: T("未配置", "Not configured"),
            summary: T("连接监控尚未配置，当前不展示模拟延迟。", "Connection monitor is not configured; no simulated latency is shown."),
            primary: _networkLatencySamples,
            yMin: 0,
            yMax: 100,
            yAxisFormatter: value => $"{value:F0}ms",
            xHint: T("最近 12 小时", "Last 12 hours"),
            showTicker: false,
            details:
            [
                (T("当前延迟", "Latency"), "--"),
                (T("丢包", "Packet loss"), "--"),
                (T("测试频率", "Frequency"), T("未启动", "Stopped")),
                (T("采样区间", "Range"), T("12 小时", "12 hours"))
            ]);
    }

    private void RenderDualLineChart(
        string title,
        string topValue,
        string summary,
        IReadOnlyList<double> primary,
        IReadOnlyList<double> secondary,
        double yMin,
        double yMax,
        Func<double, string> yAxisFormatter,
        string xHint,
        IReadOnlyList<(string Label, string Value)> details)
    {
        ChartTitleText.Text = title;
        ChartTopValueText.Text = topValue;
        ChartSummaryText.Text = summary;
        ChartXAxisText.Text = xHint;

        ChartLinePrimary.Points = BuildPolylinePoints(primary, yMin, yMax);
        ChartLineSecondary.Points = BuildPolylinePoints(secondary, yMin, yMax);
        ChartLineSecondary.IsVisible = true;

        SetYAxisLabels(yMin, yMax, yAxisFormatter);
        SetChartDetails(details);
        EventTickerContainer.IsVisible = false;
    }

    private void RenderSingleLineChart(
        string title,
        string topValue,
        string summary,
        IReadOnlyList<double> primary,
        double yMin,
        double yMax,
        Func<double, string> yAxisFormatter,
        string xHint,
        bool showTicker,
        IReadOnlyList<(string Label, string Value)> details)
    {
        ChartTitleText.Text = title;
        ChartTopValueText.Text = topValue;
        ChartSummaryText.Text = summary;
        ChartXAxisText.Text = xHint;

        ChartLinePrimary.Points = BuildPolylinePoints(primary, yMin, yMax);
        ChartLineSecondary.IsVisible = false;
        ChartLineSecondary.Points = [];

        SetYAxisLabels(yMin, yMax, yAxisFormatter);
        SetChartDetails(details);
        EventTickerContainer.IsVisible = showTicker;
    }

    private void SetYAxisLabels(double yMin, double yMax, Func<double, string> formatter)
    {
        var span = Math.Max(0.0001, yMax - yMin);
        var labels = new[]
        {
            yMax,
            yMin + span * 0.8,
            yMin + span * 0.6,
            yMin + span * 0.4,
            yMin + span * 0.2,
            yMin
        };

        YAxisLabelTop.Text = formatter(labels[0]);
        YAxisLabel2.Text = formatter(labels[1]);
        YAxisLabel3.Text = formatter(labels[2]);
        YAxisLabel4.Text = formatter(labels[3]);
        YAxisLabel5.Text = formatter(labels[4]);
        YAxisLabelBottom.Text = formatter(labels[5]);
    }

    private void SetChartDetails(IReadOnlyList<(string Label, string Value)> details)
    {
        var normalized = details.Take(4).ToArray();
        if (normalized.Length < 4)
        {
            normalized = normalized.Concat(Enumerable.Repeat((string.Empty, string.Empty), 4 - normalized.Length)).ToArray();
        }

        DetailOneLabelText.Text = normalized[0].Label;
        DetailOneValueText.Text = normalized[0].Value;
        DetailTwoLabelText.Text = normalized[1].Label;
        DetailTwoValueText.Text = normalized[1].Value;
        DetailThreeLabelText.Text = normalized[2].Label;
        DetailThreeValueText.Text = normalized[2].Value;
        DetailFourLabelText.Text = normalized[3].Label;
        DetailFourValueText.Text = normalized[3].Value;
    }

    private void RenderThumbnailCharts()
    {
        var serverYMax = NiceCeiling(Math.Max(100, Math.Max(_serverMemoryMbSamples.Max(), _serverCpuSamples.Max())));
        ServerStatusThumbLinePrimary.Points = BuildPolylinePoints(_serverCpuSamples, 0, serverYMax, ThumbnailWidth, ThumbnailHeight);
        ServerStatusThumbLineSecondary.Points = BuildPolylinePoints(_serverMemoryMbSamples, 0, serverYMax, ThumbnailWidth, ThumbnailHeight);
        RobotStatusThumbLinePrimary.Points = BuildPolylinePoints(_robotCpuSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        RobotStatusThumbLineSecondary.Points = BuildPolylinePoints(_robotMemoryMbSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        OnlinePlayersThumbLinePrimary.Points = BuildPolylinePoints(_playersSamples, 0, NiceCeiling(Math.Max(4, _playersSamples.Max() + 1)), ThumbnailWidth, ThumbnailHeight);
        NetworkStatusThumbLinePrimary.Points = BuildPolylinePoints(_networkLatencySamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
    }

    private static IList<Point> BuildPolylinePoints(
        IReadOnlyList<double> values,
        double yMin,
        double yMax,
        double width = ChartWidth,
        double height = ChartHeight)
    {
        if (values.Count <= 1)
        {
            return [new Point(0, height), new Point(width, height)];
        }

        var points = new List<Point>(values.Count);
        var denominator = Math.Max(0.0001, yMax - yMin);
        for (var i = 0; i < values.Count; i++)
        {
            var x = i * (width / (values.Count - 1));
            var normalized = Math.Clamp((values[i] - yMin) / denominator, 0, 1);
            var y = height - normalized * height;
            points.Add(new Point(x, y));
        }

        return new Points(points);
    }

    private void RefreshProfiles()
    {
        var profiles = _profileService.GetProfiles();
        _profileItems.Clear();
        foreach (var profile in profiles)
        {
            _profileItems.Add(ProfileListItem.FromProfile(profile));
        }

        var versions = _profileService.GetInstalledVersions();
        CreateVersionComboBox.ItemsSource = versions;
        if (CreateVersionComboBox.SelectedIndex < 0 && versions.Count > 0)
        {
            CreateVersionComboBox.SelectedIndex = 0;
        }

        RefreshLaunchOptions(profiles);
        _ = RefreshSavesAsync();
        _ = RefreshConfigProfilesAsync();
        _ = RefreshAutomationAsync();
        _ = RefreshModsAsync();
        _ = RefreshAuthProfilesAsync();
    }

    private void RefreshLaunchOptions(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        RefreshLaunchButtonSummary();
    }

    private async Task RefreshAutomationAsync()
    {
        if (_isRefreshingAutomation)
        {
            return;
        }

        _isRefreshingAutomation = true;
        try
        {
            var preferences = _preferencesService.Load();
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = AutomationProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                ? selectedProfile.Id
                : string.Empty;
            _automationProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _automationProfileItems.Add(profile);
            }

            AutomationProfileComboBox.ItemsSource = _automationProfileItems;
            if (_automationProfileItems.Count > 0)
            {
                var target = _automationProfileItems.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(selectedProfileId) &&
                    profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _automationProfileItems.FirstOrDefault(profile =>
                        profile.Id.Equals(preferences.DefaultLaunchProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _automationProfileItems.FirstOrDefault();
                AutomationProfileComboBox.SelectedItem = target;
            }

            var settings = await _automationSettingsService.LoadAsync();
            ApplyAutomationSettings(settings);
            AutomationStatusTextBlock.Text = T("自动化配置已加载。", "Automation settings loaded.");
            await SyncAutomationRuntimeLogsAsync();
        }
        catch (Exception ex)
        {
            AutomationStatusTextBlock.Text = T($"自动化加载失败：{ex.Message}", $"Automation load failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingAutomation = false;
        }
    }

    private void ApplyAutomationSettings(AutomationSettings settings)
    {
        AutomationRestartEnabledCheckBox.IsChecked = settings.RestartSchedulerEnabled;
        AutomationBackupEnabledCheckBox.IsChecked = settings.BackupEnabled;
        AutomationBackupBeforeShutdownCheckBox.IsChecked = settings.BackupBeforeShutdown;
        AutomationBroadcastEnabledCheckBox.IsChecked = settings.BroadcastEnabled;
        AutomationExportEnabledCheckBox.IsChecked = settings.ExportLogEnabled;
        AutomationExportBeforeShutdownCheckBox.IsChecked = settings.ExportBeforeShutdown;
        AutomationExportIncludeChatCheckBox.IsChecked = settings.ExportIncludeChat;
        AutomationExportIncludeServerCheckBox.IsChecked = settings.ExportIncludeServerInfo;

        _automationActionWindowItems.Clear();
        foreach (var window in settings.ActionWindows ?? [])
        {
            _automationActionWindowItems.Add(AutomationActionWindowItem.FromModel(window));
        }
        if (_automationActionWindowItems.Count == 0)
        {
            _automationActionWindowItems.Add(new AutomationActionWindowItem());
        }

        _automationBackupTimeItems.Clear();
        foreach (var time in settings.BackupTimes ?? [])
        {
            _automationBackupTimeItems.Add(new AutomationTimeItem(time));
        }
        if (_automationBackupTimeItems.Count == 0)
        {
            _automationBackupTimeItems.Add(new AutomationTimeItem("03:00"));
        }

        _automationBroadcastItems.Clear();
        foreach (var message in settings.BroadcastMessages ?? [])
        {
            _automationBroadcastItems.Add(ScheduledBroadcastItem.FromModel(message));
        }
        if (_automationBroadcastItems.Count == 0)
        {
            _automationBroadcastItems.Add(new ScheduledBroadcastItem());
        }

        _automationExportTimeItems.Clear();
        foreach (var time in settings.ExportTimes ?? [])
        {
            _automationExportTimeItems.Add(new AutomationTimeItem(time));
        }
        if (_automationExportTimeItems.Count == 0)
        {
            _automationExportTimeItems.Add(new AutomationTimeItem("12:00"));
        }
    }

    private AutomationSettings CollectAutomationSettings()
    {
        var selectedProfile = AutomationProfileComboBox.SelectedItem as InstanceProfile;
        return new AutomationSettings
        {
            TargetProfileId = selectedProfile?.Id ?? string.Empty,
            RestartSchedulerEnabled = AutomationRestartEnabledCheckBox.IsChecked == true,
            BackupEnabled = AutomationBackupEnabledCheckBox.IsChecked == true,
            BackupBeforeShutdown = AutomationBackupBeforeShutdownCheckBox.IsChecked == true,
            BroadcastEnabled = AutomationBroadcastEnabledCheckBox.IsChecked == true,
            ExportLogEnabled = AutomationExportEnabledCheckBox.IsChecked == true,
            ExportBeforeShutdown = AutomationExportBeforeShutdownCheckBox.IsChecked == true,
            ExportIncludeChat = AutomationExportIncludeChatCheckBox.IsChecked == true,
            ExportIncludeServerInfo = AutomationExportIncludeServerCheckBox.IsChecked == true,
            ActionWindows = _automationActionWindowItems.Select(item => item.ToModel()).ToList(),
            BackupTimes = _automationBackupTimeItems
                .Select(item => item.Time?.Trim() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList(),
            BroadcastMessages = _automationBroadcastItems
                .Select(item => item.ToModel())
                .Where(item => !string.IsNullOrWhiteSpace(item.Message) || !string.IsNullOrWhiteSpace(item.Time))
                .ToList(),
            ExportTimes = _automationExportTimeItems
                .Select(item => item.Time?.Trim() ?? string.Empty)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
    }

    private async Task SaveAutomationAsync()
    {
        try
        {
            var settings = CollectAutomationSettings();
            await _automationSettingsService.SaveAsync(settings);
            await _automationService.ReloadAsync();
            AutomationStatusTextBlock.Text = T("自动化配置已保存。", "Automation settings saved.");
        }
        catch (Exception ex)
        {
            AutomationStatusTextBlock.Text = T($"自动化保存失败：{ex.Message}", $"Automation save failed: {ex.Message}");
        }
    }

    private async Task SyncAutomationRuntimeLogsAsync()
    {
        _automationRuntimeLogItems.Clear();
        foreach (var line in _automationService.GetRuntimeLogs())
        {
            _automationRuntimeLogItems.Add(line);
        }

        await Task.CompletedTask;
    }

    private async Task RefreshModsAsync()
    {
        if (_isRefreshingMods)
        {
            return;
        }

        _isRefreshingMods = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = ModProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                ? selectedProfile.Id
                : string.Empty;
            _modProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _modProfileItems.Add(profile);
            }

            ModProfileComboBox.ItemsSource = _modProfileItems;
            if (_modProfileItems.Count > 0)
            {
                ModProfileComboBox.SelectedItem = _modProfileItems.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(selectedProfileId) &&
                    profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _modProfileItems.FirstOrDefault();
            }

            await LoadModsForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            ModStatusTextBlock.Text = T($"模组加载失败：{ex.Message}", $"Mod load failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingMods = false;
        }
    }

    private async Task LoadModsForSelectedProfileAsync()
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            _modItems.Clear();
            ModStatusTextBlock.Text = T("暂无档案，请先创建档案。", "No profile found. Create a profile first.");
            return;
        }

        var mods = await _instanceModService.GetModsAsync(profile);
        _modItems.Clear();
        foreach (var mod in mods)
        {
            _modItems.Add(ModListItem.FromModel(mod));
        }

        ModStatusTextBlock.Text = T($"已加载 {mods.Count} 个模组。", $"Loaded {mods.Count} mods.");
    }

    private async Task RefreshAuthProfilesAsync()
    {
        if (_isRefreshingAuth)
        {
            return;
        }

        _isRefreshingAuth = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            var selectedProfileId = AuthProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                ? selectedProfile.Id
                : string.Empty;
            _authProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _authProfileItems.Add(profile);
            }

            AuthProfileComboBox.ItemsSource = _authProfileItems;
            if (_authProfileItems.Count == 0)
            {
                _authPlayerItems.Clear();
                AuthStatusTextBlock.Text = T("暂无档案，请先创建档案。", "No profile found. Create a profile first.");
                return;
            }

            var target = _authProfileItems.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _authProfileItems.FirstOrDefault();
            AuthProfileComboBox.SelectedItem = target;
            if (target is not null)
            {
                await LoadAuthForProfileAsync(target);
            }
        }
        catch (Exception ex)
        {
            AuthStatusTextBlock.Text = T($"认证加载失败：{ex.Message}", $"Auth load failed: {ex.Message}");
        }
        finally
        {
            _isRefreshingAuth = false;
        }
    }

    private async Task LoadAuthForProfileAsync(InstanceProfile profile)
    {
        await _serverAuthService.EnsureAuthModDeployedAsync(profile);
        var settings = await _serverAuthService.LoadSettingsAsync(profile);
        ApplyAuthSettings(settings);
        await LoadAuthPlayersAsync(profile);
        var authModEnabled = await _serverAuthService.GetAuthModEnabledAsync(profile);
        AuthStatusTextBlock.Text = T(
            $"已加载认证配置，模组{(authModEnabled ? "已部署" : "未部署")}。",
            $"Auth settings loaded, mod {(authModEnabled ? "deployed" : "missing")}.");
    }

    private void ApplyAuthSettings(ServerAuthSettings settings)
    {
        AuthEnabledCheckBox.IsChecked = settings.Enabled;
        AuthLoginTimeoutNumericUpDown.Value = settings.LoginTimeoutSeconds;
        AuthRememberSessionNumericUpDown.Value = settings.RememberSessionMinutes;
        AuthDiscourseEnabledCheckBox.IsChecked = settings.Discourse.Enabled;
        AuthDiscourseBaseUrlTextBox.Text = settings.Discourse.BaseUrl;
        AuthDiscourseSecretTextBox.Text = settings.Discourse.SharedSecret;
        AuthDiscoursePublicCallbackTextBox.Text = settings.Discourse.PublicCallbackBaseUrl;
        AuthDiscourseListenPrefixTextBox.Text = settings.Discourse.ListenPrefix;
    }

    private ServerAuthSettings CollectAuthSettings()
    {
        return new ServerAuthSettings
        {
            Enabled = AuthEnabledCheckBox.IsChecked == true,
            LoginTimeoutSeconds = GetNumericValue(AuthLoginTimeoutNumericUpDown, 60),
            RememberSessionMinutes = GetNumericValue(AuthRememberSessionNumericUpDown, 30),
            Discourse = new ServerAuthDiscourseSettings
            {
                Enabled = AuthDiscourseEnabledCheckBox.IsChecked == true,
                BaseUrl = AuthDiscourseBaseUrlTextBox.Text?.Trim() ?? string.Empty,
                SharedSecret = AuthDiscourseSecretTextBox.Text?.Trim() ?? string.Empty,
                PublicCallbackBaseUrl = AuthDiscoursePublicCallbackTextBox.Text?.Trim() ?? string.Empty,
                ListenPrefix = AuthDiscourseListenPrefixTextBox.Text?.Trim() ?? string.Empty
            }
        };
    }

    private async Task LoadAuthPlayersAsync(InstanceProfile profile)
    {
        var players = await _serverAuthService.GetPlayersAsync(profile);
        _authPlayerItems.Clear();
        foreach (var player in players)
        {
            _authPlayerItems.Add(AuthPlayerListItem.FromModel(player));
        }
    }

    private void RefreshLaunchButtonSummary(bool? isRunning = null)
    {
        if (isRunning ?? _serverProcessService.GetCurrentStatus().IsRunning)
        {
            LaunchSelectionSummaryTextBlock.Text = T("运行中 | 点击停止", "Running | Click to stop");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        if (!TryGetLockedLaunchTarget(out var profile, out var lockedSavePath))
        {
            LaunchSelectionSummaryTextBlock.Text = T("未锁定默认存档", "No default save locked");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        var profileName = string.IsNullOrWhiteSpace(profile.Name) ? T("未选择档案", "No profile") : profile.Name;
        var fileName = Path.GetFileName(lockedSavePath);
        var saveName = string.IsNullOrWhiteSpace(fileName)
            ? (string.IsNullOrWhiteSpace(lockedSavePath) ? T("未固定存档", "No fixed save") : lockedSavePath)
            : fileName;
        LaunchSelectionSummaryTextBlock.Text = $"{profileName} | {saveName}";
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private async Task RefreshSavesAsync()
    {
        if (_isRefreshingSaves)
        {
            return;
        }

        _isRefreshingSaves = true;
        try
        {
            var selectedProfile = SaveProfileComboBox.SelectedItem;
            var profiles = _profileService.GetProfiles();
            var preferences = _preferencesService.Load();
            var lockedProfileId = string.IsNullOrWhiteSpace(preferences.DefaultLaunchProfileId)
                ? string.Empty
                : preferences.DefaultLaunchProfileId.Trim();
            var lockedSavePath = NormalizeFullPath(preferences.DefaultLaunchSaveFile);
            var saveProfileItems = new List<object> { T("全部档案", "All profiles") };
            saveProfileItems.AddRange(profiles);
            SaveProfileComboBox.ItemsSource = saveProfileItems;

            if (selectedProfile is InstanceProfile selectedInstance)
            {
                SaveProfileComboBox.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedInstance.Id) ?? saveProfileItems[0];
            }
            else if (SaveProfileComboBox.SelectedIndex < 0)
            {
                SaveProfileComboBox.SelectedIndex = 0;
            }

            InstanceProfile? filter = SaveProfileComboBox.SelectedItem as InstanceProfile;
            var saves = await _saveService.GetSavesAsync(filter);
            _saveItems.Clear();
            foreach (var save in saves)
            {
                var isLocked = !string.IsNullOrWhiteSpace(lockedProfileId) &&
                               !string.IsNullOrWhiteSpace(lockedSavePath) &&
                               save.ProfileId.Equals(lockedProfileId, StringComparison.OrdinalIgnoreCase) &&
                               NormalizeFullPath(save.FullPath).Equals(lockedSavePath, StringComparison.OrdinalIgnoreCase);
                _saveItems.Add(SaveListItem.FromSave(
                    save,
                    isLocked,
                    T("默认启动", "Default"),
                    T("锁定默认", "Set default")));
            }

            RefreshLaunchButtonSummary();
        }
        finally
        {
            _isRefreshingSaves = false;
        }
    }

    private async Task RefreshDownloadVersionsAsync(bool forceReload)
    {
        if (_downloadCatalogLoaded && !forceReload)
        {
            RebuildDownloadVersionItems();
            return;
        }

        SetDownloadStatus(T("正在加载服务端版本列表...", "Loading server versions..."));
        try
        {
            _catalogEntries.Clear();
            _catalogEntries.AddRange(await _serverPackageService.GetServerDownloadEntriesAsync());
            _downloadCatalogLoaded = true;
            RebuildDownloadVersionItems();
            SetDownloadStatus(T($"已加载 {_catalogEntries.Count} 个服务端版本。", $"Loaded {_catalogEntries.Count} server versions."));
        }
        catch (Exception ex)
        {
            _downloadCatalogLoaded = false;
            _catalogEntries.Clear();
            _downloadVersionItems.Clear();
            SetDownloadStatus(T($"加载失败：{ex.Message}", $"Load failed: {ex.Message}"));
        }
    }

    private void RebuildDownloadVersionItems()
    {
        var preferences = _preferencesService.Load();
        var installedVersions = _profileService.GetInstalledVersions().ToHashSet(StringComparer.OrdinalIgnoreCase);
        _downloadVersionItems.Clear();
        var searchKeyword = DownloadVersionSearchTextBox.Text?.Trim() ?? string.Empty;

        foreach (var entry in _catalogEntries)
        {
            if (!string.IsNullOrWhiteSpace(searchKeyword)
                && !entry.Version.Contains(searchKeyword, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isDownloaded = installedVersions.Contains(entry.Version) ||
                               File.Exists(Path.Combine(preferences.ServerDirectory, entry.FileName));
            _downloadVersionItems.Add(new DownloadVersionListItem(
                entry,
                entry.Version,
                isDownloaded,
                T("已下载", "Downloaded"),
                T("下载", "Download")));
        }
    }

    private void OnDownloadVersionSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        RebuildDownloadVersionItems();
    }

    private void SetDownloadStatus(string message)
    {
        DownloadStatusTextBlock.Text = message;
    }

    private void SelectTab(MainTab tab)
    {
        var previousTab = _selectedTab;
        _selectedTab = tab;
        var isHome = tab == MainTab.Home;

        HomePanel.IsVisible = isHome;
        NonHomePanelHost.IsVisible = !isHome;
        MonitorPanel.IsVisible = tab == MainTab.Monitor;
        ConsolePanel.IsVisible = tab == MainTab.Console;
        InstanceManagePanel.IsVisible = tab == MainTab.InstanceManage;
        SettingsPanel.IsVisible = tab == MainTab.Settings;
        ConnectionPanel.IsVisible = tab == MainTab.Connection;

        SetSelectedClass(HomeNavButton, tab == MainTab.Home);
        SetSelectedClass(MonitorNavButton, tab == MainTab.Monitor);
        SetSelectedClass(ConsoleNavButton, tab == MainTab.Console);
        SetSelectedClass(InstanceManageNavButton, tab == MainTab.InstanceManage);
        SetSelectedClass(SettingsNavButton, tab == MainTab.Settings);
        SetSelectedClass(ConnectionNavButton, tab == MainTab.Connection);

        if (tab == MainTab.Monitor)
        {
            RenderSelectedMetricChart();
        }

        if (tab == MainTab.Connection)
        {
            RefreshConnectionSettingsEditor();
            RefreshConnectionRuntimeStatus();
        }

        if (isHome)
        {
            NonHomePanelHost.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
        }
        else
        {
            ShowNonHomePanel(previousTab == MainTab.Home);
        }
    }

    private void ShowNonHomePanel(bool animate)
    {
        if (!animate)
        {
            NonHomePanelHost.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            return;
        }

        var offset = Math.Max(MainContentHost.Bounds.Height, NonHomePanelHost.Bounds.Height);
        if (offset < 1)
        {
            offset = 480;
        }

        var offsetText = offset.ToString(CultureInfo.InvariantCulture);
        NonHomePanelHost.RenderTransform = TransformOperations.Parse($"translate(0px,{offsetText}px)");
        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedTab != MainTab.Home)
            {
                NonHomePanelHost.RenderTransform = TransformOperations.Parse("translate(0px,0px)");
            }
        }, DispatcherPriority.Render);
    }

    private void SelectMetric(HomeMetric metric)
    {
        _selectedMetric = metric;
        SetSelectedClass(ServerStatusCard, metric == HomeMetric.Server);
        SetSelectedClass(RobotStatusCard, metric == HomeMetric.Robot);
        SetSelectedClass(OnlinePlayersCard, metric == HomeMetric.Players);
        SetSelectedClass(NetworkStatusCard, metric == HomeMetric.Network);
        RenderSelectedMetricChart();
    }

    private void SelectInstanceManageTab(InstanceManageTab tab)
    {
        _selectedInstanceManageTab = tab;
        ProfilesPanel.IsVisible = tab == InstanceManageTab.Profiles;
        ConfigPanel.IsVisible = tab == InstanceManageTab.Config;
        SavesPanel.IsVisible = tab == InstanceManageTab.Saves;
        AutomationPanel.IsVisible = tab == InstanceManageTab.Automation;
        ModsPanel.IsVisible = tab == InstanceManageTab.Mods;
        DownloadVersionsPanel.IsVisible = tab == InstanceManageTab.DownloadVersions;
        SetSelectedClass(ProfilesTabButton, tab == InstanceManageTab.Profiles);
        SetSelectedClass(ConfigTabButton, tab == InstanceManageTab.Config);
        SetSelectedClass(SavesTabButton, tab == InstanceManageTab.Saves);
        SetSelectedClass(AutomationTabButton, tab == InstanceManageTab.Automation);
        SetSelectedClass(ModsTabButton, tab == InstanceManageTab.Mods);
        SetSelectedClass(DownloadVersionsTabButton, tab == InstanceManageTab.DownloadVersions);

        if (tab == InstanceManageTab.Config)
        {
            _ = RefreshConfigProfilesAsync();
        }
        else if (tab == InstanceManageTab.Automation)
        {
            _ = RefreshAutomationAsync();
        }
        else if (tab == InstanceManageTab.Mods)
        {
            _ = RefreshModsAsync();
        }
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        _selectedSettingsTab = tab;
        SetSelectedClass(ServerSettingsTabButton, tab == SettingsTab.Server);
        SetSelectedClass(AppearanceSettingsTabButton, tab == SettingsTab.Appearance);
        SetSelectedClass(NetworkSettingsTabButton, tab == SettingsTab.Network);
        SetSelectedClass(AdvancedSettingsTabButton, tab == SettingsTab.Advanced);
        SetSelectedClass(AboutSettingsTabButton, tab == SettingsTab.About);
        SetSelectedClass(SponsorsSettingsTabButton, tab == SettingsTab.Sponsors);
        SetSelectedClass(ContributorsSettingsTabButton, tab == SettingsTab.Contributors);
        var isServer = tab == SettingsTab.Server;
        var isAppearance = tab == SettingsTab.Appearance;
        var isNetwork = tab == SettingsTab.Network;
        var isAdvanced = tab == SettingsTab.Advanced;
        var isAbout = tab == SettingsTab.About;
        var isSponsors = tab == SettingsTab.Sponsors;
        var isContributors = tab == SettingsTab.Contributors;
        SettingsServerPanel.IsVisible = isServer;
        SettingsAppearancePanel.IsVisible = isAppearance;
        SettingsNetworkPanel.IsVisible = isNetwork;
        SettingsAdvancedPanel.IsVisible = isAdvanced;
        SettingsAboutPanel.IsVisible = isAbout;
        SettingsSponsorsPanel.IsVisible = isSponsors;
        SettingsContributorsPanel.IsVisible = isContributors;
        SettingsBlankPanel.IsVisible = !isServer &&
                                       !isAppearance &&
                                       !isNetwork &&
                                       !isAdvanced &&
                                       !isAbout &&
                                       !isSponsors &&
                                       !isContributors;

        if (isServer)
        {
            RefreshServerSettingsEditor();
        }
        else if (isAppearance)
        {
            RefreshAppearanceSettingsEditor();
        }
        else if (isNetwork)
        {
            RefreshNetworkSettingsEditor();
        }
        else if (isAbout)
        {
            LoadAboutMarkdown();
        }
        else if (isSponsors)
        {
            if (!_sponsorsLoaded)
            {
                _ = RefreshSponsorsAsync();
            }
        }
        else if (isContributors && !_contributorsLoaded)
        {
            _ = RefreshContributorsAsync();
        }
    }

    private void SelectConnectionTab(ConnectionTab tab)
    {
        _selectedConnectionTab = tab;
        ConnectionFrpPanel.IsVisible = tab == ConnectionTab.Frp;
        ConnectionOpenInfoPanel.IsVisible = tab == ConnectionTab.OpenInfo;
        ConnectionRobotPanel.IsVisible = tab == ConnectionTab.Robot;
        ConnectionAuthPanel.IsVisible = tab == ConnectionTab.Auth;
        SetSelectedClass(ConnectionFrpTabButton, tab == ConnectionTab.Frp);
        SetSelectedClass(ConnectionOpenInfoTabButton, tab == ConnectionTab.OpenInfo);
        SetSelectedClass(ConnectionRobotTabButton, tab == ConnectionTab.Robot);
        SetSelectedClass(ConnectionAuthTabButton, tab == ConnectionTab.Auth);
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
        if (tab == ConnectionTab.Auth)
        {
            _ = RefreshAuthProfilesAsync();
        }
    }

    private void RegisterAutoSaveHandlers()
    {
        SettingsWorkspaceDirectoryTextBox.LostFocus += OnServerSettingsAutoSaveChanged;
        SettingsQuickCommandsTextBox.LostFocus += OnServerSettingsAutoSaveChanged;

        foreach (var check in new[]
                 {
                     SettingsStartWithWindowsCheckBox,
                     SettingsCloseToTrayCheckBox,
                     SettingsStartHiddenCheckBox,
                     SettingsAutoStartOsqCheckBox,
                     SettingsAutoStartRobotCheckBox,
                     SettingsAutoStartFrpCheckBox,
                     SettingsAutoStartThirdPartyFrpcCheckBox
                 })
        {
            check.IsCheckedChanged += OnServerSettingsAutoSaveChanged;
        }

        SettingsThirdPartyServerTextBox.LostFocus += OnNetworkSettingsAutoSaveChanged;
        SettingsDownloadChunkCountTextBox.LostFocus += OnNetworkSettingsAutoSaveChanged;
        SettingsChunkedDownloadToggleSwitch.IsCheckedChanged += OnNetworkSettingsAutoSaveChanged;

        ConnectionFrpCommandTextBox.LostFocus += OnFrpAutoSaveChanged;
        ConnectionThirdPartyFrpcCommandTextBox.LostFocus += OnFrpAutoSaveChanged;

        OsqListenPrefixTextBox.LostFocus += OnOpenInfoAutoSaveChanged;
        OsqRequestTimeoutNumericUpDown.LostFocus += OnOpenInfoAutoSaveChanged;
        foreach (var check in new[]
                 {
                     OsqEnabledCheckBox,
                     OsqAllowInsecureHttpCheckBox,
                     OsqIncludeServerInfoCheckBox,
                     OsqIncludePlayersCheckBox,
                     OsqIncludeEventsCheckBox,
                     OsqIncludeChatsCheckBox,
                     OsqIncludeNotificationsCheckBox,
                     OsqIncludeMapCheckBox
                 })
        {
            check.IsCheckedChanged += OnOpenInfoAutoSaveChanged;
        }

        RobotOneBotTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotAccessTokenTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotDatabasePathTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotDefaultEncodingTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotFallbackEncodingTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotSuperUsersTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotReconnectNumericUpDown.LostFocus += OnRobotAutoSaveChanged;
        RobotPollIntervalNumericUpDown.LostFocus += OnRobotAutoSaveChanged;
        RobotOsqPollNumericUpDown.LostFocus += OnRobotAutoSaveChanged;
        RobotOsqTimeoutNumericUpDown.LostFocus += OnRobotAutoSaveChanged;
    }

    private void OnServerSettingsAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnNetworkSettingsAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingNetworkSettings)
        {
            return;
        }

        SaveNetworkSettings(refreshEditor: false);
    }

    private void OnFrpAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveFrpSettings(updateStatus: false, refreshEditor: false);
    }

    private void OnOpenInfoAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveOpenServerQuerySettings(updateStatus: false, refreshEditor: false);
    }

    private void OnRobotAutoSaveChanged(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        SaveRobotSettings(updateStatus: false, refreshEditor: false);
    }

    private void RefreshQuickCommandItems(IEnumerable<string>? commands)
    {
        QuickCommandComboBox.ItemsSource = NormalizeQuickCommands(commands);
        QuickCommandComboBox.SelectedIndex = -1;
    }

    private static string FormatQuickCommands(IEnumerable<string>? commands)
    {
        return string.Join(Environment.NewLine, NormalizeQuickCommands(commands));
    }

    private static List<string> ParseQuickCommands(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        return NormalizeQuickCommands(text.Split(
            ["\r\n", "\n", "\r"],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static List<string> NormalizeQuickCommands(IEnumerable<string>? commands)
    {
        var result = new List<string>();
        foreach (var command in commands ?? [])
        {
            var normalized = command?.Trim();
            if (!string.IsNullOrWhiteSpace(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    private void RefreshServerSettingsEditor()
    {
        _isApplyingServerSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            SettingsWorkspaceDirectoryTextBox.Text = preferences.WorkspaceRoot;
            SettingsQuickCommandsTextBox.Text = FormatQuickCommands(preferences.QuickCommands);
            SettingsStartWithWindowsCheckBox.IsChecked = preferences.StartWithWindows;
            SettingsCloseToTrayCheckBox.IsChecked = preferences.CloseToTrayOnExit;
            SettingsStartHiddenCheckBox.IsChecked = preferences.StartHiddenOnLaunch;
            SettingsAutoStartOsqCheckBox.IsChecked = preferences.AutoStartOpenServerQueryOnLaunch;
            SettingsAutoStartRobotCheckBox.IsChecked = preferences.AutoStartRobotOnLaunch;
            SettingsAutoStartFrpCheckBox.IsChecked = preferences.AutoStartFrpOnLaunch;
            SettingsAutoStartThirdPartyFrpcCheckBox.IsChecked = preferences.AutoStartThirdPartyFrpcOnLaunch;
            SettingsServerStatusTextBlock.Text = T("已加载服务器设置。", "Server settings loaded.");
        }
        finally
        {
            _isApplyingServerSettings = false;
        }
    }

    private void SaveServerSettings(bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.WorkspaceRoot = SettingsWorkspaceDirectoryTextBox.Text?.Trim() ?? string.Empty;
        preferences.QuickCommands = ParseQuickCommands(SettingsQuickCommandsTextBox.Text);
        preferences.StartWithWindows = SettingsStartWithWindowsCheckBox.IsChecked == true;
        preferences.CloseToTrayOnExit = SettingsCloseToTrayCheckBox.IsChecked == true;
        preferences.StartHiddenOnLaunch = SettingsStartHiddenCheckBox.IsChecked == true;
        preferences.AutoStartOpenServerQueryOnLaunch = SettingsAutoStartOsqCheckBox.IsChecked == true;
        preferences.AutoStartRobotOnLaunch = SettingsAutoStartRobotCheckBox.IsChecked == true;
        preferences.AutoStartFrpOnLaunch = SettingsAutoStartFrpCheckBox.IsChecked == true;
        preferences.AutoStartThirdPartyFrpcOnLaunch = SettingsAutoStartThirdPartyFrpcCheckBox.IsChecked == true;
        _preferencesService.Save(preferences);
        RefreshQuickCommandItems(preferences.QuickCommands);
        try
        {
            ApplyWindowsStartupRegistration(preferences.StartWithWindows);
        }
        catch (Exception ex)
        {
            SettingsServerStatusTextBlock.Text = T($"开机启动设置失败：{ex.Message}", $"Startup registration failed: {ex.Message}");
        }

        if (refreshEditor)
        {
            RefreshServerSettingsEditor();
        }
    }

    private void RefreshNetworkSettingsEditor()
    {
        _isApplyingNetworkSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            SettingsThirdPartyServerTextBox.Text = string.IsNullOrWhiteSpace(preferences.ServerDownloadCatalogUrl)
                ? DefaultServerDownloadCatalogUrl
                : preferences.ServerDownloadCatalogUrl;
            SettingsChunkedDownloadToggleSwitch.IsChecked = preferences.EnableChunkedDownloads;
            SettingsDownloadChunkCountTextBox.Text = Math.Clamp(preferences.DownloadChunkCount, 1, 32).ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isApplyingNetworkSettings = false;
        }
    }

    private void SaveNetworkSettings(bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.ServerDownloadCatalogUrl = SettingsThirdPartyServerTextBox.Text?.Trim() ?? string.Empty;
        preferences.EnableChunkedDownloads = SettingsChunkedDownloadToggleSwitch.IsChecked == true;
        preferences.DownloadChunkCount = ParseClampedInt(SettingsDownloadChunkCountTextBox.Text, 4, 1, 32);
        _preferencesService.Save(preferences);
        _downloadCatalogLoaded = false;

        if (refreshEditor)
        {
            RefreshNetworkSettingsEditor();
        }
    }

    private void LoadAboutMarkdown()
    {
        if (_aboutMarkdownLoaded)
        {
            return;
        }

        var fileName = _isChinese ? "README.md" : "README_en.md";
        var path = FindBundledReadmePath(fileName);
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            SetAboutFallbackText(T("未找到 README.md。", "README_en.md was not found."));
            _aboutMarkdownLoaded = true;
            return;
        }

        try
        {
            RenderAboutMarkdown(File.ReadAllText(path));
        }
        catch (Exception ex)
        {
            SetAboutFallbackText(T($"读取 README 失败：{ex.Message}", $"Failed to read README: {ex.Message}"));
        }

        _aboutMarkdownLoaded = true;
    }

    private void RenderAboutMarkdown(string markdown)
    {
        var sanitized = SanitizeAboutMarkdown(markdown);
        try
        {
            SettingsAboutContentHost.Content = BuildAboutMarkdownView(sanitized);
        }
        catch
        {
            SetAboutFallbackText(sanitized);
        }
    }

    private void SetAboutFallbackText(string text)
    {
        SettingsAboutContentHost.Content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = new SelectableTextBlock
            {
                Text = text,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 13
            }
        };
    }

    private Control BuildAboutMarkdownView(string markdown)
    {
        var host = new StackPanel
        {
            Spacing = 6
        };

        var lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var inCodeBlock = false;
        var codeBuffer = new StringBuilder();
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var trimmed = line.Trim();
            if (trimmed.StartsWith("```", StringComparison.Ordinal))
            {
                if (inCodeBlock)
                {
                    AddAboutCodeBlock(host, codeBuffer.ToString().TrimEnd());
                    codeBuffer.Clear();
                    inCodeBlock = false;
                }
                else
                {
                    inCodeBlock = true;
                }

                continue;
            }

            if (inCodeBlock)
            {
                codeBuffer.AppendLine(line);
                continue;
            }

            if (string.IsNullOrWhiteSpace(trimmed))
            {
                continue;
            }

            if (IsMarkdownTableStart(lines, i))
            {
                var tableRows = new List<string> { lines[i] };
                i += 2;
                while (i < lines.Length && IsMarkdownTableRow(lines[i]))
                {
                    tableRows.Add(lines[i]);
                    i++;
                }

                i--;
                AddAboutTable(host, tableRows);
                continue;
            }

            if (trimmed.StartsWith("# ", StringComparison.Ordinal))
            {
                host.Children.Add(CreateAboutText(CleanInlineMarkdown(trimmed[2..]), "AboutHeading1"));
                continue;
            }

            if (trimmed.StartsWith("## ", StringComparison.Ordinal))
            {
                host.Children.Add(CreateAboutText(CleanInlineMarkdown(trimmed[3..]), "AboutHeading2"));
                continue;
            }

            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
            {
                host.Children.Add(CreateAboutText($"• {CleanInlineMarkdown(trimmed[2..])}", "AboutSubText"));
                continue;
            }

            var text = CleanInlineMarkdown(trimmed);
            if (!string.IsNullOrWhiteSpace(text))
            {
                host.Children.Add(CreateAboutText(text, "AboutParagraph"));
            }
        }

        if (inCodeBlock && codeBuffer.Length > 0)
        {
            AddAboutCodeBlock(host, codeBuffer.ToString().TrimEnd());
        }

        return new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = host
        };
    }

    private static TextBlock CreateAboutText(string text, string className)
    {
        var textBlock = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap
        };
        textBlock.Classes.Add(className);
        return textBlock;
    }

    private static void AddAboutCodeBlock(StackPanel host, string code)
    {
        var textBlock = CreateAboutText(code, "AboutCodeText");
        var border = new Border
        {
            Child = textBlock
        };
        border.Classes.Add("AboutCodeBlock");
        host.Children.Add(border);
    }

    private static void AddAboutTable(StackPanel host, IReadOnlyList<string> rawRows)
    {
        var rows = rawRows
            .Select(SplitMarkdownTableRow)
            .Where(row => row.Count > 0)
            .ToList();
        if (rows.Count == 0)
        {
            return;
        }

        var columnCount = rows.Max(row => row.Count);
        var grid = new Grid
        {
            ColumnSpacing = 0,
            RowSpacing = 0,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch
        };
        grid.Classes.Add("AboutTable");

        for (var column = 0; column < columnCount; column++)
        {
            grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Star));
        }

        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            for (var column = 0; column < columnCount; column++)
            {
                var cellText = column < rows[row].Count ? CleanInlineMarkdown(rows[row][column]) : string.Empty;
                var cell = new Border
                {
                    Child = CreateAboutText(
                        cellText,
                        row == 0 ? "AboutTableHeaderText" : "AboutTableCellText")
                };
                cell.Classes.Add(row == 0 ? "AboutTableHeaderCell" : "AboutTableCell");
                Grid.SetRow(cell, row);
                Grid.SetColumn(cell, column);
                grid.Children.Add(cell);
            }
        }

        host.Children.Add(grid);
    }

    private static bool IsMarkdownTableStart(IReadOnlyList<string> lines, int index)
    {
        return index + 1 < lines.Count &&
               IsMarkdownTableRow(lines[index]) &&
               IsMarkdownTableSeparator(lines[index + 1]);
    }

    private static bool IsMarkdownTableRow(string line)
    {
        var trimmed = line.Trim();
        return trimmed.Length >= 3 &&
               trimmed.StartsWith('|') &&
               trimmed.EndsWith('|') &&
               trimmed.Count(character => character == '|') >= 2;
    }

    private static bool IsMarkdownTableSeparator(string line)
    {
        var cells = SplitMarkdownTableRow(line);
        return cells.Count > 0 &&
               cells.All(cell => Regex.IsMatch(cell.Trim(), "^:?-{3,}:?$"));
    }

    private static List<string> SplitMarkdownTableRow(string line)
    {
        var trimmed = line.Trim();
        if (trimmed.StartsWith('|'))
        {
            trimmed = trimmed[1..];
        }

        if (trimmed.EndsWith('|'))
        {
            trimmed = trimmed[..^1];
        }

        return trimmed
            .Split('|')
            .Select(cell => cell.Trim())
            .ToList();
    }

    private static string CleanInlineMarkdown(string value)
    {
        var result = Regex.Replace(value, @"`([^`]+)`", "$1");
        result = Regex.Replace(result, @"\[([^\]]+)\]\(([^\)]+)\)", "$1 ($2)");
        result = Regex.Replace(result, @"<br\s*/?>", " ", RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"</?(p|span|strong)[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        result = Regex.Replace(result, @"<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(result).Trim();
    }

    private static string SanitizeAboutMarkdown(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return string.Empty;
        }

        return Regex.Replace(markdown, @"(?m)^\s*!\[[^\r\n\]]*\]\([^\r\n)]*\)\s*$", string.Empty).Trim();
    }

    private async Task RefreshContributorsAsync(bool forceReload = false)
    {
        if (_contributorsLoaded && !forceReload)
        {
            return;
        }

        try
        {
            using var response = await SharedHttpClient.GetAsync(GitHubContributorsApiUrl);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync();
            using var document = await JsonDocument.ParseAsync(stream);

            _settingsContributorItems.Clear();
            foreach (var contributor in document.RootElement.EnumerateArray())
            {
                var login = ReadJsonString(contributor, "login");
                if (string.IsNullOrWhiteSpace(login))
                {
                    continue;
                }

                var contributions = contributor.TryGetProperty("contributions", out var contributionsNode) &&
                                    contributionsNode.TryGetInt32(out var parsed)
                    ? parsed
                    : 0;
                _settingsContributorItems.Add(new SettingsContributorItem
                {
                    Login = login,
                    HtmlUrl = ReadJsonString(contributor, "html_url"),
                    AvatarImage = await LoadAvatarImageAsync(ReadJsonString(contributor, "avatar_url")),
                    ContributionsText = T($"贡献 {contributions} 次", $"{contributions} contributions")
                });
            }

            _contributorsLoaded = true;
        }
        catch
        {
            _settingsContributorItems.Clear();
        }
    }

    private async Task RefreshSponsorsAsync(bool forceReload = false)
    {
        if (_sponsorsLoaded && !forceReload)
        {
            return;
        }

        try
        {
            using var response = await SharedHttpClient.GetAsync(GetSponsorApiUrl());
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadAsStringAsync();
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.TryGetProperty("ok", out var okNode) &&
                okNode.ValueKind == JsonValueKind.False)
            {
                var message = ReadJsonString(document.RootElement, "message");
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "Sponsor API failed." : message);
            }

            _settingsSponsorItems.Clear();
            if (TryGetSponsorList(document.RootElement, out var listNode))
            {
                foreach (var sponsor in listNode.EnumerateArray())
                {
                    var item = await BuildSponsorItemAsync(sponsor);
                    if (!string.IsNullOrWhiteSpace(item.Name))
                    {
                        _settingsSponsorItems.Add(item);
                    }
                }
            }

            _sponsorsLoaded = true;
        }
        catch
        {
            _settingsSponsorItems.Clear();
        }
    }

    private async Task OpenAppLogsAsync()
    {
        var logDirectory = GetAppLogDirectory();
        Directory.CreateDirectory(logDirectory);

        var latestLog = Directory.EnumerateFiles(logDirectory, "LauncherGo-*.log", SearchOption.TopDirectoryOnly)
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        var target = latestLog ?? logDirectory;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
            SettingsAdvancedStatusTextBlock.Text = T("已打开软件日志。", "App logs opened.");
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"打开软件日志失败：{ex.Message}", $"Failed to open app logs: {ex.Message}");
        }

        await Task.CompletedTask;
    }

    private async Task ClearDownloadCacheAsync()
    {
        try
        {
            var preferences = _preferencesService.Load();
            var deleted = await _serverPackageService.ClearDownloadCacheAsync(preferences.ServerDirectory);
            _downloadCatalogLoaded = false;
            await RefreshDownloadVersionsAsync(forceReload: true);
            SettingsAdvancedStatusTextBlock.Text = T($"已清空下载缓存：{deleted} 个文件。", $"Download cache cleared: {deleted} files.");
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"清空下载缓存失败：{ex.Message}", $"Failed to clear download cache: {ex.Message}");
        }
    }

    private void ResetAllSettingsAndRestartToGuide()
    {
        try
        {
            _preferencesService.Save(new LauncherPreferences { IsOnboardingCompleted = false });
            var executablePath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(executablePath))
            {
                Process.Start(new ProcessStartInfo { FileName = executablePath, UseShellExecute = true });
            }

            if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                _isExitRequested = true;
                desktop.Shutdown();
            }
        }
        catch (Exception ex)
        {
            SettingsAdvancedStatusTextBlock.Text = T($"重置设置失败：{ex.Message}", $"Failed to reset settings: {ex.Message}");
        }
    }

    private static void ApplyWindowsStartupRegistration(bool enabled)
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        const string runKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string valueName = "LauncherGo";
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(runKeyPath, writable: true);
        if (key is null)
        {
            return;
        }

        if (!enabled)
        {
            key.DeleteValue(valueName, throwOnMissingValue: false);
            return;
        }

        var executablePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            using var process = Process.GetCurrentProcess();
            executablePath = process.MainModule?.FileName;
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            return;
        }

        key.SetValue(valueName, $"\"{executablePath}\"", Microsoft.Win32.RegistryValueKind.String);
    }

    private void RefreshConnectionSettingsEditor()
    {
        _isApplyingConnectionSettings = true;
        try
        {
            RebuildThirdPartyFrpcModeOptions();

            var preferences = _preferencesService.Load();
            ApplyFrpSettings(preferences.Frp);
            ApplyOpenServerQuerySettings(preferences.OpenServerQuery);
            ApplyRobotSettings(preferences.Robot);
        }
        finally
        {
            _isApplyingConnectionSettings = false;
        }
    }

    private void ApplyFrpSettings(FrpIntegrationSettings settings)
    {
        ConnectionFrpCommandTextBox.Text = settings.FrpCommand;
        SelectConfigChoiceByValue(
            ConnectionThirdPartyFrpcModeComboBox,
            _thirdPartyFrpcModeOptions,
            settings.ThirdPartyFrpcLaunchMode.ToString());
        ConnectionThirdPartyFrpcCommandTextBox.Text = settings.ThirdPartyFrpcCommand;
    }

    private void ApplyOpenServerQuerySettings(OpenServerQuerySettings settings)
    {
        OsqEnabledCheckBox.IsChecked = settings.Enabled;
        OsqAllowInsecureHttpCheckBox.IsChecked = settings.AllowInsecureHttp;
        OsqListenPrefixTextBox.Text = settings.ListenPrefix;
        SetNumericValue(OsqRequestTimeoutNumericUpDown, settings.RequestTimeoutSec);
        OsqIncludeServerInfoCheckBox.IsChecked = settings.IncludeServerInfo;
        OsqIncludePlayersCheckBox.IsChecked = settings.IncludePlayers;
        OsqIncludeEventsCheckBox.IsChecked = settings.IncludePlayerEvents;
        OsqIncludeChatsCheckBox.IsChecked = settings.IncludeChats;
        OsqIncludeNotificationsCheckBox.IsChecked = settings.IncludeNotifications;
        OsqIncludeMapCheckBox.IsChecked = settings.IncludeMapData;
        RebuildOsqEndpointEditors(GetOpenServerQueryEndpointConfigsForEditor(settings));
    }

    private static IReadOnlyList<OpenServerQueryEndpointConfig> GetOpenServerQueryEndpointConfigsForEditor(OpenServerQuerySettings settings)
    {
        var endpoints = (settings.Endpoints ?? [])
            .Select(endpoint => new OpenServerQueryEndpointConfig
            {
                ServerHost = endpoint.ServerHost?.Trim() ?? string.Empty,
                Token = endpoint.Token?.Trim() ?? string.Empty,
                Enabled = endpoint.Enabled
            })
            .Where(endpoint => !string.IsNullOrWhiteSpace(endpoint.ServerHost) || !string.IsNullOrWhiteSpace(endpoint.Token))
            .ToList();

        if (endpoints.Count == 0 &&
            (!string.IsNullOrWhiteSpace(settings.EndpointHost) || !string.IsNullOrWhiteSpace(settings.EndpointToken)))
        {
            endpoints.Add(new OpenServerQueryEndpointConfig
            {
                ServerHost = settings.EndpointHost?.Trim() ?? string.Empty,
                Token = settings.EndpointToken?.Trim() ?? string.Empty,
                Enabled = true
            });
        }

        if (endpoints.Count == 0)
        {
            endpoints.Add(new OpenServerQueryEndpointConfig());
        }

        return endpoints;
    }

    private void RebuildOsqEndpointEditors(IReadOnlyList<OpenServerQueryEndpointConfig> endpoints)
    {
        OsqEndpointRowsHost.Children.Clear();
        _osqEndpointEditors.Clear();

        foreach (var endpoint in endpoints)
        {
            AddOsqEndpointEditorRow(endpoint.ServerHost, endpoint.Token, endpoint.Enabled);
        }
    }

    private void AddOsqEndpointEditorRow(string serverHost, string token, bool enabled = true)
    {
        var rowWidth = OsqEndpointColumnWidth * 2 + OsqEndpointColumnSpacing;
        var row = new Grid
        {
            Width = rowWidth,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            ColumnDefinitions = new ColumnDefinitions($"{OsqEndpointColumnWidth},{OsqEndpointColumnWidth}"),
            ColumnSpacing = OsqEndpointColumnSpacing
        };

        var hostTextBox = new TextBox
        {
            Text = serverHost
        };
        hostTextBox.Classes.Add("CompactInput");
        hostTextBox.LostFocus += OnOpenInfoAutoSaveChanged;
        row.Children.Add(hostTextBox);

        var tokenTextBox = new TextBox
        {
            Text = token
        };
        tokenTextBox.Classes.Add("CompactInput");
        tokenTextBox.LostFocus += OnOpenInfoAutoSaveChanged;
        Grid.SetColumn(tokenTextBox, 1);
        row.Children.Add(tokenTextBox);

        OsqEndpointRowsHost.Children.Add(row);
        _osqEndpointEditors.Add(new OsqEndpointEditorRow(hostTextBox, tokenTextBox, enabled));
    }

    private void ApplyRobotSettings(RobotIntegrationSettings settings)
    {
        RobotOneBotTextBox.Text = settings.OneBotWsUrl;
        RobotAccessTokenTextBox.Text = settings.AccessToken;
        SetNumericValue(RobotReconnectNumericUpDown, settings.ReconnectIntervalSec);
        SetNumericValue(RobotPollIntervalNumericUpDown, settings.PollIntervalSec);
        RobotDatabasePathTextBox.Text = settings.DatabasePath;
        RobotDefaultEncodingTextBox.Text = settings.DefaultEncoding;
        RobotFallbackEncodingTextBox.Text = settings.FallbackEncoding;
        SetNumericValue(RobotOsqPollNumericUpDown, settings.OsqPollIntervalSec);
        SetNumericValue(RobotOsqTimeoutNumericUpDown, settings.OsqRequestTimeoutSec);
        RobotSuperUsersTextBox.Text = settings.SuperUsersText;
    }

    private void SaveFrpSettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.Frp = CollectFrpSettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("内网穿透配置已保存。", "FRP configuration saved."));
        }
    }

    private void SaveOpenServerQuerySettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.OpenServerQuery = CollectOpenServerQuerySettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("开放信息配置已保存。", "Open Info configuration saved."));
        }
    }

    private void SaveRobotSettings(bool updateStatus = true, bool refreshEditor = true)
    {
        var preferences = _preferencesService.Load();
        preferences.Robot = CollectRobotSettings();
        _preferencesService.Save(preferences);
        if (refreshEditor)
        {
            RefreshConnectionSettingsEditor();
        }

        if (updateStatus)
        {
            SetConnectionStatus(T("QQ机器人配置已保存。", "QQ robot configuration saved."));
        }
    }

    private FrpIntegrationSettings CollectFrpSettings()
    {
        var mode = GetSelectedThirdPartyFrpcMode();
        var fallbackThirdPartyCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;

        return new FrpIntegrationSettings
        {
            FrpCommand = string.IsNullOrWhiteSpace(ConnectionFrpCommandTextBox.Text)
                ? FrpIntegrationSettings.DefaultFrpCommand
                : ConnectionFrpCommandTextBox.Text.Trim(),
            ThirdPartyFrpcLaunchMode = mode,
            ThirdPartyFrpcCommand = string.IsNullOrWhiteSpace(ConnectionThirdPartyFrpcCommandTextBox.Text)
                ? fallbackThirdPartyCommand
                : ConnectionThirdPartyFrpcCommandTextBox.Text.Trim()
        };
    }

    private OpenServerQuerySettings CollectOpenServerQuerySettings()
    {
        var endpoints = CollectOpenServerQueryEndpoints();
        var firstEndpoint = endpoints.FirstOrDefault();
        return new OpenServerQuerySettings
        {
            Enabled = OsqEnabledCheckBox.IsChecked == true,
            ListenPrefix = string.IsNullOrWhiteSpace(OsqListenPrefixTextBox.Text)
                ? "http://127.0.0.1:18089/"
                : OsqListenPrefixTextBox.Text.Trim(),
            AllowInsecureHttp = OsqAllowInsecureHttpCheckBox.IsChecked == true,
            RequestTimeoutSec = GetNumericValue(OsqRequestTimeoutNumericUpDown, 8),
            IncludeServerInfo = OsqIncludeServerInfoCheckBox.IsChecked == true,
            IncludePlayers = OsqIncludePlayersCheckBox.IsChecked == true,
            IncludePlayerEvents = OsqIncludeEventsCheckBox.IsChecked == true,
            IncludeChats = OsqIncludeChatsCheckBox.IsChecked == true,
            IncludeNotifications = OsqIncludeNotificationsCheckBox.IsChecked == true,
            IncludeMapData = OsqIncludeMapCheckBox.IsChecked == true,
            Endpoints = endpoints,
            EndpointHost = firstEndpoint?.ServerHost ?? string.Empty,
            EndpointToken = firstEndpoint?.Token ?? string.Empty
        };
    }

    private List<OpenServerQueryEndpointConfig> CollectOpenServerQueryEndpoints()
    {
        var endpoints = new List<OpenServerQueryEndpointConfig>();
        foreach (var row in _osqEndpointEditors)
        {
            var host = row.HostTextBox.Text?.Trim() ?? string.Empty;
            var token = row.TokenTextBox.Text?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            endpoints.Add(new OpenServerQueryEndpointConfig
            {
                ServerHost = host,
                Token = token,
                Enabled = row.Enabled
            });
        }

        return endpoints;
    }

    private RobotIntegrationSettings CollectRobotSettings()
    {
        return new RobotIntegrationSettings
        {
            OneBotWsUrl = string.IsNullOrWhiteSpace(RobotOneBotTextBox.Text)
                ? "ws://127.0.0.1:3001/"
                : RobotOneBotTextBox.Text.Trim(),
            AccessToken = RobotAccessTokenTextBox.Text?.Trim() ?? string.Empty,
            ReconnectIntervalSec = GetNumericValue(RobotReconnectNumericUpDown, 5),
            DatabasePath = RobotDatabasePathTextBox.Text?.Trim() ?? string.Empty,
            PollIntervalSec = GetNumericDoubleValue(RobotPollIntervalNumericUpDown, 1.0),
            DefaultEncoding = string.IsNullOrWhiteSpace(RobotDefaultEncodingTextBox.Text)
                ? "utf-8"
                : RobotDefaultEncodingTextBox.Text.Trim(),
            FallbackEncoding = string.IsNullOrWhiteSpace(RobotFallbackEncodingTextBox.Text)
                ? "gbk"
                : RobotFallbackEncodingTextBox.Text.Trim(),
            SuperUsersText = RobotSuperUsersTextBox.Text?.Trim() ?? string.Empty,
            OsqPollIntervalSec = GetNumericValue(RobotOsqPollNumericUpDown, 20),
            OsqRequestTimeoutSec = GetNumericValue(RobotOsqTimeoutNumericUpDown, 8)
        };
    }

    private static OpenServerQueryRuntimeSettings ToOpenServerQueryRuntimeSettings(OpenServerQuerySettings settings)
    {
        var endpoints = new List<OpenServerQueryEndpointSettings>();
        foreach (var endpoint in settings.Endpoints ?? [])
        {
            var host = endpoint.ServerHost?.Trim() ?? string.Empty;
            var token = endpoint.Token?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host) || string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            endpoints.Add(new OpenServerQueryEndpointSettings
            {
                ServerHost = host,
                Token = token,
                Enabled = endpoint.Enabled
            });
        }

        if (endpoints.Count == 0 &&
            !string.IsNullOrWhiteSpace(settings.EndpointHost) &&
            !string.IsNullOrWhiteSpace(settings.EndpointToken))
        {
            endpoints.Add(new OpenServerQueryEndpointSettings
            {
                ServerHost = settings.EndpointHost.Trim(),
                Token = settings.EndpointToken.Trim(),
                Enabled = true
            });
        }

        return new OpenServerQueryRuntimeSettings
        {
            Enabled = settings.Enabled,
            ListenPrefix = settings.ListenPrefix,
            AllowInsecureHttp = settings.AllowInsecureHttp,
            RequestTimeoutSec = settings.RequestTimeoutSec,
            IncludeServerInfo = settings.IncludeServerInfo,
            IncludePlayers = settings.IncludePlayers,
            IncludePlayerEvents = settings.IncludePlayerEvents,
            IncludeChats = settings.IncludeChats,
            IncludeNotifications = settings.IncludeNotifications,
            IncludeMapData = settings.IncludeMapData,
            Endpoints = endpoints
        };
    }

    private static RobotSettings ToRobotSettings(RobotIntegrationSettings settings, OpenServerQuerySettings osqSettings)
    {
        return new RobotSettings
        {
            OneBotWsUrl = settings.OneBotWsUrl,
            AccessToken = settings.AccessToken,
            ReconnectIntervalSec = settings.ReconnectIntervalSec,
            DatabasePath = settings.DatabasePath,
            PollIntervalSec = settings.PollIntervalSec,
            DefaultEncoding = settings.DefaultEncoding,
            FallbackEncoding = settings.FallbackEncoding,
            SuperUsers = ParseQqIds(settings.SuperUsersText),
            OsqPollIntervalSec = settings.OsqPollIntervalSec,
            OsqRequestTimeoutSec = settings.OsqRequestTimeoutSec,
            OsqAllowInsecureHttp = osqSettings.AllowInsecureHttp,
            OsqListenPrefix = osqSettings.ListenPrefix,
            EnableOsqListener = false
        };
    }

    private static IReadOnlyList<long> ParseQqIds(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return [];
        }

        return value
            .Split([',', ';', '，', '；', '\r', '\n', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => long.TryParse(item.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : 0)
            .Where(id => id > 0)
            .Distinct()
            .ToList();
    }

    private void RebuildThirdPartyFrpcModeOptions()
    {
        var selectedValue = (ConnectionThirdPartyFrpcModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        _thirdPartyFrpcModeOptions.Clear();
        _thirdPartyFrpcModeOptions.Add(new ConfigChoiceOption(
            ThirdPartyFrpcLaunchMode.ConfigFile.ToString(),
            T("配置文件", "Config File")));
        _thirdPartyFrpcModeOptions.Add(new ConfigChoiceOption(
            ThirdPartyFrpcLaunchMode.CommandOnly.ToString(),
            T("纯命令", "Command Only")));
        SelectConfigChoiceByValue(
            ConnectionThirdPartyFrpcModeComboBox,
            _thirdPartyFrpcModeOptions,
            selectedValue ?? ThirdPartyFrpcLaunchMode.ConfigFile.ToString());
    }

    private ThirdPartyFrpcLaunchMode GetSelectedThirdPartyFrpcMode()
    {
        var value = (ConnectionThirdPartyFrpcModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        return Enum.TryParse<ThirdPartyFrpcLaunchMode>(value, ignoreCase: true, out var mode)
            ? mode
            : ThirdPartyFrpcLaunchMode.ConfigFile;
    }

    private void UpdateConnectionFrpActionButtons()
    {
        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _isFrpRunning = frpStatus.IsRunning;
        _isThirdPartyFrpcRunning = thirdPartyStatus.IsRunning;

        ConnectionFrpToggleButton.Content = frpStatus.IsRunning
            ? T("停止常规", "Stop Regular")
            : T("启动常规", "Start Regular");
        ConnectionThirdPartyFrpcToggleButton.Content = thirdPartyStatus.IsRunning
            ? T("停止第三方", "Stop Third-party")
            : T("启动第三方", "Start Third-party");
    }

    private void UpdateOsqToggleButtonText()
    {
        var isRunning = _openServerQueryService.GetRuntimeStatus().IsListening;
        OsqToggleButton.Content = isRunning ? T("停止", "Stop") : T("启动", "Start");
    }

    private void UpdateRobotToggleButtonText()
    {
        var isRunning = _robotService.GetCurrentStatus().IsRunning;
        RobotToggleButton.Content = isRunning ? T("停止", "Stop") : T("启动", "Start");
    }

    private void RefreshConnectionRuntimeStatus()
    {
        UpdateConnectionFrpActionButtons();
        UpdateOsqToggleButtonText();
        UpdateRobotToggleButtonText();
        var currentStatus = _selectedConnectionTab switch
        {
            ConnectionTab.Frp => BuildFrpRuntimeStatusText(),
            ConnectionTab.OpenInfo => BuildOpenInfoRuntimeStatusText(),
            ConnectionTab.Robot => BuildRobotRuntimeStatusText(),
            ConnectionTab.Auth => AuthStatusTextBlock.Text ?? string.Empty,
            _ => string.Empty
        };

        SetConnectionStatus(currentStatus);
        UpdateCardValues(_serverProcessService.GetCurrentStatus());
    }

    private string BuildFrpRuntimeStatusText()
    {
        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _isFrpRunning = frpStatus.IsRunning;
        _isThirdPartyFrpcRunning = thirdPartyStatus.IsRunning;

        var regular = frpStatus.IsRunning
            ? T(
                $"常规内网穿透：运行中 PID={frpStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(frpStatus.StartedAtUtc)}",
                $"Regular FRP: running PID={frpStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(frpStatus.StartedAtUtc)}")
            : T("常规内网穿透：未启动", "Regular FRP: stopped");
        var thirdParty = thirdPartyStatus.IsRunning
            ? T(
                $"第三方内网穿透：运行中 PID={thirdPartyStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(thirdPartyStatus.StartedAtUtc)}",
                $"Third-party FRPC: running PID={thirdPartyStatus.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(thirdPartyStatus.StartedAtUtc)}")
            : T("第三方内网穿透：未启动", "Third-party FRPC: stopped");
        return $"{regular}；{thirdParty}";
    }

    private string BuildOpenInfoRuntimeStatusText()
    {
        var status = _openServerQueryService.GetRuntimeStatus();
        if (!status.IsListening)
        {
            return T("开放信息：未启动", "Open Info: stopped");
        }

        var lastReceived = string.IsNullOrWhiteSpace(status.LastReceivedUtc) ? "--" : status.LastReceivedUtc;
        return T(
            $"开放信息：运行中 {status.ListenPrefix}  接收 {status.AcceptedRequests}/{status.TotalRequests}  最近 {lastReceived}",
            $"Open Info: running {status.ListenPrefix}  accepted {status.AcceptedRequests}/{status.TotalRequests}  last {lastReceived}");
    }

    private string BuildRobotRuntimeStatusText()
    {
        var status = _robotService.GetCurrentStatus();
        if (!status.IsRunning)
        {
            return T("QQ机器人：未启动", "QQ robot: stopped");
        }

        return T(
            $"QQ机器人：运行中 PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(status.StartedAtUtc)}",
            $"QQ robot: running PID={status.ProcessId?.ToString(CultureInfo.InvariantCulture) ?? "--"}  {FormatConnectionUptime(status.StartedAtUtc)}");
    }

    private static string FormatConnectionUptime(DateTimeOffset? startedAtUtc)
    {
        return startedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - startedAtUtc.Value)
            : "--";
    }

    private void SetConnectionStatus(string message)
    {
        ConnectionStatusTextBlock.Text = message;
    }

    private async Task ImportConnectionExecutableAsync(ConnectionProcessKind kind)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = kind == ConnectionProcessKind.Frp
                ? T("导入frpc可执行文件", "Import frpc executable")
                : T("导入第三方frpc可执行文件", "Import third-party frpc executable"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Executable")
                {
                    Patterns = ["*.exe"]
                }
            ]
        });

        var sourcePath = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.ImportExecutableAsync(sourcePath);
            }
            else
            {
                await _thirdPartyFrpcService.ImportExecutableAsync(sourcePath);
            }

            if (kind == ConnectionProcessKind.Frp)
            {
                if (string.IsNullOrWhiteSpace(ConnectionFrpCommandTextBox.Text))
                {
                    ConnectionFrpCommandTextBox.Text = FrpIntegrationSettings.DefaultFrpCommand;
                }
            }
            else
            {
                var mode = GetSelectedThirdPartyFrpcMode();
                var defaultCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
                    ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
                    : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;
                if (string.IsNullOrWhiteSpace(ConnectionThirdPartyFrpcCommandTextBox.Text))
                {
                    ConnectionThirdPartyFrpcCommandTextBox.Text = defaultCommand;
                }
            }

            SaveFrpSettings(updateStatus: false, refreshEditor: false);
            SetConnectionStatus(T($"已导入：{Path.GetFileName(sourcePath)}", $"Imported: {Path.GetFileName(sourcePath)}"));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async Task ToggleConnectionProcessAsync(ConnectionProcessKind kind)
    {
        if (kind == ConnectionProcessKind.Frp ? _frpService.GetCurrentStatus().IsRunning : _thirdPartyFrpcService.GetCurrentStatus().IsRunning)
        {
            await StopConnectionProcessAsync(kind);
            return;
        }

        SaveFrpSettings(updateStatus: false, refreshEditor: false);
        await StartConnectionProcessAsync(kind);
    }

    private async Task EditConnectionTomlAsync(ConnectionProcessKind kind)
    {
        try
        {
            string tomlPath;
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.LoadConfigAsync();
                tomlPath = _frpService.ConfigPath;
            }
            else
            {
                await _thirdPartyFrpcService.LoadConfigAsync();
                tomlPath = _thirdPartyFrpcService.ConfigPath;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = tomlPath,
                UseShellExecute = true
            });

            var serviceName = kind == ConnectionProcessKind.Frp
                ? T("常规内网穿透", "Regular FRP")
                : T("第三方内网穿透", "Third-party FRPC");
            SetConnectionStatus(T($"已打开 {serviceName} 的 TOML 配置。", $"Opened TOML config for {serviceName}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"打开 TOML 配置失败：{ex.Message}", $"Failed to open TOML config: {ex.Message}"));
        }
    }

    private async Task StartConnectionProcessAsync(ConnectionProcessKind kind)
    {
        var isRunning = kind == ConnectionProcessKind.Frp
            ? _frpService.GetCurrentStatus().IsRunning
            : _thirdPartyFrpcService.GetCurrentStatus().IsRunning;
        if (isRunning)
        {
            SetConnectionStatus(kind == ConnectionProcessKind.Frp
                ? T("常规内网穿透已在运行。", "Regular FRP is already running.")
                : T("第三方内网穿透已在运行。", "Third-party FRPC is already running."));
            return;
        }

        var serviceName = kind == ConnectionProcessKind.Frp
            ? T("常规内网穿透", "Regular FRP")
            : T("第三方内网穿透", "Third-party FRPC");

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.StartAsync();
            }
            else
            {
                await _thirdPartyFrpcService.StartAsync();
            }

            var status = kind == ConnectionProcessKind.Frp
                ? _frpService.GetCurrentStatus()
                : _thirdPartyFrpcService.GetCurrentStatus();
            SetConnectionStatus(T($"{serviceName} 已启动，PID={status.ProcessId}。", $"{serviceName} started, PID={status.ProcessId}."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"{serviceName} 启动失败：{ex.Message}", $"{serviceName} start failed: {ex.Message}"));
        }
    }

    private async Task StopConnectionProcessAsync(ConnectionProcessKind kind)
    {
        var serviceName = kind == ConnectionProcessKind.Frp
            ? T("常规内网穿透", "Regular FRP")
            : T("第三方内网穿透", "Third-party FRPC");

        try
        {
            if (kind == ConnectionProcessKind.Frp)
            {
                await _frpService.StopAsync(TimeSpan.FromSeconds(15));
            }
            else
            {
                await _thirdPartyFrpcService.StopAsync(TimeSpan.FromSeconds(15));
            }

            SetConnectionStatus(T($"{serviceName} 已停止。", $"{serviceName} stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"{serviceName} 停止失败：{ex.Message}", $"{serviceName} stop failed: {ex.Message}"));
        }
    }

    private void RefreshAppearanceSettingsEditor()
    {
        _isApplyingAppearanceSettings = true;
        try
        {
            var preferences = _preferencesService.Load();
            SettingsLanguageLabelTextBlock.Text = T("语言", "Language");
            SettingsThemeLabelTextBlock.Text = T("主题", "Theme");

            SettingsLanguageComboBox.ItemsSource = AppearanceLanguageOptions
                .Select(option => _isChinese ? option.Zh : option.En)
                .ToList();
            SettingsThemeComboBox.ItemsSource = AppearanceThemeOptions
                .Select(option => _isChinese ? option.Zh : option.En)
                .ToList();

            SettingsLanguageComboBox.SelectedIndex =
                preferences.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? 0 : 1;

            var themeIndex = Array.FindIndex(AppearanceThemeOptions, option => option.Mode == preferences.ThemeMode);
            SettingsThemeComboBox.SelectedIndex = themeIndex >= 0 ? themeIndex : 0;
        }
        finally
        {
            _isApplyingAppearanceSettings = false;
        }
    }

    private void OnSettingsLanguageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingAppearanceSettings)
        {
            return;
        }

        var index = SettingsLanguageComboBox.SelectedIndex;
        if (index < 0 || index >= AppearanceLanguageOptions.Length)
        {
            return;
        }

        var languageCode = AppearanceLanguageOptions[index].Code;
        var preferences = _preferencesService.Load();
        preferences.Language = languageCode;
        _preferencesService.Save(preferences);

        ApplyCulture(languageCode);
        try
        {
            ServiceLocator.GetRequiredService<ILocalizationService>().CurrentCulture = CultureInfo.GetCultureInfo(languageCode);
        }
        catch
        {
            // ignore localization service failures
        }

        _isChinese = languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _aboutMarkdownLoaded = false;
        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
        if (_selectedSettingsTab == SettingsTab.About)
        {
            LoadAboutMarkdown();
        }
    }

    private void OnSettingsThemeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingAppearanceSettings)
        {
            return;
        }

        var index = SettingsThemeComboBox.SelectedIndex;
        if (index < 0 || index >= AppearanceThemeOptions.Length)
        {
            return;
        }

        var mode = AppearanceThemeOptions[index].Mode;
        var preferences = _preferencesService.Load();
        preferences.ThemeMode = mode;
        _preferencesService.Save(preferences);

        ApplyTheme(mode);
        RefreshAppearanceSettingsEditor();
    }

    private static void ApplyTheme(ThemeMode mode)
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    private static void ApplyCulture(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
            // ignore invalid culture code
        }
    }

    private static void SetSelectedClass(StyledElement element, bool selected)
    {
        element.Classes.Set("selected", selected);
    }

    private void OnServerOutputReceived(object? sender, string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsSystemConsoleLine(line))
            {
                return;
            }

            AppendConsoleLine(line);
            TrackPlayerEventText(line);
        });
    }

    private void OnServerStatusChanged(object? sender, ServerRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCardValues(status);
            _ = HandleServerLogTailAsync(status);
            if (_selectedTab == MainTab.Monitor)
            {
                RenderSelectedMetricChart(status);
            }
        });
    }

    private void OnOpenServerQueryOutputReceived(object? sender, string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.OpenInfo)
            {
                SetConnectionStatus(line);
            }
        });
    }

    private async Task HandleServerLogTailAsync(ServerRuntimeStatus status)
    {
        try
        {
            if (!status.IsRunning || string.IsNullOrWhiteSpace(status.ProfileId))
            {
                if (!string.IsNullOrWhiteSpace(_tailedProfileId))
                {
                    _tailedProfileId = string.Empty;
                    await _logTailService.StopAsync();
                }

                return;
            }

            if (_tailedProfileId.Equals(status.ProfileId, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var profile = _profileService.GetProfileById(status.ProfileId);
            if (profile is null)
            {
                return;
            }

            _tailedProfileId = profile.Id;
            await _logTailService.StartAsync(profile);
        }
        catch
        {
            // 日志跟随失败不影响主流程。
        }
    }

    private void OnLogTailLineReceived(object? sender, string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (IsSystemConsoleLine(line))
            {
                return;
            }

            AppendConsoleLine($"[log] {line}");
            TrackPlayerEventText(line);
        });
    }

    private void OnAutomationRuntimeLogReceived(object? sender, string line)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _automationRuntimeLogItems.Add(line);
            while (_automationRuntimeLogItems.Count > 1500)
            {
                _automationRuntimeLogItems.RemoveAt(0);
            }

            if (_selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Automation)
            {
                if (_automationRuntimeLogItems.LastOrDefault() is { } lastLog)
                {
                    AutomationRuntimeLogsListBox.ScrollIntoView(lastLog);
                }
            }
        });
    }

    private void OnFrpStatusChanged(object? sender, FrpRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isFrpRunning = status.IsRunning;
            UpdateConnectionFrpActionButtons();
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp)
            {
                RefreshConnectionRuntimeStatus();
            }
        });
    }

    private void OnThirdPartyFrpcStatusChanged(object? sender, FrpRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _isThirdPartyFrpcRunning = status.IsRunning;
            UpdateConnectionFrpActionButtons();
            if (_selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp)
            {
                RefreshConnectionRuntimeStatus();
            }
        });
    }

    private void AppendConsoleLine(string line)
    {
        if (IsSystemConsoleLine(line))
        {
            return;
        }

        _consoleLines.Add(line);
        while (_consoleLines.Count > 500)
        {
            _consoleLines.RemoveAt(0);
        }

        var text = string.Join(Environment.NewLine, _consoleLines);
        ConsoleOutputTextBlock.Text = text;
        ConsoleOutputScrollViewer.ScrollToEnd();
    }

    private static bool IsSystemConsoleLine(string? line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               line.TrimStart().StartsWith("[system]", StringComparison.OrdinalIgnoreCase);
    }

    private void TrackPlayerEventText(string line)
    {
        if (!PlayerEventHintRegex().IsMatch(line))
        {
            return;
        }

        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        _playerEvents.Insert(0, text);
        if (_playerEvents.Count > 24)
        {
            _playerEvents.RemoveAt(_playerEvents.Count - 1);
        }

        EventTickerCurrentText.Text = _playerEvents[0];
        EventTickerNextText.Text = _playerEvents.Count > 1 ? _playerEvents[1] : _playerEvents[0];
    }

    private void OnWindowPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            return;
        }

        DeactivateInputControlsOnBackgroundClick(e.Source);

        if (ShouldSkipWindowDrag(e.Source))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private void DeactivateInputControlsOnBackgroundClick(object? source)
    {
        if (ShouldKeepInputFocus(source))
        {
            return;
        }

        var closedDropDown = CloseOpenComboBoxDropDowns();
        var focusedElement = FocusManager?.GetFocusedElement();
        if (focusedElement is TextBox or ComboBox or ComboBoxItem or NumericUpDown || closedDropDown)
        {
            FocusManager?.Focus(null!, NavigationMethod.Pointer, KeyModifiers.None);
        }
    }

    private bool CloseOpenComboBoxDropDowns()
    {
        var closedAny = false;
        foreach (var comboBox in this.GetVisualDescendants().OfType<ComboBox>())
        {
            if (!comboBox.IsDropDownOpen)
            {
                continue;
            }

            comboBox.IsDropDownOpen = false;
            closedAny = true;
        }

        return closedAny;
    }

    private static bool ShouldKeepInputFocus(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is TextBox
                or ComboBox
                or ComboBoxItem
                or NumericUpDown)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private static bool ShouldSkipWindowDrag(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is Button
                or ToggleSwitch
                or CheckBox
                or ComboBox
                or ComboBoxItem
                or TextBox
                or SelectableTextBlock
                or ListBox
                or ListBoxItem
                or ScrollViewer
                or ScrollBar
                or Thumb)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
    }

    private async void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ChartSummaryText.Text = T("无法打开链接。", "Unable to open the link.");
            });
        }
    }

    private string T(string zh, string en) => _isChinese ? zh : en;

    private void OnRepositoryClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/TGU-HansJack/LauncherGo");

    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/TGU-HansJack/LauncherGo/issues");

    private void OnSponsorClick(object? sender, RoutedEventArgs e) => OpenUrl("https://afdian.com/a/hansjack");

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHomeNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Home);

    private void OnMonitorNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Monitor);

    private void OnConsoleNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Console);

    private void OnInstanceManageNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.InstanceManage);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Settings);

    private void OnConnectionNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Connection);

    private void OnServerStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Server);

    private void OnRobotStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Robot);

    private void OnOnlinePlayersCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Players);

    private void OnNetworkStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Network);

    private void OnProfilesSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Profiles);

    private void OnConfigSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Config);

    private void OnSavesSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Saves);

    private void OnAutomationSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Automation);

    private void OnModsSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Mods);

    private void OnDownloadVersionsSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.DownloadVersions);

    private void OnServerSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Server);

    private void OnAppearanceSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Appearance);

    private void OnNetworkSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Network);

    private void OnAdvancedSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Advanced);

    private void OnAboutSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.About);

    private void OnSponsorsSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Sponsors);

    private void OnContributorsSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Contributors);

    private void OnSettingsServerSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings();
    }

    private void OnSettingsServerRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshServerSettingsEditor();
    }

    private async void OnSettingsBrowseWorkspaceDirectoryClick(object? sender, RoutedEventArgs e)
    {
        await BrowseFolderToTextBoxAsync(SettingsWorkspaceDirectoryTextBox, T("选择工作目录", "Select workspace directory"));
    }

    private async void OnSettingsOpenLogClick(object? sender, RoutedEventArgs e)
    {
        await OpenAppLogsAsync();
    }

    private async void OnSettingsClearDownloadCacheClick(object? sender, RoutedEventArgs e)
    {
        await ClearDownloadCacheAsync();
    }

    private void OnSettingsResetAllClick(object? sender, RoutedEventArgs e)
    {
        ResetAllSettingsAndRestartToGuide();
    }

    private void OnContributorOpenClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && !string.IsNullOrWhiteSpace(url))
        {
            OpenUrl(url);
        }
    }

    private void OnConnectionFrpTabClick(object? sender, RoutedEventArgs e) => SelectConnectionTab(ConnectionTab.Frp);

    private void OnConnectionOpenInfoTabClick(object? sender, RoutedEventArgs e) => SelectConnectionTab(ConnectionTab.OpenInfo);

    private void OnConnectionRobotTabClick(object? sender, RoutedEventArgs e) => SelectConnectionTab(ConnectionTab.Robot);

    private void OnConnectionAuthTabClick(object? sender, RoutedEventArgs e) => SelectConnectionTab(ConnectionTab.Auth);

    private async void OnConnectionFrpImportClick(object? sender, RoutedEventArgs e)
    {
        await ImportConnectionExecutableAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcImportClick(object? sender, RoutedEventArgs e)
    {
        await ImportConnectionExecutableAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private void OnConnectionRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
        if (_selectedConnectionTab == ConnectionTab.Auth)
        {
            _ = RefreshAuthProfilesAsync();
        }
    }

    private void OnConnectionFrpSaveClick(object? sender, RoutedEventArgs e) => SaveFrpSettings();

    private async void OnConnectionFrpToggleClick(object? sender, RoutedEventArgs e)
    {
        await ToggleConnectionProcessAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcToggleClick(object? sender, RoutedEventArgs e)
    {
        await ToggleConnectionProcessAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private async void OnConnectionFrpEditTomlClick(object? sender, RoutedEventArgs e)
    {
        await EditConnectionTomlAsync(ConnectionProcessKind.Frp);
    }

    private async void OnConnectionThirdPartyFrpcEditTomlClick(object? sender, RoutedEventArgs e)
    {
        await EditConnectionTomlAsync(ConnectionProcessKind.ThirdPartyFrpc);
    }

    private async void OnAutomationProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingAutomation)
        {
            return;
        }

        var selected = AutomationProfileComboBox.SelectedItem as InstanceProfile;
        if (selected is null)
        {
            return;
        }

        try
        {
            var settings = await _automationSettingsService.LoadAsync();
            if (string.IsNullOrWhiteSpace(settings.TargetProfileId))
            {
                settings.TargetProfileId = selected.Id;
                await _automationSettingsService.SaveAsync(settings);
            }
        }
        catch
        {
            // ignore
        }
    }

    private async void OnAutomationRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAutomationAsync();
    }

    private async void OnAutomationSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveAutomationAsync();
    }

    private void OnAutomationAddActionClick(object? sender, RoutedEventArgs e)
    {
        _automationActionWindowItems.Add(new AutomationActionWindowItem());
    }

    private void OnAutomationRemoveActionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationActionWindowItem item })
        {
            _automationActionWindowItems.Remove(item);
        }
    }

    private void OnAutomationAddBackupTimeClick(object? sender, RoutedEventArgs e)
    {
        _automationBackupTimeItems.Add(new AutomationTimeItem("03:00"));
    }

    private void OnAutomationRemoveBackupTimeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationTimeItem item })
        {
            _automationBackupTimeItems.Remove(item);
        }
    }

    private void OnAutomationAddBroadcastClick(object? sender, RoutedEventArgs e)
    {
        _automationBroadcastItems.Add(new ScheduledBroadcastItem());
    }

    private void OnAutomationRemoveBroadcastClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledBroadcastItem item })
        {
            _automationBroadcastItems.Remove(item);
        }
    }

    private void OnAutomationAddExportTimeClick(object? sender, RoutedEventArgs e)
    {
        _automationExportTimeItems.Add(new AutomationTimeItem("12:00"));
    }

    private void OnAutomationRemoveExportTimeClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: AutomationTimeItem item })
        {
            _automationExportTimeItems.Remove(item);
        }
    }

    private async void OnModProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingMods)
        {
            return;
        }

        await LoadModsForSelectedProfileAsync();
    }

    private async void OnBrowseModZipClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择 Mod ZIP 文件", "Select Mod ZIP file"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (!string.IsNullOrWhiteSpace(path))
        {
            ModZipPathTextBox.Text = path;
        }
    }

    private async void OnImportModZipClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            ModStatusTextBlock.Text = T("请先选择档案。", "Select a profile first.");
            return;
        }

        var path = ModZipPathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            ModStatusTextBlock.Text = T("请输入 Mod ZIP 路径。", "Enter a Mod ZIP path.");
            return;
        }

        try
        {
            var imported = await _instanceModService.ImportModZipAsync(profile, path);
            await LoadModsForSelectedProfileAsync();
            ModStatusTextBlock.Text = T($"已导入：{imported.ModId}", $"Imported: {imported.ModId}");
        }
        catch (Exception ex)
        {
            ModStatusTextBlock.Text = T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}");
        }
    }

    private async void OnDeleteSelectedModsClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            ModStatusTextBlock.Text = T("请先选择档案。", "Select a profile first.");
            return;
        }

        var selected = ModsListBox.SelectedItems?
            .OfType<ModListItem>()
            .Select(ModListItem.ToModel)
            .ToList() ?? [];
        if (selected.Count == 0)
        {
            ModStatusTextBlock.Text = T("请先选择模组。", "Select mods first.");
            return;
        }

        try
        {
            var deleted = await _instanceModService.DeleteModsAsync(profile, selected);
            await LoadModsForSelectedProfileAsync();
            ModStatusTextBlock.Text = T($"已删除 {deleted} 个模组。", $"Deleted {deleted} mods.");
        }
        catch (Exception ex)
        {
            ModStatusTextBlock.Text = T($"删除失败：{ex.Message}", $"Delete failed: {ex.Message}");
        }
    }

    private async void OnRefreshModsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshModsAsync();
    }

    private async void OnModEnabledSwitchClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch { Tag: ModListItem item } toggleSwitch ||
            ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        try
        {
            await _instanceModService.SetModEnabledAsync(profile, item.ModId, item.Version, toggleSwitch.IsChecked == true);
            await LoadModsForSelectedProfileAsync();
        }
        catch (Exception ex)
        {
            ModStatusTextBlock.Text = T($"切换失败：{ex.Message}", $"Toggle failed: {ex.Message}");
        }
    }

    private async void OnAuthProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingAuth)
        {
            return;
        }

        if (AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthForProfileAsync(profile);
        }
    }

    private async void OnAuthSaveClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AuthStatusTextBlock.Text = T("请先选择档案。", "Select a profile first.");
            return;
        }

        try
        {
            await _serverAuthService.SaveSettingsAsync(profile, CollectAuthSettings());
            await LoadAuthForProfileAsync(profile);
            AuthStatusTextBlock.Text = T("认证配置已保存。", "Auth settings saved.");
        }
        catch (Exception ex)
        {
            AuthStatusTextBlock.Text = T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}");
        }
    }

    private async void OnAuthRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshAuthProfilesAsync();
    }

    private async void OnAuthDeployClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AuthStatusTextBlock.Text = T("请先选择档案。", "Select a profile first.");
            return;
        }

        try
        {
            await _serverAuthService.EnsureAuthModDeployedAsync(profile);
            await LoadAuthForProfileAsync(profile);
            AuthStatusTextBlock.Text = T("认证模组已部署。", "Auth mod deployed.");
        }
        catch (Exception ex)
        {
            AuthStatusTextBlock.Text = T($"部署失败：{ex.Message}", $"Deploy failed: {ex.Message}");
        }
    }

    private async void OnAuthRefreshPlayersClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthPlayersAsync(profile);
        }
    }

    private async void OnAuthClearPlayerPasswordClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: AuthPlayerListItem item } || AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        try
        {
            var changed = await _serverAuthService.ClearPasswordAsync(profile, item.PlayerUid);
            await LoadAuthPlayersAsync(profile);
            AuthStatusTextBlock.Text = changed
                ? T($"已清空 {item.PlayerName} 的密码。", $"Cleared password for {item.PlayerName}.")
                : T($"未找到玩家：{item.PlayerName}", $"Player not found: {item.PlayerName}");
        }
        catch (Exception ex)
        {
            AuthStatusTextBlock.Text = T($"清空失败：{ex.Message}", $"Clear failed: {ex.Message}");
        }
    }

    private void OnConnectionThirdPartyFrpcModeSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        var mode = GetSelectedThirdPartyFrpcMode();
        var defaultCommand = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;
        var current = ConnectionThirdPartyFrpcCommandTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(current) ||
            current.Equals(FrpIntegrationSettings.DefaultThirdPartyFrpcCommand, StringComparison.OrdinalIgnoreCase) ||
            current.Equals(FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand, StringComparison.OrdinalIgnoreCase))
        {
            ConnectionThirdPartyFrpcCommandTextBox.Text = defaultCommand;
        }

        SaveFrpSettings(updateStatus: false, refreshEditor: false);
    }

    private void OnOsqSaveClick(object? sender, RoutedEventArgs e) => SaveOpenServerQuerySettings();

    private void OnOsqEndpointAddClick(object? sender, RoutedEventArgs e)
    {
        AddOsqEndpointEditorRow(string.Empty, string.Empty);
        _osqEndpointEditors[^1].HostTextBox.Focus();
    }

    private async void OnOsqToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_openServerQueryService.GetRuntimeStatus().IsListening)
        {
            await StopOpenInfoAsync();
            return;
        }

        await StartOpenInfoAsync();
    }

    private async Task StartOpenInfoAsync()
    {
        SaveOpenServerQuerySettings(updateStatus: false);
        try
        {
            var settings = _preferencesService.Load().OpenServerQuery;
            if (!settings.Enabled)
            {
                SetConnectionStatus(T("开放信息未启用。", "Open Info is disabled."));
                return;
            }

            await _openServerQueryService.StartAsync(ToOpenServerQueryRuntimeSettings(settings));
            SetConnectionStatus(BuildOpenInfoRuntimeStatusText());
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"开放信息启动失败：{ex.Message}", $"Open Info start failed: {ex.Message}"));
        }
        finally
        {
            UpdateOsqToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCurrentStatus());
        }
    }

    private async Task StopOpenInfoAsync()
    {
        try
        {
            await _openServerQueryService.StopAsync(TimeSpan.FromSeconds(5));
            SetConnectionStatus(T("开放信息已停止。", "Open Info stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"开放信息停止失败：{ex.Message}", $"Open Info stop failed: {ex.Message}"));
        }
        finally
        {
            UpdateOsqToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCurrentStatus());
        }
    }

    private void OnRobotSaveClick(object? sender, RoutedEventArgs e) => SaveRobotSettings();

    private async void OnRobotToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_robotService.GetCurrentStatus().IsRunning)
        {
            await StopRobotAsync();
            return;
        }

        await StartRobotAsync();
    }

    private async Task StartRobotAsync()
    {
        SaveRobotSettings(updateStatus: false);
        try
        {
            var preferences = _preferencesService.Load();
            await _robotService.StartAsync(ToRobotSettings(preferences.Robot, preferences.OpenServerQuery));
            SetConnectionStatus(BuildRobotRuntimeStatusText());
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人启动失败：{ex.Message}", $"QQ robot start failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCurrentStatus());
        }
    }

    private async Task StopRobotAsync()
    {
        try
        {
            await _robotService.StopAsync(TimeSpan.FromSeconds(5));
            SetConnectionStatus(T("QQ机器人已停止。", "QQ robot stopped."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人停止失败：{ex.Message}", $"QQ robot stop failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCurrentStatus());
        }
    }

    private async void OnLaunchServerClick(object? sender, RoutedEventArgs e)
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var status = _serverProcessService.GetCurrentStatus();
        if (!status.IsRunning)
        {
            await StartLockedServerAsync();
            return;
        }

        await StopServerFromLaunchButtonAsync();
    }

    private void OnLaunchServerPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_serverProcessService.GetCurrentStatus().IsRunning || TryGetLockedLaunchTarget(out _, out _))
        {
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        LaunchSelectionPillHost.Classes.Set("expanded", true);
    }

    private void OnLaunchServerPointerExited(object? sender, PointerEventArgs e)
    {
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private async Task StopServerFromLaunchButtonAsync()
    {
        _isStoppingOrStarting = true;
        LaunchServerButton.IsEnabled = false;
        try
        {
            AppendConsoleLine("[system] 正在停止服务器...");
            await _serverProcessService.StopAsync(TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 启动/停止失败：{ex.Message}");
        }
        finally
        {
            LaunchServerButton.IsEnabled = true;
            _isStoppingOrStarting = false;
            RefreshLaunchButtonSummary();
        }
    }

    private async Task StartLockedServerAsync()
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        if (!TryGetLockedLaunchTarget(out var profile, out var lockedSavePath))
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            LaunchSelectionSummaryTextBlock.Text = T("请先锁定默认存档", "Set default save first");
            return;
        }

        var saves = await _saveService.GetSavesAsync(profile);
        var lockedSave = saves.FirstOrDefault(save =>
            NormalizeFullPath(save.FullPath).Equals(lockedSavePath, StringComparison.OrdinalIgnoreCase));
        if (lockedSave is null)
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            LaunchSelectionSummaryTextBlock.Text = T("请先锁定默认存档", "Set default save first");
            return;
        }

        var normalizedLockedSavePath = NormalizeFullPath(lockedSave.FullPath);
        if (!string.IsNullOrWhiteSpace(normalizedLockedSavePath) && File.Exists(normalizedLockedSavePath))
        {
            var fileInfo = new FileInfo(normalizedLockedSavePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedLockedSavePath);
            }
        }

        _isStoppingOrStarting = true;
        LaunchServerButton.IsEnabled = false;
        try
        {
            profile.ActiveSaveFile = lockedSave.FullPath;
            _profileService.UpdateProfile(profile);
            SelectTab(MainTab.Console);
            await _serverProcessService.StartAsync(profile);
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 启动/停止失败：{ex.Message}");
        }
        finally
        {
            LaunchServerButton.IsEnabled = true;
            _isStoppingOrStarting = false;
            RefreshLaunchButtonSummary();
        }
    }

    private async void OnSendCommandClick(object? sender, RoutedEventArgs e)
    {
        await SendCommandFromInputAsync();
    }

    private async void OnCommandTextBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter)
        {
            return;
        }

        e.Handled = true;
        await SendCommandFromInputAsync();
    }

    private void OnQuickCommandSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (QuickCommandComboBox.SelectedItem is not string command || string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        CommandTextBox.Text = command;
        CommandTextBox.CaretIndex = command.Length;
        CommandTextBox.Focus();
        QuickCommandComboBox.SelectedIndex = -1;
    }

    private async Task SendCommandFromInputAsync()
    {
        var command = CommandTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        CommandTextBox.Text = string.Empty;
        await SendCommandAsync(command);
    }

    private async Task SendCommandAsync(string command)
    {
        try
        {
            await _serverProcessService.SendCommandAsync(command);
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 命令发送失败：{ex.Message}");
        }
    }

    private async Task RefreshConfigProfilesAsync()
    {
        if (_isRefreshingConfigProfiles)
        {
            return;
        }

        InstanceProfile? targetProfile = null;
        _isRefreshingConfigProfiles = true;
        try
        {
            var selectedProfileId = (ConfigProfileComboBox.SelectedItem as InstanceProfile)?.Id;
            var profiles = _profileService.GetProfiles();
            ConfigProfileComboBox.ItemsSource = profiles;
            targetProfile = profiles.FirstOrDefault(profile =>
                                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                            ?? profiles.FirstOrDefault();
            ConfigProfileComboBox.SelectedItem = targetProfile;
            SetConfigHasProfiles(profiles.Count > 0);
        }
        finally
        {
            _isRefreshingConfigProfiles = false;
        }

        if (targetProfile is null)
        {
            ClearConfigForm();
            SetConfigStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."));
            return;
        }

        await LoadConfigForProfileAsync(targetProfile);
    }

    private void SetConfigHasProfiles(bool hasProfiles)
    {
        ConfigScrollViewer.IsVisible = hasProfiles;
        ConfigEmptyPanel.IsVisible = !hasProfiles;
        ConfigRefreshButton.IsEnabled = true;
        ConfigImportButton.IsEnabled = hasProfiles;
        ConfigSaveButton.IsEnabled = hasProfiles;
    }

    private async Task LoadConfigForProfileAsync(InstanceProfile selectedProfile)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        var profile = _profileService.GetProfileById(selectedProfile.Id) ?? selectedProfile;
        _isLoadingConfig = true;
        ConfigContentHost.IsEnabled = false;
        try
        {
            var serverSettings = await _instanceServerConfigService.LoadServerSettingsAsync(profile);
            var worldSettings = await _instanceServerConfigService.LoadWorldSettingsAsync(profile);
            var worldRules = await _instanceServerConfigService.LoadWorldRulesAsync(profile);

            ApplyConfigServerSettings(serverSettings);
            await LoadConfigSavesAsync(profile, worldSettings.SaveFileLocation);
            ApplyConfigWorldSettings(worldSettings);
            RebuildConfigWorldRules(worldRules);
            UpdateConfigWorldGeneratedState();
            SetConfigStatus(T($"已加载配置：{profile.Name}", $"Loaded configuration: {profile.Name}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"加载配置失败：{ex.Message}", $"Failed to load configuration: {ex.Message}"));
        }
        finally
        {
            ConfigContentHost.IsEnabled = true;
            _isLoadingConfig = false;
        }
    }

    private void ApplyConfigServerSettings(ServerCommonSettings settings)
    {
        ConfigServerNameTextBox.Text = settings.ServerName;
        ConfigServerDescriptionTextBox.Text = settings.ServerDescription ?? string.Empty;
        ConfigServerUrlTextBox.Text = settings.ServerUrl ?? string.Empty;
        ConfigIpTextBox.Text = settings.Ip ?? string.Empty;
        SetNumericValue(ConfigPortNumericUpDown, settings.Port);
        SetNumericValue(ConfigMaxClientsNumericUpDown, settings.MaxClients);
        SetNumericValue(ConfigMaxClientsInQueueNumericUpDown, settings.MaxClientsInQueue);
        ConfigPasswordTextBox.Text = settings.Password ?? string.Empty;
        ConfigAdvertiseServerCheckBox.IsChecked = settings.AdvertiseServer;
        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, settings.WhitelistMode.ToString(CultureInfo.InvariantCulture));
        ConfigUpnpCheckBox.IsChecked = settings.Upnp;
        ConfigAllowPvPCheckBox.IsChecked = settings.AllowPvP;
        ConfigAllowFireSpreadCheckBox.IsChecked = settings.AllowFireSpread;
        ConfigAllowFallingBlocksCheckBox.IsChecked = settings.AllowFallingBlocks;
        ConfigPassTimeWhenEmptyCheckBox.IsChecked = settings.PassTimeWhenEmpty;
        SetNumericValue(ConfigWarnAfkSecondsNumericUpDown, settings.WarnClientsAfterAfkSeconds);
        SetNumericValue(ConfigKickAfkSecondsNumericUpDown, settings.KickClientsAfterAfkSeconds);
        SetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, settings.ClientConnectionTimeout);
        SetNumericValue(ConfigMaxChunkRadiusNumericUpDown, settings.MaxChunkRadius);
        SetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, settings.DieBelowDiskSpaceMb);
        ConfigCorruptionProtectionCheckBox.IsChecked = settings.CorruptionProtection;
        ConfigRegenerateCorruptChunksCheckBox.IsChecked = settings.RegenerateCorruptChunks;
        ConfigStartupCommandsTextBox.Text = settings.StartupCommands;
        ConfigVerifyPlayerAuthCheckBox.IsChecked = settings.VerifyPlayerAuth;
        EnsureComboItem(ConfigServerLanguageComboBox, settings.ServerLanguage);
        ConfigServerLanguageComboBox.SelectedItem = settings.ServerLanguage;
        EnsureConfigChoiceOptionExists(_configDefaultRoleOptions, settings.DefaultRoleCode);
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, settings.DefaultRoleCode);
        ConfigDefaultRoleCodeTextBox.Text = settings.DefaultRoleCode;
        ConfigWelcomeMessageTextBox.Text = settings.WelcomeMessage;
    }

    private async Task LoadConfigSavesAsync(InstanceProfile profile, string preferredSavePath)
    {
        _configSaveItems.Clear();
        var saves = await _saveService.GetSavesAsync(profile);
        foreach (var save in saves)
        {
            _configSaveItems.Add(ConfigSaveFileItem.FromSave(save));
        }

        var normalizedPreferred = NormalizeFullPath(preferredSavePath);
        if (string.IsNullOrWhiteSpace(normalizedPreferred))
        {
            normalizedPreferred = NormalizeFullPath(profile.ActiveSaveFile);
        }

        if (!string.IsNullOrWhiteSpace(normalizedPreferred) &&
            _configSaveItems.All(item => !item.FullPath.Equals(normalizedPreferred, StringComparison.OrdinalIgnoreCase)))
        {
            _configSaveItems.Insert(0, ConfigSaveFileItem.FromPath(normalizedPreferred));
        }

        ConfigSaveFileComboBox.SelectedItem =
            _configSaveItems.FirstOrDefault(item => item.FullPath.Equals(normalizedPreferred, StringComparison.OrdinalIgnoreCase))
            ?? _configSaveItems.FirstOrDefault();
    }

    private void ApplyConfigWorldSettings(WorldSettings settings)
    {
        _configSaveFileLocation = settings.SaveFileLocation;
        ConfigSeedTextBox.Text = settings.Seed;
        ConfigWorldNameTextBox.Text = settings.WorldName;
        EnsureConfigChoiceOptionExists(_configPlayStyleOptions, settings.PlayStyle);
        EnsureConfigChoiceOptionExists(_configWorldTypeOptions, settings.WorldType);
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, settings.PlayStyle);
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, settings.WorldType);
        SetNumericValue(ConfigWorldHeightNumericUpDown, settings.WorldHeight ?? 256);
    }

    private void ClearConfigForm()
    {
        ConfigServerNameTextBox.Text = "Vintage Story Server";
        ConfigServerDescriptionTextBox.Text = string.Empty;
        ConfigServerUrlTextBox.Text = string.Empty;
        ConfigIpTextBox.Text = string.Empty;
        SetNumericValue(ConfigPortNumericUpDown, 42420);
        SetNumericValue(ConfigMaxClientsNumericUpDown, 16);
        SetNumericValue(ConfigMaxClientsInQueueNumericUpDown, 0);
        ConfigPasswordTextBox.Text = string.Empty;
        ConfigAdvertiseServerCheckBox.IsChecked = false;
        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, "0");
        ConfigUpnpCheckBox.IsChecked = false;
        ConfigAllowPvPCheckBox.IsChecked = true;
        ConfigAllowFireSpreadCheckBox.IsChecked = true;
        ConfigAllowFallingBlocksCheckBox.IsChecked = true;
        ConfigPassTimeWhenEmptyCheckBox.IsChecked = false;
        SetNumericValue(ConfigWarnAfkSecondsNumericUpDown, 0);
        SetNumericValue(ConfigKickAfkSecondsNumericUpDown, 0);
        SetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, 150);
        SetNumericValue(ConfigMaxChunkRadiusNumericUpDown, 12);
        SetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, 400);
        ConfigCorruptionProtectionCheckBox.IsChecked = true;
        ConfigRegenerateCorruptChunksCheckBox.IsChecked = false;
        ConfigStartupCommandsTextBox.Text = string.Empty;
        ConfigVerifyPlayerAuthCheckBox.IsChecked = true;
        ConfigServerLanguageComboBox.SelectedItem = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, "suplayer");
        ConfigDefaultRoleCodeTextBox.Text = "suplayer";
        ConfigWelcomeMessageTextBox.Text = string.Empty;
        ConfigSeedTextBox.Text = "123456789";
        ConfigWorldNameTextBox.Text = "A new world";
        _configSaveFileLocation = string.Empty;
        _configSaveItems.Clear();
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, "surviveandbuild");
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, "standard");
        SetNumericValue(ConfigWorldHeightNumericUpDown, 256);
        _configWorldRuleItems.Clear();
        UpdateConfigWorldGeneratedState();
    }

    private async void OnConfigProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingConfigProfiles || ConfigProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        await LoadConfigForProfileAsync(profile);
    }

    private async void OnConfigRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshConfigProfilesAsync();
    }

    private async void OnConfigImportClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("导入 serverconfig.json", "Import serverconfig.json"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("JSON")
                {
                    Patterns = ["*.json"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            await _instanceServerConfigService.ImportRawJsonAsync(profile, path);
            await LoadConfigForProfileAsync(profile);
            SetConfigStatus(T($"已导入配置：{Path.GetFileName(path)}", $"Configuration imported: {Path.GetFileName(path)}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"导入配置失败：{ex.Message}", $"Failed to import configuration: {ex.Message}"));
        }
    }

    private async void OnConfigSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveConfigAsync();
    }

    private void OnConfigDefaultRoleSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig || ConfigDefaultRoleComboBox.SelectedItem is not ConfigChoiceOption option)
        {
            return;
        }

        ConfigDefaultRoleCodeTextBox.Text = option.Value;
    }

    private void OnConfigSaveFileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isLoadingConfig)
        {
            return;
        }

        if (ConfigSaveFileComboBox.SelectedItem is ConfigSaveFileItem item)
        {
            _configSaveFileLocation = item.FullPath;
        }

        UpdateConfigWorldGeneratedState();
    }

    private async Task SaveConfigAsync()
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        ConfigSaveButton.IsEnabled = false;
        try
        {
            var saveFile = ResolveConfigSavePath(profile);
            var serverSettings = CollectConfigServerSettings();
            var worldSettings = CollectConfigWorldSettings(saveFile);
            var rules = _configWorldRuleItems
                .Select(item => new WorldRuleValue
                {
                    Definition = item.Definition,
                    Value = item.Value
                })
                .ToList();

            if (IsSaveWorldGenerated(saveFile))
            {
                var persistedWorldSettings = await _instanceServerConfigService.LoadWorldSettingsAsync(profile);
                var persistedRules = await _instanceServerConfigService.LoadWorldRulesAsync(profile);
                var persistedRuleValues = persistedRules.ToDictionary(
                    rule => rule.Definition.Key,
                    rule => rule.Value ?? string.Empty,
                    StringComparer.OrdinalIgnoreCase);

                worldSettings.Seed = persistedWorldSettings.Seed;
                worldSettings.PlayStyle = persistedWorldSettings.PlayStyle;
                worldSettings.WorldType = persistedWorldSettings.WorldType;
                worldSettings.WorldHeight = persistedWorldSettings.WorldHeight ?? worldSettings.WorldHeight;

                foreach (var rule in rules)
                {
                    if (ConfigOnlyDuringWorldCreateRuleKeys.Contains(rule.Definition.Key) &&
                        persistedRuleValues.TryGetValue(rule.Definition.Key, out var persistedValue))
                    {
                        rule.Value = persistedValue;
                    }
                }
            }

            await _instanceServerConfigService.SaveSettingsAsync(profile, serverSettings, worldSettings, rules);

            profile.ActiveSaveFile = saveFile;
            profile.SaveDirectory = Path.GetDirectoryName(saveFile) ?? profile.SaveDirectory;
            profile.LastUpdatedUtc = DateTimeOffset.UtcNow;
            _profileService.UpdateProfile(profile);
            _configSaveFileLocation = saveFile;

            await LoadConfigSavesAsync(profile, saveFile);
            UpdateConfigWorldGeneratedState();
            await RefreshSavesAsync();
            RefreshLaunchOptions();
            RefreshProfiles();
            SetConfigStatus(T("配置已保存。", "Configuration saved."));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"保存配置失败：{ex.Message}", $"Failed to save configuration: {ex.Message}"));
        }
        finally
        {
            ConfigSaveButton.IsEnabled = true;
        }
    }

    private ServerCommonSettings CollectConfigServerSettings()
    {
        return new ServerCommonSettings
        {
            ServerName = ConfigServerNameTextBox.Text?.Trim() ?? string.Empty,
            ServerDescription = NullIfWhiteSpace(ConfigServerDescriptionTextBox.Text),
            ServerUrl = NullIfWhiteSpace(ConfigServerUrlTextBox.Text),
            Ip = NullIfWhiteSpace(ConfigIpTextBox.Text),
            Port = GetNumericValue(ConfigPortNumericUpDown, 42420),
            MaxClients = GetNumericValue(ConfigMaxClientsNumericUpDown, 16),
            MaxClientsInQueue = GetNumericValue(ConfigMaxClientsInQueueNumericUpDown, 0),
            Password = NullIfWhiteSpace(ConfigPasswordTextBox.Text),
            AdvertiseServer = ConfigAdvertiseServerCheckBox.IsChecked == true,
            WhitelistMode = TryParseInt((ConfigWhitelistModeComboBox.SelectedItem as ConfigChoiceOption)?.Value, 0),
            Upnp = ConfigUpnpCheckBox.IsChecked == true,
            AllowPvP = ConfigAllowPvPCheckBox.IsChecked == true,
            AllowFireSpread = ConfigAllowFireSpreadCheckBox.IsChecked == true,
            AllowFallingBlocks = ConfigAllowFallingBlocksCheckBox.IsChecked == true,
            PassTimeWhenEmpty = ConfigPassTimeWhenEmptyCheckBox.IsChecked == true,
            WarnClientsAfterAfkSeconds = GetNumericValue(ConfigWarnAfkSecondsNumericUpDown, 0),
            KickClientsAfterAfkSeconds = GetNumericValue(ConfigKickAfkSecondsNumericUpDown, 0),
            ClientConnectionTimeout = GetNumericValue(ConfigClientConnectionTimeoutNumericUpDown, 150),
            MaxChunkRadius = GetNumericValue(ConfigMaxChunkRadiusNumericUpDown, 12),
            DieBelowDiskSpaceMb = GetNumericValue(ConfigDieBelowDiskSpaceMbNumericUpDown, 400),
            CorruptionProtection = ConfigCorruptionProtectionCheckBox.IsChecked == true,
            RegenerateCorruptChunks = ConfigRegenerateCorruptChunksCheckBox.IsChecked == true,
            StartupCommands = ConfigStartupCommandsTextBox.Text?.Trim() ?? string.Empty,
            VerifyPlayerAuth = ConfigVerifyPlayerAuthCheckBox.IsChecked == true,
            ServerLanguage = ConfigServerLanguageComboBox.SelectedItem?.ToString() ?? ResolveDefaultServerLanguage(),
            DefaultRoleCode = string.IsNullOrWhiteSpace(ConfigDefaultRoleCodeTextBox.Text)
                ? (ConfigDefaultRoleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "suplayer"
                : ConfigDefaultRoleCodeTextBox.Text.Trim(),
            WelcomeMessage = ConfigWelcomeMessageTextBox.Text?.Trim() ?? string.Empty
        };
    }

    private WorldSettings CollectConfigWorldSettings(string saveFile)
    {
        return new WorldSettings
        {
            Seed = ConfigSeedTextBox.Text?.Trim() ?? string.Empty,
            WorldName = ConfigWorldNameTextBox.Text?.Trim() ?? string.Empty,
            SaveFileLocation = saveFile,
            PlayStyle = (ConfigPlayStyleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "surviveandbuild",
            WorldType = (ConfigWorldTypeComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? "standard",
            WorldHeight = GetNumericValue(ConfigWorldHeightNumericUpDown, 256)
        };
    }

    private void RebuildConfigChoiceOptions()
    {
        var selectedWhitelist = (ConfigWhitelistModeComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        var selectedRole = (ConfigDefaultRoleComboBox.SelectedItem as ConfigChoiceOption)?.Value ?? ConfigDefaultRoleCodeTextBox.Text;
        var selectedPlayStyle = (ConfigPlayStyleComboBox.SelectedItem as ConfigChoiceOption)?.Value;
        var selectedWorldType = (ConfigWorldTypeComboBox.SelectedItem as ConfigChoiceOption)?.Value;

        _configWhitelistModeOptions.Clear();
        foreach (var (value, zh, en) in ConfigWhitelistModeDefinitions)
        {
            _configWhitelistModeOptions.Add(new ConfigChoiceOption(value.ToString(CultureInfo.InvariantCulture), T(zh, en)));
        }

        _configDefaultRoleOptions.Clear();
        foreach (var (value, zh, en) in ConfigRoleDefinitions)
        {
            _configDefaultRoleOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        _configPlayStyleOptions.Clear();
        foreach (var (value, zh, en) in ConfigPlayStyleDefinitions)
        {
            _configPlayStyleOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        _configWorldTypeOptions.Clear();
        foreach (var (value, zh, en) in ConfigWorldTypeDefinitions)
        {
            _configWorldTypeOptions.Add(new ConfigChoiceOption(value, T(zh, en)));
        }

        SelectConfigChoiceByValue(ConfigWhitelistModeComboBox, _configWhitelistModeOptions, selectedWhitelist ?? "0");
        EnsureConfigChoiceOptionExists(_configDefaultRoleOptions, selectedRole);
        SelectConfigChoiceByValue(ConfigDefaultRoleComboBox, _configDefaultRoleOptions, selectedRole ?? "suplayer");
        EnsureConfigChoiceOptionExists(_configPlayStyleOptions, selectedPlayStyle);
        SelectConfigChoiceByValue(ConfigPlayStyleComboBox, _configPlayStyleOptions, selectedPlayStyle ?? "surviveandbuild");
        EnsureConfigChoiceOptionExists(_configWorldTypeOptions, selectedWorldType);
        SelectConfigChoiceByValue(ConfigWorldTypeComboBox, _configWorldTypeOptions, selectedWorldType ?? "standard");
    }

    private void RebuildConfigWorldRules(IReadOnlyList<WorldRuleValue> rules)
    {
        _configWorldRuleItems.Clear();
        foreach (var rule in rules)
        {
            var value = rule.Value ?? string.Empty;
            var item = new ConfigWorldRuleItem(rule.Definition, value, _isChinese, BuildConfigRuleChoiceOptions(rule.Definition, value))
            {
                IsOnlyDuringWorldCreate = ConfigOnlyDuringWorldCreateRuleKeys.Contains(rule.Definition.Key)
            };
            _configWorldRuleItems.Add(item);
        }
    }

    private IReadOnlyList<ConfigChoiceOption> BuildConfigRuleChoiceOptions(WorldRuleDefinition definition, string currentValue)
    {
        if (definition.Choices.Count == 0)
        {
            return [];
        }

        var options = new List<ConfigChoiceOption>(definition.Choices.Count + 1);
        for (var index = 0; index < definition.Choices.Count; index++)
        {
            var value = definition.Choices[index];
            var choiceName = index < definition.ChoiceNames.Count ? definition.ChoiceNames[index] : value;
            options.Add(new ConfigChoiceOption(value, ResolveConfigRuleChoiceLabel(definition.Key, value, choiceName)));
        }

        if (!string.IsNullOrWhiteSpace(currentValue) &&
            options.All(option => !option.Value.Equals(currentValue, StringComparison.OrdinalIgnoreCase)))
        {
            options.Add(new ConfigChoiceOption(currentValue, currentValue));
        }

        return options;
    }

    private string ResolveConfigRuleChoiceLabel(string key, string value, string name)
    {
        if (!_isChinese)
        {
            return name;
        }

        if (value.Equals("true", StringComparison.OrdinalIgnoreCase))
        {
            return "启用";
        }

        if (value.Equals("false", StringComparison.OrdinalIgnoreCase))
        {
            return "禁用";
        }

        return key.ToLowerInvariant() switch
        {
            "gamemode" when value.Equals("survival", StringComparison.OrdinalIgnoreCase) => "生存",
            "gamemode" when value.Equals("creative", StringComparison.OrdinalIgnoreCase) => "创造",
            "playerlives" when value == "-1" => "无限",
            "worldedge" when value.Equals("blocked", StringComparison.OrdinalIgnoreCase) => "被阻挡",
            "worldedge" when value.Equals("traversable", StringComparison.OrdinalIgnoreCase) => "可越过/可掉落",
            "deathpunishment" when value.Equals("drop", StringComparison.OrdinalIgnoreCase) => "掉落背包物品",
            "deathpunishment" when value.Equals("keep", StringComparison.OrdinalIgnoreCase) => "保留背包物品",
            "seasons" when value.Equals("enabled", StringComparison.OrdinalIgnoreCase) => "启用",
            "seasons" when value.Equals("spring", StringComparison.OrdinalIgnoreCase) => "关闭，永远春天",
            "seasons" when value.Equals("summer", StringComparison.OrdinalIgnoreCase) => "关闭，永远夏天",
            "seasons" when value.Equals("fall", StringComparison.OrdinalIgnoreCase) => "关闭，永远秋天",
            "seasons" when value.Equals("winter", StringComparison.OrdinalIgnoreCase) => "关闭，永远冬天",
            "temporalrifts" when value.Equals("off", StringComparison.OrdinalIgnoreCase) => "关闭",
            "temporalrifts" when value.Equals("invisible", StringComparison.OrdinalIgnoreCase) => "不可见",
            "temporalrifts" when value.Equals("visible", StringComparison.OrdinalIgnoreCase) => "可见",
            _ => ResolveCommonConfigChoiceName(name)
        };
    }

    private static string ResolveCommonConfigChoiceName(string name)
    {
        return name switch
        {
            "Enabled" => "启用",
            "Disabled" => "禁用",
            "Allowed" => "允许",
            "Disallowed" => "不允许",
            "Off" => "关",
            "Normal" => "正常",
            "Fast" => "快",
            "Slightly faster" => "稍快",
            "Slightly slower" => "稍慢",
            "Slower" => "缓",
            "Much slower" => "很慢",
            "Very common" => "非常常见",
            "Common" => "常见",
            "Uncommon" => "不常见",
            "Rare" => "稀有",
            "Very Rare" => "非常稀有",
            "Never" => "不存在",
            _ => name
        };
    }

    private void RefreshConfigWorldRuleLabels()
    {
        foreach (var item in _configWorldRuleItems)
        {
            item.SetLanguage(_isChinese, BuildConfigRuleChoiceOptions(item.Definition, item.Value));
        }
    }

    private void UpdateConfigWorldGeneratedState()
    {
        var savePath = (ConfigSaveFileComboBox.SelectedItem as ConfigSaveFileItem)?.FullPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = _configSaveFileLocation;
        }

        var generated = IsSaveWorldGenerated(savePath);
        ConfigWorldGeneratedNoticeTextBlock.IsVisible = generated;
        ConfigSeedTextBox.IsEnabled = !generated;
        ConfigPlayStyleComboBox.IsEnabled = !generated;
        ConfigWorldTypeComboBox.IsEnabled = !generated;
        ConfigWorldHeightNumericUpDown.IsEnabled = !generated;

        foreach (var rule in _configWorldRuleItems)
        {
            rule.CanEdit = !(generated && rule.IsOnlyDuringWorldCreate);
        }
    }

    private InstanceProfile? GetSelectedConfigProfile()
    {
        if (ConfigProfileComboBox.SelectedItem is not InstanceProfile selectedProfile)
        {
            return null;
        }

        return _profileService.GetProfileById(selectedProfile.Id) ?? selectedProfile;
    }

    private string ResolveConfigSavePath(InstanceProfile profile)
    {
        var savePath = (ConfigSaveFileComboBox.SelectedItem as ConfigSaveFileItem)?.FullPath;
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = _configSaveFileLocation;
        }

        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = profile.ActiveSaveFile;
        }

        var saveRoot = profile.SaveDirectory;
        if (string.IsNullOrWhiteSpace(saveRoot))
        {
            saveRoot = Path.GetDirectoryName(_profileService.GetDefaultSaveFilePath(profile.Id)) ?? profile.DirectoryPath;
        }

        saveRoot = Path.GetFullPath(saveRoot);
        Directory.CreateDirectory(saveRoot);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            savePath = Path.Combine(saveRoot, "default.vcdbs");
        }

        var fullPath = Path.GetFullPath(savePath.Trim());
        if (!IsSameOrChildPath(Path.GetDirectoryName(fullPath), saveRoot))
        {
            fullPath = Path.Combine(saveRoot, Path.GetFileName(fullPath));
        }

        return fullPath;
    }

    private void OpenLocalFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"打开文件失败：{ex.Message}", $"Failed to open file: {ex.Message}"));
        }
    }

    private void SetConfigStatus(string message)
    {
        ConfigStatusTextBlock.Text = message;
    }

    private static void SetNumericValue(NumericUpDown control, int value)
    {
        control.Value = value;
    }

    private static void SetNumericValue(NumericUpDown control, double value)
    {
        control.Value = (decimal)value;
    }

    private static int GetNumericValue(NumericUpDown control, int fallback)
    {
        return control.Value.HasValue
            ? decimal.ToInt32(control.Value.Value)
            : fallback;
    }

    private static double GetNumericDoubleValue(NumericUpDown control, double fallback)
    {
        return control.Value.HasValue
            ? decimal.ToDouble(control.Value.Value)
            : fallback;
    }

    private static int TryParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static int ParseClampedInt(string? value, int fallback, int min, int max)
    {
        return Math.Clamp(TryParseInt(value, fallback), min, max);
    }

    private static string? NullIfWhiteSpace(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string ResolveDefaultServerLanguage()
    {
        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static bool IsSameOrChildPath(string? candidatePath, string? rootPath)
    {
        var candidate = NormalizeFullPath(candidatePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = NormalizeFullPath(rootPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSaveWorldGenerated(string? savePath)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return false;
        }

        try
        {
            var fullPath = Path.GetFullPath(savePath.Trim());
            return File.Exists(fullPath) && new FileInfo(fullPath).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private void EnsureComboItem(ComboBox comboBox, string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        if (comboBox.ItemsSource is IEnumerable<string> items &&
            items.Any(item => item.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var values = ConfigServerLanguageOptions
            .Append(value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        comboBox.ItemsSource = values;
    }

    private static void SelectConfigChoiceByValue(
        ComboBox comboBox,
        IEnumerable<ConfigChoiceOption> options,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            comboBox.SelectedIndex = -1;
            return;
        }

        comboBox.SelectedItem = options.FirstOrDefault(option =>
            option.Value.Equals(value.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    private void EnsureConfigChoiceOptionExists(ObservableCollection<ConfigChoiceOption> options, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var normalized = value.Trim();
        if (options.Any(option => option.Value.Equals(normalized, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        options.Add(new ConfigChoiceOption(normalized, T($"自定义：{normalized}", $"Custom: {normalized}")));
    }

    private async void OnCreateProfileClick(object? sender, RoutedEventArgs e)
    {
        var version = CreateVersionComboBox.SelectedItem?.ToString() ?? string.Empty;
        var name = ProfileNameTextBox.Text?.Trim() ?? string.Empty;
        try
        {
            await Task.Run(() => _profileService.CreateProfile(name, version));
            ProfileNameTextBox.Text = string.Empty;
            RefreshProfiles();
            AppendConsoleLine($"[system] 已创建档案：{name}");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 创建档案失败：{ex.Message}");
        }
    }

    private async void OnImportProfileClick(object? sender, RoutedEventArgs e)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("选择服务端档案目录", "Select server profile directory"),
            AllowMultiple = false
        });

        var path = TryGetLocalPath(folders.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var profile = _profileService.ImportProfile(path);
            RefreshProfiles();
            AppendConsoleLine($"[system] 已导入档案：{profile.Name}");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 导入档案失败：{ex.Message}");
        }
    }

    private void OnDeleteProfilesClick(object? sender, RoutedEventArgs e)
    {
        var selectedIds = ProfilesListBox.SelectedItems?
            .OfType<ProfileListItem>()
            .Select(item => item.Id)
            .ToArray() ?? [];
        if (selectedIds.Length == 0)
        {
            return;
        }

        try
        {
            var count = _profileService.DeleteProfiles(selectedIds, deleteData: true);
            RefreshProfiles();
            AppendConsoleLine($"[system] 已删除 {count} 个档案。");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 删除档案失败：{ex.Message}");
        }
    }

    private void OnRefreshProfilesClick(object? sender, RoutedEventArgs e)
    {
        RefreshProfiles();
    }

    private async void OnSaveProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingSaves)
        {
            return;
        }

        await RefreshSavesAsync();
    }

    private async void OnImportSaveClick(object? sender, RoutedEventArgs e)
    {
        if (SaveProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AppendConsoleLine("[system] 导入存档前请先选择一个档案，不能选择全部。");
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择存档文件", "Select save file"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Vintage Story Save")
                {
                    Patterns = ["*.vcdbs"]
                }
            ]
        });

        var path = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var target = await _saveService.ImportSaveAsync(profile, path);
            await RefreshSavesAsync();
            AppendConsoleLine($"[system] 已导入存档：{Path.GetFileName(target)}");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 导入存档失败：{ex.Message}");
        }
    }

    private async void OnDeleteSavesClick(object? sender, RoutedEventArgs e)
    {
        var selectedPaths = SavesListBox.SelectedItems?
            .OfType<SaveListItem>()
            .Select(item => item.FullPath)
            .ToArray() ?? [];
        if (selectedPaths.Length == 0)
        {
            return;
        }

        try
        {
            var count = SaveProfileComboBox.SelectedItem is InstanceProfile profile
                ? await _saveService.DeleteSavesAsync(profile, selectedPaths)
                : await _saveService.DeleteSavesAsync(selectedPaths);
            await RefreshSavesAsync();
            AppendConsoleLine($"[system] 已删除 {count} 个存档。");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 删除存档失败：{ex.Message}");
        }
    }

    private async void OnRefreshSavesClick(object? sender, RoutedEventArgs e)
    {
        await RefreshSavesAsync();
    }

    private async void OnCreateSaveClick(object? sender, RoutedEventArgs e)
    {
        if (SaveProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            AppendConsoleLine("[system] 创建存档前请先选择一个档案，不能选择全部。");
            return;
        }

        var name = NewSaveNameTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(name))
        {
            AppendConsoleLine("[system] 请输入新存档名称。");
            return;
        }

        try
        {
            await _saveService.CreateSaveAsync(profile, name);
            await RefreshSavesAsync();
            AppendConsoleLine($"[system] 已创建存档：{name}");
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 创建存档失败：{ex.Message}");
        }
    }

    private void OnOpenSaveDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directoryPath } ||
            string.IsNullOrWhiteSpace(directoryPath) ||
            !Directory.Exists(directoryPath))
        {
            return;
        }

        Process.Start(new ProcessStartInfo { FileName = directoryPath, UseShellExecute = true });
    }

    private async void OnToggleDefaultSaveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: SaveListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is null)
        {
            AppendConsoleLine(T("[system] 锁定失败：未找到对应档案。", "[system] Lock failed: profile not found."));
            return;
        }

        try
        {
            await _saveService.SetActiveSaveAsync(profile, item.FullPath);
            var preferences = _preferencesService.Load();
            preferences.DefaultLaunchProfileId = profile.Id;
            preferences.DefaultLaunchSaveFile = item.FullPath;
            _preferencesService.Save(preferences);
            await RefreshSavesAsync();
            RefreshLaunchOptions();
            AppendConsoleLine(T($"[system] 已锁定默认存档：{item.FileName}", $"[system] Default save locked: {item.FileName}"));
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 锁定默认存档失败：{ex.Message}", $"[system] Failed to lock default save: {ex.Message}"));
        }
    }

    private bool TryGetLockedLaunchTarget(out InstanceProfile profile, out string lockedSavePath)
    {
        profile = null!;
        lockedSavePath = string.Empty;

        var preferences = _preferencesService.Load();
        if (string.IsNullOrWhiteSpace(preferences.DefaultLaunchProfileId))
        {
            return false;
        }

        var targetProfile = _profileService.GetProfileById(preferences.DefaultLaunchProfileId.Trim());
        if (targetProfile is null)
        {
            return false;
        }

        var targetSavePath = NormalizeFullPath(preferences.DefaultLaunchSaveFile);
        if (string.IsNullOrWhiteSpace(targetSavePath))
        {
            return false;
        }

        profile = targetProfile;
        lockedSavePath = targetSavePath;
        return true;
    }

    private async void OnImportServerPackageClick(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择服务端压缩包", "Select server package"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("ZIP")
                {
                    Patterns = ["*.zip"]
                }
            ]
        });

        var sourcePath = TryGetLocalPath(files.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(sourcePath))
        {
            return;
        }

        try
        {
            var preferences = _preferencesService.Load();
            var importedPath = await _serverPackageService.ImportServerPackageAsync(sourcePath, preferences.ServerDirectory);
            SetDownloadStatus(T($"导入完成：{Path.GetFileName(importedPath)}", $"Imported: {Path.GetFileName(importedPath)}"));
            RefreshProfiles();
            await RefreshDownloadVersionsAsync(forceReload: true);
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async void OnRefreshDownloadVersionsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshDownloadVersionsAsync(forceReload: true);
        RefreshProfiles();
    }

    private async void OnDownloadVersionClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DownloadVersionListItem item } || !item.CanDownload)
        {
            return;
        }

        var preferences = _preferencesService.Load();
        var targetPath = Path.Combine(preferences.ServerDirectory, item.Entry.FileName);
        try
        {
            DownloadVersionsListBox.IsEnabled = false;
            var progress = new Progress<double>(value =>
            {
                SetDownloadStatus(T($"正在下载 {item.Entry.Version} {value:P0}", $"Downloading {item.Entry.Version} {value:P0}"));
            });
            await _serverPackageService.DownloadByCdnAsync(item.Entry.CdnUrl, targetPath, progress);
            SetDownloadStatus(T($"下载完成：{item.Entry.Version}", $"Download completed: {item.Entry.Version}"));
            RefreshProfiles();
            await RefreshDownloadVersionsAsync(forceReload: false);
        }
        catch (Exception ex)
        {
            SetDownloadStatus(T($"下载失败：{ex.Message}", $"Download failed: {ex.Message}"));
        }
        finally
        {
            DownloadVersionsListBox.IsEnabled = true;
        }
    }

    private static string? TryGetLocalPath(IStorageItem? item)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            return item.TryGetLocalPath();
        }
        catch
        {
            return item.Path.LocalPath;
        }
    }

    private async Task BrowseFolderToTextBoxAsync(TextBox targetTextBox, string title)
    {
        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false
        });

        var path = TryGetLocalPath(folders.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        targetTextBox.Text = path;
        SaveServerSettings();
    }

    private static HttpClient CreateSharedHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(20)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }

    private static string? FindBundledReadmePath(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, "LauncherGo", fileName),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName)),
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", fileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string GetAppLogDirectory()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LauncherGo",
            "logs");
    }

    private static async Task<Bitmap?> LoadAvatarImageAsync(string url)
    {
        if (string.IsNullOrWhiteSpace(url) ||
            !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return null;
        }

        try
        {
            await using var source = await SharedHttpClient.GetStreamAsync(uri);
            using var buffer = new MemoryStream();
            await source.CopyToAsync(buffer);
            buffer.Position = 0;
            return new Bitmap(buffer);
        }
        catch
        {
            return null;
        }
    }

    private static string GetSponsorApiUrl()
    {
        var overrideUrl = Environment.GetEnvironmentVariable("LAUNCHERGO_SPONSOR_API_URL");
        return string.IsNullOrWhiteSpace(overrideUrl)
            ? SponsorApiUrl
            : overrideUrl.Trim();
    }

    private static bool TryGetSponsorList(JsonElement root, out JsonElement listNode)
    {
        if (root.TryGetProperty("sponsors", out listNode) &&
            listNode.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        if (root.TryGetProperty("data", out var dataNode) &&
            dataNode.TryGetProperty("list", out listNode) &&
            listNode.ValueKind == JsonValueKind.Array)
        {
            return true;
        }

        listNode = default;
        return false;
    }

    private async Task<SettingsSponsorItem> BuildSponsorItemAsync(JsonElement sponsor)
    {
        var name = ReadFirstJsonString(sponsor, "name", "userName");
        var avatarUrl = ReadFirstJsonString(sponsor, "avatarUrl", "avatar", "avatar_url", "pic", "url");
        if (string.IsNullOrWhiteSpace(name) &&
            sponsor.TryGetProperty("user", out var userNode))
        {
            name = ReadJsonString(userNode, "name");
            avatarUrl = string.IsNullOrWhiteSpace(avatarUrl)
                ? ReadFirstJsonString(userNode, "avatarUrl", "avatar", "avatar_url", "pic", "url")
                : avatarUrl;
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = ReadJsonString(sponsor, "user_id");
        }

        var amount = ReadFirstJsonString(sponsor, "amount", "all_sum_amount", "sum_amount");

        var plan = ReadJsonString(sponsor, "plan");
        if (sponsor.TryGetProperty("current_plan", out var currentPlanNode))
        {
            plan = string.IsNullOrWhiteSpace(plan)
                ? ReadJsonString(currentPlanNode, "name")
                : plan;
        }

        if (string.IsNullOrWhiteSpace(plan) &&
            sponsor.TryGetProperty("sponsor_plans", out var plansNode) &&
            plansNode.ValueKind == JsonValueKind.Array)
        {
            var firstPlan = plansNode.EnumerateArray().FirstOrDefault();
            if (firstPlan.ValueKind == JsonValueKind.Object)
            {
                plan = ReadJsonString(firstPlan, "name");
            }
        }

        return new SettingsSponsorItem
        {
            Name = string.IsNullOrWhiteSpace(name) ? T("匿名赞助者", "Anonymous Sponsor") : name,
            AvatarImage = await LoadAvatarImageAsync(avatarUrl),
            AmountText = string.IsNullOrWhiteSpace(amount)
                ? T("累计赞助金额未知", "Total sponsored amount unknown")
                : T($"累计赞助 {amount} 元", $"Total sponsored CNY {amount}"),
            PlanText = string.IsNullOrWhiteSpace(plan)
                ? T("未识别赞助方案", "Plan not available")
                : plan
        };
    }

    private static string ReadFirstJsonString(JsonElement node, params string[] propertyNames)
    {
        foreach (var propertyName in propertyNames)
        {
            var value = ReadJsonString(node, propertyName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadJsonString(JsonElement node, string propertyName)
    {
        if (!node.TryGetProperty(propertyName, out var value))
        {
            return string.Empty;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString()?.Trim() ?? string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => string.Empty
        };
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        }

        return duration.ToString("hh\\:mm\\:ss", CultureInfo.InvariantCulture);
    }

    private static void PushNextSample(List<double> samples, double value, int maxCount = RealtimeRangeSeconds)
    {
        if (samples.Count >= maxCount)
        {
            samples.RemoveAt(0);
        }

        samples.Add(value);
    }

    private static double BytesToMb(long bytes)
    {
        return bytes <= 0 ? 0 : bytes / 1024.0 / 1024.0;
    }

    private static double NiceCeiling(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var exponent = Math.Floor(Math.Log10(value));
        var magnitude = Math.Pow(10, exponent);
        var normalized = value / magnitude;
        var nice = normalized switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 5 => 5,
            _ => 10
        };

        return nice * magnitude;
    }

    [GeneratedRegex(@"joins\.|left\.|leaves\.|离开|进入|加入|玩家", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerEventHintRegex();

    private enum MainTab
    {
        Home,
        Monitor,
        Console,
        InstanceManage,
        Settings,
        Connection
    }

    private enum HomeMetric
    {
        Server,
        Robot,
        Players,
        Network
    }

    private enum InstanceManageTab
    {
        Profiles,
        Config,
        Saves,
        Automation,
        Mods,
        DownloadVersions
    }

    private enum SettingsTab
    {
        Server,
        Appearance,
        Network,
        Advanced,
        About,
        Sponsors,
        Contributors
    }

    private enum ConnectionTab
    {
        Frp,
        OpenInfo,
        Robot,
        Auth
    }

    private enum ConnectionProcessKind
    {
        Frp,
        ThirdPartyFrpc
    }

    private sealed class OsqEndpointEditorRow(TextBox hostTextBox, TextBox tokenTextBox, bool enabled = true)
    {
        public TextBox HostTextBox { get; } = hostTextBox;

        public TextBox TokenTextBox { get; } = tokenTextBox;

        public bool Enabled { get; set; } = enabled;
    }

    public sealed class ProfileListItem
    {
        public required string Id { get; init; }

        public required string Name { get; init; }

        public required string Version { get; init; }

        public required string DirectoryPath { get; init; }

        public required string ActiveSaveFile { get; init; }

        public static ProfileListItem FromProfile(InstanceProfile profile)
        {
            return new ProfileListItem
            {
                Id = profile.Id,
                Name = profile.Name,
                Version = profile.Version,
                DirectoryPath = profile.DirectoryPath,
                ActiveSaveFile = profile.ActiveSaveFile
            };
        }
    }

    public sealed class SaveListItem
    {
        private const string UnlockedIconPath =
            "M528 320C528 205.1 434.9 112 320 112C205.1 112 112 205.1 112 320C112 434.9 205.1 528 320 528C434.9 528 528 434.9 528 320zM64 320C64 178.6 178.6 64 320 64C461.4 64 576 178.6 576 320C576 461.4 461.4 576 320 576C178.6 576 64 461.4 64 320z";
        private const string LockedIconPath =
            "M320 576C178.6 576 64 461.4 64 320C64 178.6 178.6 64 320 64C461.4 64 576 178.6 576 320C576 461.4 461.4 576 320 576zM438 209.7C427.3 201.9 412.3 204.3 404.5 215L285.1 379.2L233 327.1C223.6 317.7 208.4 317.7 199.1 327.1C189.8 336.5 189.7 351.7 199.1 361L271.1 433C276.1 438 282.9 440.5 289.9 440C296.9 439.5 303.3 435.9 307.4 430.2L443.3 243.2C451.1 232.5 448.7 217.5 438 209.7z";

        public required string ProfileId { get; init; }

        public required string FullPath { get; init; }

        public required string FileName { get; init; }

        public required string ProfileName { get; init; }

        public required string Description { get; init; }

        public required string DirectoryPath { get; init; }

        public required string SizeText { get; init; }

        public required string LastWriteText { get; init; }

        public bool IsLocked { get; init; }

        public string LockActionText { get; init; } = string.Empty;

        public string LockIconData => IsLocked ? LockedIconPath : UnlockedIconPath;

        public string LockIconBrush => IsLocked ? "#6B8E23" : "#8A8A8A";

        public static SaveListItem FromSave(
            SaveFileEntry save,
            bool isLocked,
            string lockedActionText,
            string unlockedActionText)
        {
            var directoryPath = Path.GetDirectoryName(save.FullPath) ?? string.Empty;
            var sizeText = FormatFileSize(save.SizeBytes);
            var lastWriteText = save.LastWriteTimeUtc.LocalDateTime.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
            return new SaveListItem
            {
                ProfileId = save.ProfileId,
                FullPath = save.FullPath,
                FileName = save.FileName,
                ProfileName = save.ProfileName,
                Description = $"{sizeText}  {lastWriteText}  {save.FullPath}",
                DirectoryPath = directoryPath,
                SizeText = sizeText,
                LastWriteText = lastWriteText,
                IsLocked = isLocked,
                LockActionText = isLocked ? lockedActionText : unlockedActionText
            };
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes >= 1024L * 1024 * 1024)
            {
                return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
            }

            if (bytes >= 1024L * 1024)
            {
                return $"{bytes / 1024d / 1024d:F1} MB";
            }

            if (bytes >= 1024)
            {
                return $"{bytes / 1024d:F1} KB";
            }

            return $"{bytes} B";
        }
    }

    public sealed class DownloadVersionListItem(
        ServerDownloadEntry entry,
        string displayText,
        bool isDownloaded,
        string downloadedText,
        string actionText)
    {
        public ServerDownloadEntry Entry { get; } = entry;

        public string DisplayText { get; } = displayText;

        public bool IsDownloaded { get; } = isDownloaded;

        public bool CanDownload => !IsDownloaded;

        public string DownloadedText { get; } = downloadedText;

        public string ActionText { get; } = actionText;
    }

    public sealed class SettingsContributorItem
    {
        public required string Login { get; init; }

        public required string HtmlUrl { get; init; }

        public Bitmap? AvatarImage { get; init; }

        public bool HasAvatar => AvatarImage is not null;

        public bool HasNoAvatar => AvatarImage is null;

        public string Initial => string.IsNullOrWhiteSpace(Login) ? "?" : Login.Trim()[..1].ToUpperInvariant();

        public required string ContributionsText { get; init; }
    }

    public sealed class SettingsSponsorItem
    {
        public required string Name { get; init; }

        public Bitmap? AvatarImage { get; init; }

        public bool HasAvatar => AvatarImage is not null;

        public bool HasNoAvatar => AvatarImage is null;

        public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name.Trim()[..1].ToUpperInvariant();

        public required string AmountText { get; init; }

        public required string PlanText { get; init; }
    }

    public sealed class ConfigChoiceOption(string value, string label)
    {
        public string Value { get; } = value;

        public string Label { get; } = label;

        public override string ToString() => Label;
    }

    public sealed class AutomationActionWindowItem : INotifyPropertyChanged
    {
        private AutomationScheduleMode _scheduleMode = AutomationScheduleMode.Weekly;
        private AutomationActionType _action = AutomationActionType.Start;
        private string _startDayOfWeek = "1";
        private string _endDayOfWeek = "7";
        private string _startDate = string.Empty;
        private string _endDate = string.Empty;
        private string _startTime = "08:00";
        private string _endTime = "23:00";
        private bool _enabled = true;

        public AutomationActionWindowItem()
        {
            ScheduleModeOptions = new ObservableCollection<ConfigChoiceOption>
            {
                new(AutomationScheduleMode.Weekly.ToString(), "每周"),
                new(AutomationScheduleMode.DateRange.ToString(), "日期范围")
            };
            ActionOptions = new ObservableCollection<ConfigChoiceOption>
            {
                new(AutomationActionType.Start.ToString(), "启动"),
                new(AutomationActionType.Stop.ToString(), "停止")
            };
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public ObservableCollection<ConfigChoiceOption> ScheduleModeOptions { get; }

        public ObservableCollection<ConfigChoiceOption> ActionOptions { get; }

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string StartDayOfWeek
        {
            get => _startDayOfWeek;
            set => SetField(ref _startDayOfWeek, value);
        }

        public string EndDayOfWeek
        {
            get => _endDayOfWeek;
            set => SetField(ref _endDayOfWeek, value);
        }

        public string StartDate
        {
            get => _startDate;
            set
            {
                if (SetField(ref _startDate, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(StartDateValue));
                }
            }
        }

        public string EndDate
        {
            get => _endDate;
            set
            {
                if (SetField(ref _endDate, value ?? string.Empty))
                {
                    OnPropertyChanged(nameof(EndDateValue));
                }
            }
        }

        public DateTimeOffset? StartDateValue
        {
            get => TryParseDateValue(_startDate);
            set => SetDateValue(ref _startDate, value, nameof(StartDateValue), nameof(StartDate));
        }

        public DateTimeOffset? EndDateValue
        {
            get => TryParseDateValue(_endDate);
            set => SetDateValue(ref _endDate, value, nameof(EndDateValue), nameof(EndDate));
        }

        public string StartTime
        {
            get => _startTime;
            set => SetField(ref _startTime, value);
        }

        public string EndTime
        {
            get => _endTime;
            set => SetField(ref _endTime, value);
        }

        public ConfigChoiceOption SelectedScheduleMode
        {
            get => ScheduleModeOptions.First(option => option.Value.Equals(_scheduleMode.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null) return;
                if (Enum.TryParse(value.Value, true, out AutomationScheduleMode mode))
                {
                    _scheduleMode = mode;
                    OnPropertyChanged(nameof(SelectedScheduleMode));
                }
            }
        }

        public ConfigChoiceOption SelectedAction
        {
            get => ActionOptions.First(option => option.Value.Equals(_action.ToString(), StringComparison.OrdinalIgnoreCase));
            set
            {
                if (value is null) return;
                if (Enum.TryParse(value.Value, true, out AutomationActionType action))
                {
                    _action = action;
                    OnPropertyChanged(nameof(SelectedAction));
                }
            }
        }

        public AutomationActionWindow ToModel()
        {
            return new AutomationActionWindow
            {
                ScheduleMode = _scheduleMode,
                StartDayOfWeek = TryParseInt(_startDayOfWeek, 1),
                EndDayOfWeek = TryParseInt(_endDayOfWeek, 7),
                StartDate = _startDate?.Trim() ?? string.Empty,
                EndDate = _endDate?.Trim() ?? string.Empty,
                StartTime = _startTime?.Trim() ?? string.Empty,
                EndTime = _endTime?.Trim() ?? string.Empty,
                Action = _action,
                Enabled = _enabled
            };
        }

        public static AutomationActionWindowItem FromModel(AutomationActionWindow model)
        {
            return new AutomationActionWindowItem
            {
                Enabled = model.Enabled,
                StartDayOfWeek = model.StartDayOfWeek.ToString(CultureInfo.InvariantCulture),
                EndDayOfWeek = model.EndDayOfWeek.ToString(CultureInfo.InvariantCulture),
                StartDate = model.StartDate,
                EndDate = model.EndDate,
                StartTime = model.StartTime,
                EndTime = model.EndTime,
                _scheduleMode = model.ScheduleMode,
                _action = model.Action
            };
        }

        private static DateTimeOffset? TryParseDateValue(string? value)
        {
            var text = value?.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                return null;
            }

            if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) &&
                !DateOnly.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out date) &&
                !DateOnly.TryParse(text, out date))
            {
                return null;
            }

            return new DateTimeOffset(date.Year, date.Month, date.Day, 0, 0, 0, TimeSpan.Zero);
        }

        private bool SetDateValue(ref string field, DateTimeOffset? value, string datePropertyName, string textPropertyName)
        {
            var next = value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? string.Empty;
            if (EqualityComparer<string>.Default.Equals(field, next))
            {
                return false;
            }

            field = next;
            OnPropertyChanged(datePropertyName);
            OnPropertyChanged(textPropertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class AutomationTimeItem : INotifyPropertyChanged
    {
        private string _time;

        public AutomationTimeItem(string time = "03:00")
        {
            _time = time;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ScheduledBroadcastItem : INotifyPropertyChanged
    {
        private string _time = "12:00";
        private string _message = string.Empty;
        private bool _enabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public bool Enabled
        {
            get => _enabled;
            set => SetField(ref _enabled, value);
        }

        public string Time
        {
            get => _time;
            set => SetField(ref _time, value);
        }

        public string Message
        {
            get => _message;
            set => SetField(ref _message, value);
        }

        public ScheduledBroadcastMessage ToModel()
        {
            return new ScheduledBroadcastMessage
            {
                Enabled = _enabled,
                Time = _time?.Trim() ?? string.Empty,
                Message = _message?.Trim() ?? string.Empty
            };
        }

        public static ScheduledBroadcastItem FromModel(ScheduledBroadcastMessage model)
        {
            return new ScheduledBroadcastItem
            {
                Enabled = model.Enabled,
                Time = model.Time,
                Message = model.Message
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class ModListItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public required string ModId { get; init; }

        public required string Version { get; init; }

        public required string FilePath { get; init; }

        public required string StatusText { get; init; }

        public bool IsDisabled { get; init; }

        public bool ModEnabled => !IsDisabled;

        public required string DependenciesText { get; init; }

        public required string IssuesText { get; init; }

        public string ToggleText => IsDisabled ? "启用" : "停用";

        public bool IsSelected
        {
            get => _isSelected;
            set => SetField(ref _isSelected, value);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static ModListItem FromModel(ModEntry model)
        {
            return new ModListItem
            {
                ModId = model.ModId,
                Version = model.Version,
                FilePath = model.FilePath,
                StatusText = model.Status,
                IsDisabled = model.IsDisabled,
                DependenciesText = model.DependenciesText,
                IssuesText = model.IssuesText
            };
        }

        public static ModEntry ToModel(ModListItem item)
        {
            return new ModEntry
            {
                ModId = item.ModId,
                Version = item.Version,
                FilePath = item.FilePath,
                Status = item.StatusText,
                IsDisabled = item.IsDisabled,
                Dependencies = [],
                DependencyIssues = []
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }
    }

    public sealed class AuthPlayerListItem
    {
        public required string PlayerUid { get; init; }

        public required string PlayerName { get; init; }

        public required string RegisteredAtText { get; init; }

        public required string RegisteredIp { get; init; }

        public required string LastLoginAtText { get; init; }

        public required string LastIp { get; init; }

        public required string PasswordStateText { get; init; }

        public required string DiscourseUsername { get; init; }

        public static AuthPlayerListItem FromModel(ServerAuthPlayerSummary model)
        {
            return new AuthPlayerListItem
            {
                PlayerUid = model.PlayerUid,
                PlayerName = model.PlayerName,
                RegisteredAtText = model.RegisteredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture),
                RegisteredIp = model.RegisteredIp,
                LastLoginAtText = model.LastLoginAtUtc?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "-",
                LastIp = model.LastIp,
                PasswordStateText = model.PasswordResetRequired
                    ? "重置待处理"
                    : model.HasPassword ? "已设置" : "未设置",
                DiscourseUsername = string.IsNullOrWhiteSpace(model.DiscourseUsername) ? "-" : model.DiscourseUsername
            };
        }
    }

    public sealed class ConfigSaveFileItem
    {
        public required string FullPath { get; init; }

        public required string FileName { get; init; }

        public static ConfigSaveFileItem FromSave(SaveFileEntry save)
        {
            return new ConfigSaveFileItem
            {
                FullPath = save.FullPath,
                FileName = save.FileName
            };
        }

        public static ConfigSaveFileItem FromPath(string path)
        {
            return new ConfigSaveFileItem
            {
                FullPath = path,
                FileName = string.IsNullOrWhiteSpace(Path.GetFileName(path)) ? path : Path.GetFileName(path)
            };
        }
    }

    public sealed class ConfigWorldRuleItem : INotifyPropertyChanged
    {
        private string _value;
        private ConfigChoiceOption? _selectedChoiceOption;
        private bool _canEdit = true;

        public ConfigWorldRuleItem(
            WorldRuleDefinition definition,
            string value,
            bool isChinese,
            IReadOnlyList<ConfigChoiceOption> choiceOptions)
        {
            Definition = definition;
            Key = definition.Key;
            Type = definition.Type;
            ChoiceOptions = choiceOptions;
            _value = value;
            SetLanguage(isChinese, choiceOptions);
            _selectedChoiceOption = ChoiceOptions.FirstOrDefault(option =>
                option.Value.Equals(value, StringComparison.OrdinalIgnoreCase));
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public WorldRuleDefinition Definition { get; }

        public string Key { get; }

        public WorldRuleType Type { get; }

        public string Label { get; private set; } = string.Empty;

        public string Description { get; private set; } = string.Empty;

        public bool HasDescription => !string.IsNullOrWhiteSpace(Description);

        public string BooleanLabel { get; private set; } = string.Empty;

        public IReadOnlyList<ConfigChoiceOption> ChoiceOptions { get; private set; }

        public bool IsOnlyDuringWorldCreate { get; init; }

        public bool IsBoolean => Type == WorldRuleType.Boolean;

        public bool IsChoice => Type == WorldRuleType.Choice;

        public bool IsText => Type is WorldRuleType.Text or WorldRuleType.Number;

        public bool CanEdit
        {
            get => _canEdit;
            set => SetField(ref _canEdit, value);
        }

        public string Value
        {
            get => _value;
            set
            {
                if (!SetField(ref _value, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(BoolValue));
            }
        }

        public bool BoolValue
        {
            get => bool.TryParse(Value, out var parsed) && parsed;
            set => Value = value ? bool.TrueString.ToLowerInvariant() : bool.FalseString.ToLowerInvariant();
        }

        public ConfigChoiceOption? SelectedChoiceOption
        {
            get => _selectedChoiceOption;
            set
            {
                if (!SetField(ref _selectedChoiceOption, value) || value is null)
                {
                    return;
                }

                Value = value.Value;
            }
        }

        public void SetLanguage(bool isChinese, IReadOnlyList<ConfigChoiceOption> choiceOptions)
        {
            var selectedValue = SelectedChoiceOption?.Value ?? Value;
            ChoiceOptions = choiceOptions;
            Label = isChinese ? Definition.LabelZh : Definition.LabelEn;
            Description = isChinese ? Definition.DescriptionZh ?? string.Empty : Definition.DescriptionEn ?? string.Empty;
            BooleanLabel = isChinese ? "启用" : "Enabled";
            _selectedChoiceOption = ChoiceOptions.FirstOrDefault(option =>
                option.Value.Equals(selectedValue, StringComparison.OrdinalIgnoreCase));
            OnPropertyChanged(nameof(ChoiceOptions));
            OnPropertyChanged(nameof(Label));
            OnPropertyChanged(nameof(Description));
            OnPropertyChanged(nameof(HasDescription));
            OnPropertyChanged(nameof(BooleanLabel));
            OnPropertyChanged(nameof(SelectedChoiceOption));
        }

        private bool SetField<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
