using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow : Window
{
    private const int RealtimeRangeSeconds = 60;
    private const int NetworkRangeCount = 144;
    private const int MaxConsoleLines = 800;
    private const double ConsoleAutoScrollThreshold = 12;
    private const int ConsoleRefreshDelayMs = 80;
    private const int ServerStartTimeoutSeconds = 30;
    private const int RunningServerLogReplayGraceSeconds = 5;
    private const int ConsoleProfileReplayLogBytes = 256 * 1024;
    private const int ConsoleProfileReplayLogLines = 220;
    private const double ChartWidth = 640;
    private const double ChartHeight = 248;
    private const double ThumbnailWidth = 76;
    private const double ThumbnailHeight = 50;
    private const double OsqEndpointHostColumnWidth = 420;
    private const double OsqEndpointTokenColumnWidth = 365;
    private const double OsqEndpointColumnSpacing = 10;
    private const string DefaultServerDownloadCatalogUrl = "https://api.vintagestory.at/stable-unstable.json";
    private const string GitHubContributorsApiUrl = "https://api.github.com/repos/vscn-studio/LauncherGo/contributors?per_page=100";
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
    private readonly IModRestrictionService _modRestrictionService;
    private readonly IFrpService _frpService;
    private readonly IThirdPartyFrpcService _thirdPartyFrpcService;
    private readonly IInstanceModService _instanceModService;
    private readonly IServerAuthService _serverAuthService;
    private readonly IServerMapService _serverMapService;
    private readonly ILogger<LauncherMainWindow> _logger;
    private readonly DispatcherTimer _dataTimer;
    private readonly DispatcherTimer _tickerTimer;
    private readonly DispatcherTimer _homeSloganTimer;
    private readonly DispatcherTimer _toastTimer;
    private readonly DateTimeOffset _windowStartedAtUtc = DateTimeOffset.UtcNow;

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
    private readonly ObservableCollection<ProfileConfigListItem> _automationConfigItems = [];
    private readonly ObservableCollection<RestrictionProfileConfigItem> _restrictionConfigItems = [];
    private readonly ObservableCollection<RestrictionModIdItem> _restrictionWhitelistItems = [];
    private readonly ObservableCollection<RestrictionModIdItem> _restrictionBlacklistItems = [];
    private readonly ObservableCollection<AutomationActionWindowItem> _automationActionWindowItems = [];
    private readonly ObservableCollection<AutomationTimeItem> _automationBackupTimeItems = [];
    private readonly ObservableCollection<ScheduledBroadcastItem> _automationBroadcastItems = [];
    private readonly ObservableCollection<ScheduledCommandItem> _automationCommandItems = [];
    private readonly ObservableCollection<AutomationTimeItem> _automationExportTimeItems = [];
    private readonly ObservableCollection<string> _automationRuntimeLogItems = [];
    private readonly ObservableCollection<InstanceProfile> _modProfileItems = [];
    private readonly ObservableCollection<ModListItem> _modItems = [];
    private readonly ObservableCollection<InstanceProfile> _authProfileItems = [];
    private readonly ObservableCollection<ProfileConfigListItem> _authConfigItems = [];
    private readonly ObservableCollection<AuthPlayerListItem> _authPlayerItems = [];
    private readonly ObservableCollection<RobotProfileBindingItem> _robotBindingItems = [];
    private readonly ObservableCollection<InstanceProfile> _robotProfileItems = [];
    private readonly ObservableCollection<OpenServerQueryProfileConfigItem> _openInfoConfigItems = [];
    private readonly ObservableCollection<DashboardServerItem> _dashboardServerItems = [];
    private readonly ObservableCollection<DashboardPlayerItem> _dashboardOnlinePlayerItems = [];
    private readonly ObservableCollection<DashboardUptimeItem> _dashboardUptimeItems = [];
    private readonly ObservableCollection<LaunchTargetItem> _launchTargetItems = [];
    private readonly ObservableCollection<InstanceProfile> _launchAddProfileItems = [];
    private readonly ObservableCollection<LaunchTargetItem> _settingsAutoStartTargetItems = [];
    private readonly ObservableCollection<InstanceProfile> _settingsAutoStartAddProfileItems = [];
    private readonly ObservableCollection<ConsoleServerItem> _consoleServerItems = [];
    private readonly List<ServerDownloadEntry> _catalogEntries = [];
    private readonly Dictionary<string, string> _configGameLanguageZh = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<string>> _consoleLinesByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _consoleReplayLoadedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _tailedProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _replayedLogProfileIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerCommonSettings> _dashboardSettingsByProfile = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dashboardSettingsLoadingProfileIds = new(StringComparer.OrdinalIgnoreCase);

    private MainTab _selectedTab = MainTab.Monitor;
    private HomeMetric _selectedMetric = HomeMetric.Server;
    private InstanceManageTab _selectedInstanceManageTab = InstanceManageTab.Profiles;
    private SettingsTab _selectedSettingsTab = SettingsTab.Server;
    private ConnectionTab _selectedConnectionTab = ConnectionTab.Frp;
    private bool _logsNavSelected;
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
    private bool _isConfigLoaded;
    private bool _isApplyingServerSettings;
    private bool _isApplyingNetworkSettings;
    private bool _isApplyingConnectionSettings;
    private bool _aboutMarkdownLoaded;
    private bool _contributorsLoaded;
    private bool _sponsorsLoaded;
    private bool _consoleAutoScroll = true;
    private bool _consoleRefreshQueued;
    private bool _isFrpRunning;
    private bool _isThirdPartyFrpcRunning;
    private bool _isTogglingFrp;
    private bool _isTogglingThirdPartyFrpc;
    private bool _isTogglingOsq;
    private bool _isTogglingRobot;
    private bool _isExitRequested;
    private bool _isRefreshingAutomation;
    private bool _isRefreshingRestriction;
    private bool _isRefreshingMods;
    private bool _isRefreshingAuth;
    private bool _toastPointerOver;
    private string _editingConfigProfileId = string.Empty;
    private string _pendingConfigLoadProfileId = string.Empty;
    private string _loadedConfigProfileId = string.Empty;
    private string _selectedConsoleProfileId = string.Empty;
    private string _editingAutomationProfileId = string.Empty;
    private string _editingRestrictionProfileId = string.Empty;
    private string _editingAuthProfileId = string.Empty;
    private string _editingOpenInfoProfileId = string.Empty;
    private long _configLoadVersion;
    private long _dashboardSettingsVersion;
    private TimeSpan _robotLastProcessorTime;
    private DateTimeOffset _robotLastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _robotLastCpuPercent;
    private string _configGameLanguageZhPath = string.Empty;
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
            ServiceLocator.GetRequiredService<IModRestrictionService>(),
            ServiceLocator.GetRequiredService<IFrpService>(),
            ServiceLocator.GetRequiredService<IThirdPartyFrpcService>(),
            ServiceLocator.GetRequiredService<IInstanceModService>(),
            ServiceLocator.GetRequiredService<IServerAuthService>(),
            ServiceLocator.GetRequiredService<IServerMapService>(),
            ServiceLocator.GetRequiredService<ILogger<LauncherMainWindow>>())
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
        IModRestrictionService modRestrictionService,
        IFrpService frpService,
        IThirdPartyFrpcService thirdPartyFrpcService,
        IInstanceModService instanceModService,
        IServerAuthService serverAuthService,
        IServerMapService serverMapService,
        ILogger<LauncherMainWindow>? logger = null)
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
        _modRestrictionService = modRestrictionService;
        _frpService = frpService;
        _thirdPartyFrpcService = thirdPartyFrpcService;
        _instanceModService = instanceModService;
        _serverAuthService = serverAuthService;
        _serverMapService = serverMapService;
        _logger = logger ?? NullLogger<LauncherMainWindow>.Instance;

        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _dataTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _dataTimer.Tick += OnDataTimerTick;

        _tickerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.3) };
        _tickerTimer.Tick += OnTickerTimerTick;

        _homeSloganTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.1) };
        _homeSloganTimer.Tick += OnHomeSloganTimerTick;

        _toastTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _toastTimer.Tick += OnToastTimerTick;

        _serverProcessService.OutputReceived += OnServerOutputReceived;
        _serverProcessService.ProfileOutputReceived += OnServerProfileOutputReceived;
        _serverProcessService.StatusChanged += OnServerStatusChanged;
        _logTailService.LogLineReceived += OnLogTailLineReceived;
        _logTailService.ProfileLogLineReceived += OnProfileLogTailLineReceived;
        _automationService.RuntimeLogReceived += OnAutomationRuntimeLogReceived;
        _frpService.StatusChanged += OnFrpStatusChanged;
        _thirdPartyFrpcService.StatusChanged += OnThirdPartyFrpcStatusChanged;
        _openServerQueryService.OutputReceived += OnOpenServerQueryOutputReceived;

        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
        InitializeCollections();
        InitializeSeries();
        RegisterAutoSaveHandlers();
        RefreshProfiles();
        _ = RefreshSavesAsync();
        _ = RefreshDownloadVersionsAsync(forceReload: false);

        SelectMetric(HomeMetric.Server);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
        SelectSettingsTab(SettingsTab.Server);
        SelectConnectionTab(ConnectionTab.Frp);
        SelectTab(MainTab.Monitor);

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
            _toastTimer.Stop();
            _serverProcessService.OutputReceived -= OnServerOutputReceived;
            _serverProcessService.ProfileOutputReceived -= OnServerProfileOutputReceived;
            _serverProcessService.StatusChanged -= OnServerStatusChanged;
            _logTailService.LogLineReceived -= OnLogTailLineReceived;
            _logTailService.ProfileLogLineReceived -= OnProfileLogTailLineReceived;
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
        if (preferences.AutoStartServerOnLaunch)
        {
            await StartConfiguredServerAsync(preferences);
        }

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

    private async Task StartConfiguredServerAsync(LauncherPreferences preferences)
    {
        try
        {
            var profileIds = SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
            if (profileIds.Count == 0)
            {
                profileIds = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
            }

            if (profileIds.Count == 0)
            {
                LaunchSelectionSummaryTextBlock.Text = T("未设置自启动服务器档案", "No auto-start server profile configured");
                return;
            }

            SetLaunchOperationBusy(T("启动中...", "Starting..."));
            try
            {
                foreach (var profileId in profileIds)
                {
                    var profile = _profileService.GetProfileById(profileId.Trim());
                    if (profile is null)
                    {
                        continue;
                    }

                    if (_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
                    {
                        continue;
                    }

                    var savePath = NormalizeFullPath(profile.ActiveSaveFile);

                    var reloadedProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
                    await StartServerProfileWithTimeoutAsync(reloadedProfile);
                }
            }
            finally
            {
                ClearLaunchOperationBusy();
            }
        }
        catch (Exception ex)
        {
            AppendConsoleLine(T($"[system] 自启动服务器失败：{ex.Message}", $"[system] Auto-start server failed: {ex.Message}"));
        }
    }

    private void InitializeStaticTexts()
    {
        MonitorNavButton.Content = T("仪表盘", "Dashboard");
        ConsoleNavButton.Content = T("控制台", "Console");
        LogsNavButton.Content = T("日志", "Logs");
        HomeSloganTextBlock.Text = T(HomeSlogans[0].Zh, HomeSlogans[0].En);

        LaunchActionTextBlock.Text = T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(LaunchStartIconData);
        CommandTextBox.PlaceholderText = T("输入服务器命令，回车发送", "Enter server command, press Enter to send");
        QuickCommandComboBox.PlaceholderText = T("快捷命令", "Quick command");
        SendCommandButton.Content = T("发送", "Send");

        DashboardPlayersTitleText.Text = T("在线玩家", "Online Players");
        DashboardPlayersHintText.Text = T("玩家名称", "Player Names");
        DashboardServerLineLegendText.Text = T("服务器", "Server");
        DashboardRobotLineLegendText.Text = T("QQ机器人", "QQ Robot");
        DashboardUptimeTitleText.Text = T("运行时间", "Uptime");

        ProfilesTabButton.Content = T("实例", "Instance");
        ConfigTabButton.Content = T("配置", "Config");
        SavesTabButton.Content = T("存档", "Saves");
        AutomationTabButton.Content = T("自动化", "Automation");
        RestrictionTabButton.Content = T("限制", "Restriction");
        ModsTabButton.Content = T("模组", "Mods");
        DownloadVersionsTabButton.Content = T("下载版本", "Downloads");
        DownloadVersionsNavButton.Content = T("下载版本", "Downloads");
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
        InitializeRestrictionStaticTexts();
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
        AutomationListRefreshButton.Content = T("刷新", "Refresh");
        AutomationClearButton.Content = T("清空", "Clear");
        AutomationBackButton.Content = T("返回", "Back");
        AutomationRestartEnabledLabelTextBlock.Text = T("启用定时开关服", "Enable scheduled start/stop");
        AutomationBackupEnabledLabelTextBlock.Text = T("启用定时备份", "Enable scheduled backup");
        AutomationBackupBeforeShutdownLabelTextBlock.Text = T("关服前备份", "Backup before shutdown");
        AutomationBroadcastEnabledLabelTextBlock.Text = T("启用定时广播", "Enable scheduled broadcast");
        AutomationCommandEnabledLabelTextBlock.Text = T("启用定时命令", "Enable scheduled commands");
        AutomationExportEnabledLabelTextBlock.Text = T("启用日志导出", "Enable log export");
        AutomationExportBeforeShutdownLabelTextBlock.Text = T("关服前导出日志", "Export before shutdown");
        AutomationExportIncludeChatLabelTextBlock.Text = T("导出聊天", "Export chat");
        AutomationExportIncludeServerLabelTextBlock.Text = T("导出服务端信息", "Export server info");
        AutomationActionTitleTextBlock.Text = T("定时开关服", "Scheduled Start/Stop");
        AutomationAddActionButton.Content = T("添加", "Add");
        AutomationAddBackupTimeButton.Content = T("添加", "Add");
        AutomationAddExportTimeButton.Content = T("添加", "Add");
        AutomationAddBroadcastButton.Content = T("添加", "Add");
        AutomationAddCommandButton.Content = T("添加", "Add");
    }

    private void InitializeRestrictionStaticTexts()
    {
        RestrictionListRefreshButton.Content = T("刷新", "Refresh");
        RestrictionBackButton.Content = T("返回", "Back");
        RestrictionSaveButton.Content = T("保存", "Save");
        RestrictionRefreshButton.Content = T("刷新", "Refresh");
        RestrictionBlacklistEnabledLabelTextBlock.Text = T("启用黑名单", "Enable blacklist");
        RestrictionForceWhitelistLabelTextBlock.Text = T("强制白名单", "Force whitelist");
        RestrictionWhitelistTitleTextBlock.Text = T("白名单模组", "Whitelisted mods");
        RestrictionBlacklistTitleTextBlock.Text = T("黑名单模组", "Blacklisted mods");
        RestrictionWhitelistInputTextBox.PlaceholderText = T("输入 Mod ID", "Enter Mod ID");
        RestrictionBlacklistInputTextBox.PlaceholderText = T("输入 Mod ID", "Enter Mod ID");
        RestrictionAddWhitelistButton.Content = T("添加", "Add");
        RestrictionAddBlacklistButton.Content = T("添加", "Add");
    }

    private void InitializeModStaticTexts()
    {
        ModZipPathTextBox.PlaceholderText = T("Mod ZIP 路径", "Mod ZIP path");
        BrowseModZipButton.Content = T("浏览", "Browse");
        ImportModZipButton.Content = T("导入", "Import");
        DeployMapModButton.Content = T("部署地图模组", "Deploy Map Mod");
        DeleteSelectedModsButton.Content = T("删除", "Delete");
        RefreshModsButton.Content = T("刷新", "Refresh");
    }

    private void InitializeConfigStaticTexts()
    {
        ConfigBackButton.Content = T("返回", "Back");
        ConfigRefreshButton.Content = T("刷新", "Refresh");
        ConfigImportButton.Content = T("导入", "Import");
        ConfigSaveButton.Content = T("保存", "Save");
        ConfigPathTextBlock.Text = T("配置路径：未选择档案", "Config path: no profile selected");
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
        SettingsAutoStartServerLabelTextBlock.Text = T("启动时自动启动服务器", "Auto-start server on launch");
        SettingsAutoStartServerProfileLabelTextBlock.Text = T("自启动服务器档案", "Auto-start server profile");
        SettingsAutoStartAddProfileComboBox.PlaceholderText = T("添加自启动服务器", "Add auto-start server");
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
        ConnectionFrpTabButton.Content = T("FRP", "FRP");
        ConnectionOpenInfoTabButton.Content = T("开放API", "Open API");
        ConnectionRobotTabButton.Content = T("机器人", "Robot");
        ConnectionAuthTabButton.Content = T("安全", "Security");

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
        OsqAllowInsecureHttpLabelTextBlock.Text = T("允许 HTTP 外发", "Allow HTTP outbound");
        OsqListenPrefixLabelTextBlock.Text = T("监听地址", "Listen Prefix");
        OsqRequestTimeoutLabelTextBlock.Text = T("请求超时秒数", "Request Timeout Seconds");
        OsqIncludeServerInfoLabelTextBlock.Text = T("服务器信息", "Server Info");
        OsqIncludePlayersLabelTextBlock.Text = T("玩家列表", "Players");
        OsqIncludeEventsLabelTextBlock.Text = T("玩家事件", "Player Events");
        OsqIncludeChatsLabelTextBlock.Text = T("聊天", "Chats");
        OsqIncludeNotificationsLabelTextBlock.Text = T("通知", "Notifications");
        OsqIncludeMapLabelTextBlock.Text = T("地图数据", "Map Data");
        OsqBackButton.Content = T("返回", "Back");
        OsqConfigSaveButton.Content = T("保存", "Save");
        OsqConfigRefreshButton.Content = T("刷新", "Refresh");
        DeployMapModButton.Content = T("部署地图模组", "Deploy Map Mod");

        UpdateRobotToggleButtonText();
        RobotConfigTitleTextBlock.Text = T("QQ机器人配置", "QQ Robot Configuration");
        RobotOneBotLabelTextBlock.Text = T("OneBot WebSocket", "OneBot WebSocket");
        RobotAccessTokenLabelTextBlock.Text = T("访问令牌", "Access Token");
        RobotBoundGroupsLabelTextBlock.Text = T("绑定群号", "Bound Group IDs");
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
        RobotClearButton.Content = T("清空", "Clear");
        RobotRefreshButton.Content = T("刷新", "Refresh");
        RobotBindingAddButton.Content = T("添加", "Add");
        AuthSaveButton.Content = T("保存", "Save");
        AuthRefreshButton.Content = T("刷新", "Refresh");
        AuthClearButton.Content = T("清空", "Clear");
        AuthBackButton.Content = T("返回", "Back");
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
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private void InitializeCollections()
    {
        ConsoleOutputTextBlock.Text = string.Empty;
        _consoleAutoScroll = true;
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
        AutomationConfigItemsControl.ItemsSource = _automationConfigItems;
        AutomationProfileComboBox.ItemsSource = _automationProfileItems;
        AutomationActionsItemsControl.ItemsSource = _automationActionWindowItems;
        AutomationBackupTimesItemsControl.ItemsSource = _automationBackupTimeItems;
        AutomationBroadcastsItemsControl.ItemsSource = _automationBroadcastItems;
        AutomationCommandsItemsControl.ItemsSource = _automationCommandItems;
        AutomationExportTimesItemsControl.ItemsSource = _automationExportTimeItems;
        AutomationRuntimeLogsListBox.ItemsSource = _automationRuntimeLogItems;
        RestrictionConfigItemsControl.ItemsSource = _restrictionConfigItems;
        RestrictionWhitelistItemsControl.ItemsSource = _restrictionWhitelistItems;
        RestrictionBlacklistItemsControl.ItemsSource = _restrictionBlacklistItems;
        ModProfileComboBox.ItemsSource = _modProfileItems;
        ModsListBox.ItemsSource = _modItems;
        RobotBindingsItemsControl.ItemsSource = _robotBindingItems;
        OpenInfoConfigItemsControl.ItemsSource = _openInfoConfigItems;
        AuthConfigItemsControl.ItemsSource = _authConfigItems;
        AuthProfileComboBox.ItemsSource = _authProfileItems;
        AuthPlayersListBox.ItemsSource = _authPlayerItems;
        DashboardServersItemsControl.ItemsSource = _dashboardServerItems;
        DashboardOnlinePlayersItemsControl.ItemsSource = _dashboardOnlinePlayerItems;
        DashboardUptimeItemsControl.ItemsSource = _dashboardUptimeItems;
        LaunchTargetsItemsControl.ItemsSource = _launchTargetItems;
        LaunchAddProfileComboBox.ItemsSource = _launchAddProfileItems;
        SettingsAutoStartTargetsItemsControl.ItemsSource = _settingsAutoStartTargetItems;
        SettingsAutoStartAddProfileComboBox.ItemsSource = _settingsAutoStartAddProfileItems;
        ConsoleServerComboBox.ItemsSource = _consoleServerItems;
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
        var statuses = _serverProcessService.GetCachedStatuses();
        var status = statuses.FirstOrDefault(s => s.IsRunning) ?? _serverProcessService.GetCachedStatus();
        var robotStatus = _robotService.GetCurrentStatus();
        var robotResources = SampleRobotResources(robotStatus);
        var totalServerCpu = statuses.Where(s => s.IsRunning).Sum(s => s.CpuPercent);
        var totalServerMemoryBytes = statuses
            .Where(s => s.IsRunning)
            .Sum(s => ResolveProcessMemory(s.ProcessId) ?? s.MemoryBytes);
        PushNextSample(_serverCpuSamples, Math.Clamp(totalServerCpu, 0, 100));
        PushNextSample(_serverMemoryMbSamples, BytesToMb(totalServerMemoryBytes));
        PushNextSample(_playersSamples, statuses.Where(s => s.IsRunning).Sum(s => s.OnlinePlayers));

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
        UpdateMultiServerDashboard(statuses);
        RefreshConsoleServerItems(statuses);

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

    private void ShowToast(string message, ToastKind? kind = null)
    {
        var text = NormalizeToastMessage(message);
        if (string.IsNullOrWhiteSpace(text))
            return;

        var resolvedKind = kind ?? InferToastKind(text);
        _logger.LogInformation("Toast[{Kind}]: {Message}", resolvedKind, text);
        ToastMessageTextBlock.Text = text;
        ToastAccentBorder.Background = GetToastAccentBrush(resolvedKind);
        ToastHost.IsVisible = true;
        RestartToastTimer();
    }

    private void RestartToastTimer()
    {
        _toastTimer.Stop();
        if (!_toastPointerOver && ToastHost.IsVisible)
        {
            _toastTimer.Start();
        }
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        ToastHost.IsVisible = false;
    }

    private void OnToastTimerTick(object? sender, EventArgs e)
    {
        if (_toastPointerOver)
            return;

        HideToast();
    }

    private void OnToastPointerEntered(object? sender, PointerEventArgs e)
    {
        _toastPointerOver = true;
        _toastTimer.Stop();
    }

    private void OnToastPointerExited(object? sender, PointerEventArgs e)
    {
        _toastPointerOver = false;
        RestartToastTimer();
    }

    private void OnToastCloseClick(object? sender, RoutedEventArgs e)
    {
        HideToast();
    }

    private static string NormalizeToastMessage(string message)
    {
        var text = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (text.StartsWith("[system]", StringComparison.OrdinalIgnoreCase))
        {
            text = text[8..].Trim();
        }

        return text;
    }

    private static ToastKind InferToastKind(string text)
    {
        if (text.Contains("失败", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("错误", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("异常", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("error", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Error;
        }

        if (text.Contains("未启动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("未启用", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("已停止", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("跳过", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("stopped", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("disabled", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("skipped", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Neutral;
        }

        if (text.Contains("已", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("完成", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("成功", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("启动", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("运行中", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("saved", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("started", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("running", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("deployed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("imported", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("deleted", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return ToastKind.Success;
        }

        return ToastKind.Neutral;
    }

    private static IBrush GetToastAccentBrush(ToastKind kind)
    {
        return new SolidColorBrush(kind switch
        {
            ToastKind.Success => Color.Parse("#6B8E23"),
            ToastKind.Error => Color.Parse("#C62828"),
            _ => Color.Parse("#555555")
        });
    }

    private void UpdateCardValues(ServerRuntimeStatus status)
    {
        var serverCpu = _serverCpuSamples[^1];
        var serverMemMb = _serverMemoryMbSamples[^1];

        var robotStatus = _robotService.GetCurrentStatus();
        var robotCpu = _robotCpuSamples[^1];
        var robotMemMb = _robotMemoryMbSamples[^1];
        UpdateDashboard(status, robotStatus, serverCpu, serverMemMb, robotCpu, robotMemMb);

        var statuses = _serverProcessService.GetCachedStatuses();
        var hasRunningServer = statuses.Any(static current => current.IsRunning);
        var hasPendingLaunchTargets = HasPendingLaunchTargets(statuses);
        var stopMode = hasRunningServer && !hasPendingLaunchTargets;
        LaunchActionTextBlock.Text = stopMode ? T("停止服务器", "Stop Server") : T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(stopMode ? LaunchStopIconData : LaunchStartIconData);
        LaunchServerButton.Classes.Set("running", stopMode);
        RefreshLaunchButtonSummary();
    }

    private void UpdateDashboard(
        ServerRuntimeStatus status,
        RobotRuntimeStatus robotStatus,
        double serverCpu,
        double serverMemMb,
        double robotCpu,
        double robotMemMb)
    {
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateMultiServerDashboard(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var statusByProfileId = statuses
            .Where(static status => !string.IsNullOrWhiteSpace(status.ProfileId))
            .GroupBy(status => status.ProfileId!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var profiles = _profileService.GetProfiles()
            .Select(profile =>
            {
                var profileId = profile.Id.Trim();
                var isRunning = statusByProfileId.TryGetValue(profileId, out var status) && status.IsRunning;
                var displayName = string.IsNullOrWhiteSpace(profile.Name) ? profileId : profile.Name;
                return (Profile: profile, ProfileId: profileId, IsRunning: isRunning, DisplayName: displayName);
            })
            .OrderByDescending(static item => item.IsRunning)
            .ThenBy(static item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var desiredItems = new List<DashboardServerItem>();
        foreach (var (profile, profileId, _, displayName) in profiles)
        {
            EnsureDashboardSettings(profile);

            _dashboardSettingsByProfile.TryGetValue(profileId, out var settings);
            var hasStatus = statusByProfileId.TryGetValue(profileId, out var status);
            var isRunning = hasStatus && status is not null && status.IsRunning;
            var isLoading = _isStoppingOrStarting;
            var cpuPercent = isRunning ? status!.CpuPercent : 0;
            var memoryMb = isRunning ? BytesToMb(status!.MemoryBytes) : 0;
            var item = _dashboardServerItems.FirstOrDefault(existing =>
                           existing.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
                       ?? new DashboardServerItem { ProfileId = profileId };
            item.ProfileName = displayName;
            item.Version = string.IsNullOrWhiteSpace(profile.Version) ? "--" : profile.Version;
            item.IsRunning = isRunning;
            item.IsActionEnabled = !isLoading;
            item.ActionText = isRunning ? T("停止", "Stop") : T("启动", "Start");
            item.StatusText = isLoading
                ? T("加载中", "Loading")
                : isRunning ? T("正在运行", "Running") : T("已停止", "Stopped");
            item.StatusBrush = new SolidColorBrush(Color.Parse(isLoading ? "#F59E0B" : isRunning ? "#16A34A" : "#DC2626"));
            item.SummaryText = T(
                $"端口 {settings?.Port.ToString(CultureInfo.InvariantCulture) ?? "--"}  CPU {cpuPercent:F1}%  内存 {memoryMb:F0} MB",
                $"Port {settings?.Port.ToString(CultureInfo.InvariantCulture) ?? "--"}  CPU {cpuPercent:F1}%  Mem {memoryMb:F0} MB");
            desiredItems.Add(item);
        }

        if (desiredItems.Count == 0)
        {
            var emptyItem = _dashboardServerItems.FirstOrDefault(static item => string.IsNullOrWhiteSpace(item.ProfileId))
                            ?? new DashboardServerItem();
            emptyItem.ProfileName = T("暂无服务器档案", "No server profiles");
            emptyItem.Version = "--";
            emptyItem.IsRunning = false;
            emptyItem.IsActionEnabled = false;
            emptyItem.ActionText = T("启动", "Start");
            emptyItem.StatusText = T("已停止", "Stopped");
            emptyItem.StatusBrush = new SolidColorBrush(Color.Parse("#DC2626"));
            emptyItem.SummaryText = T("请先在实例页面创建档案。", "Create a profile from the instance page first.");
            desiredItems.Add(emptyItem);
        }

        SynchronizeDashboardServerItems(desiredItems);

        var players = _serverProcessService.GetOnlinePlayers();
        _dashboardOnlinePlayerItems.Clear();
        foreach (var player in players)
        {
            _dashboardOnlinePlayerItems.Add(DashboardPlayerItem.FromModel(player));
        }

        if (_dashboardOnlinePlayerItems.Count == 0)
        {
            _dashboardOnlinePlayerItems.Add(new DashboardPlayerItem
            {
                PlayerName = T("暂无在线玩家", "No online players"),
                ProfileName = "--",
                JoinedAtText = "--"
            });
        }

        var maxPlayers = runningStatuses
            .Select(status =>
            {
                var id = status.ProfileId ?? string.Empty;
                return _dashboardSettingsByProfile.TryGetValue(id, out var settings) ? settings.MaxClients : 0;
            })
            .Sum();
        DashboardPlayersCountText.Text = $"{players.Count.ToString(CultureInfo.InvariantCulture)}/{(maxPlayers > 0 ? maxPlayers.ToString(CultureInfo.InvariantCulture) : "--")}";

        UpdateDashboardUptimeItems(runningStatuses);
    }

    private void SynchronizeDashboardServerItems(IReadOnlyList<DashboardServerItem> desiredItems)
    {
        for (var index = _dashboardServerItems.Count - 1; index >= 0; index--)
        {
            var existing = _dashboardServerItems[index];
            if (!desiredItems.Any(item => ReferenceEquals(item, existing)))
            {
                _dashboardServerItems.RemoveAt(index);
            }
        }

        for (var desiredIndex = 0; desiredIndex < desiredItems.Count; desiredIndex++)
        {
            var desiredItem = desiredItems[desiredIndex];
            var currentIndex = _dashboardServerItems.IndexOf(desiredItem);
            if (currentIndex < 0)
            {
                _dashboardServerItems.Insert(desiredIndex, desiredItem);
                continue;
            }

            if (currentIndex != desiredIndex)
            {
                _dashboardServerItems.Move(currentIndex, desiredIndex);
            }
        }
    }

    private void EnsureDashboardSettings(InstanceProfile profile)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId) ||
            _dashboardSettingsByProfile.ContainsKey(profileId) ||
            _dashboardSettingsLoadingProfileIds.Contains(profileId))
        {
            return;
        }

        _dashboardSettingsLoadingProfileIds.Add(profileId);
        var requestVersion = _dashboardSettingsVersion;
        _ = RefreshDashboardSettingsAsync(profile, requestVersion);
    }

    private async Task RefreshDashboardSettingsAsync(InstanceProfile profile, long requestVersion)
    {
        var profileId = profile.Id.Trim();
        try
        {
            var settings = await _instanceServerConfigService.LoadServerSettingsAsync(profile);
            Dispatcher.UIThread.Post(() =>
            {
                _dashboardSettingsLoadingProfileIds.Remove(profileId);
                if (requestVersion == _dashboardSettingsVersion)
                {
                    _dashboardSettingsByProfile[profileId] = settings;
                }

                UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
            });
        }
        catch (Exception ex)
        {
            Dispatcher.UIThread.Post(() => _dashboardSettingsLoadingProfileIds.Remove(profileId));
            _logger.LogDebug(ex, "Failed to load dashboard settings for profile {ProfileId}", profile.Id);
        }
    }

    private void UpdateDashboardSettingsCache(InstanceProfile profile, ServerCommonSettings settings)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _dashboardSettingsVersion++;
        _dashboardSettingsLoadingProfileIds.Remove(profileId);
        _dashboardSettingsByProfile[profileId] = settings;
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void InvalidateDashboardSettingsCache(InstanceProfile profile)
    {
        var profileId = profile.Id.Trim();
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        _dashboardSettingsVersion++;
        _dashboardSettingsByProfile.Remove(profileId);
        _dashboardSettingsLoadingProfileIds.Remove(profileId);
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateDashboardUptimeItems(IReadOnlyList<ServerRuntimeStatus> runningStatuses)
    {
        _dashboardUptimeItems.Clear();
        foreach (var status in runningStatuses)
        {
            var profile = ResolveDashboardProfile(status);
            _dashboardUptimeItems.Add(new DashboardUptimeItem
            {
                Name = string.IsNullOrWhiteSpace(profile?.Name) ? status.ProfileId ?? T("服务器", "Server") : profile.Name,
                UptimeText = FormatConnectionUptime(status.StartedAtUtc)
            });
        }

        var robotStatus = _robotService.GetCurrentStatus();
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("QQ机器人", "QQ Robot"),
            UptimeText = robotStatus.IsRunning ? FormatConnectionUptime(robotStatus.StartedAtUtc) : "--"
        });

        var openInfoStatus = _openServerQueryService.GetRuntimeStatus();
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("开放API", "Open API"),
            UptimeText = openInfoStatus.IsListening ? FormatConnectionUptime(ParseRuntimeStartedAtUtc(openInfoStatus.StartedAtUtc)) : "--"
        });

        var frpStatus = _frpService.GetCurrentStatus();
        var thirdPartyStatus = _thirdPartyFrpcService.GetCurrentStatus();
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("FRP", "FRP"),
            UptimeText = frpStatus.IsRunning ? FormatConnectionUptime(frpStatus.StartedAtUtc) : "--"
        });
        _dashboardUptimeItems.Add(new DashboardUptimeItem
        {
            Name = T("第三方FRP", "Third-party FRP"),
            UptimeText = thirdPartyStatus.IsRunning ? FormatConnectionUptime(thirdPartyStatus.StartedAtUtc) : "--"
        });
    }

    private void RefreshConsoleServerItems(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var previousSelected = _selectedConsoleProfileId;
        _consoleServerItems.Clear();
        foreach (var status in runningStatuses)
        {
            var profile = ResolveDashboardProfile(status);
            var profileId = status.ProfileId ?? profile?.Id ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                continue;
            }

            _consoleServerItems.Add(new ConsoleServerItem
            {
                ProfileId = profileId,
                DisplayName = string.IsNullOrWhiteSpace(profile?.Name) ? profileId : profile.Name
            });
        }

        var selected = _consoleServerItems.FirstOrDefault(item =>
                           !string.IsNullOrWhiteSpace(previousSelected) &&
                           item.ProfileId.Equals(previousSelected, StringComparison.OrdinalIgnoreCase))
                       ?? _consoleServerItems.FirstOrDefault();
        if (selected is null)
        {
            _selectedConsoleProfileId = string.Empty;
            ConsoleServerComboBox.SelectedIndex = -1;
            RefreshConsoleText();
            return;
        }

        if (!selected.ProfileId.Equals(_selectedConsoleProfileId, StringComparison.OrdinalIgnoreCase))
        {
            _selectedConsoleProfileId = selected.ProfileId;
            RefreshConsoleText();
        }

        if (!ReferenceEquals(ConsoleServerComboBox.SelectedItem, selected))
        {
            ConsoleServerComboBox.SelectedItem = selected;
        }

        var selectedProfileId = selected.ProfileId;
        _ = EnsureConsoleReplayLoadedAsync(selectedProfileId);
    }

    private InstanceProfile? ResolveDashboardProfile(ServerRuntimeStatus status)
    {
        if (!string.IsNullOrWhiteSpace(status.ProfileId))
        {
            var runningProfile = _profileService.GetProfileById(status.ProfileId.Trim());
            if (runningProfile is not null)
            {
                return runningProfile;
            }
        }

        var preferences = _preferencesService.Load();
        var defaultProfileId = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(defaultProfileId))
        {
            var defaultProfile = _profileService.GetProfileById(defaultProfileId);
            if (defaultProfile is not null)
            {
                return defaultProfile;
            }
        }

        return _profileService.GetProfiles().FirstOrDefault();
    }

    private void UpdateDashboardStatus(ServerRuntimeStatus status)
    {
        UpdateMultiServerDashboard(_serverProcessService.GetCachedStatuses());
    }

    private void UpdateDashboardUptimes(ServerRuntimeStatus status, RobotRuntimeStatus robotStatus)
    {
        UpdateDashboardUptimeItems(_serverProcessService.GetCachedStatuses().Where(static s => s.IsRunning).ToList());
    }

    private static DateTimeOffset? ParseRuntimeStartedAtUtc(string value)
    {
        return DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var startedAt)
            ? startedAt
            : null;
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
        var status = statusOverride ?? _serverProcessService.GetCachedStatus();
        RenderDashboardResourceChart(status);
    }

    private void RenderDashboardResourceChart(ServerRuntimeStatus status)
    {
        var robotStatus = _robotService.GetCurrentStatus();
        var hasRunningServer = _serverProcessService.GetCachedStatuses().Any(static current => current.IsRunning);
        var serverCpu = _serverCpuSamples[^1];
        var robotCpu = _robotCpuSamples[^1];
        var serverMemoryMb = _serverMemoryMbSamples[^1];
        var robotMemoryMb = _robotMemoryMbSamples[^1];
        var yMax = Math.Max(
            GetMemoryChartYMax(_serverMemoryMbSamples),
            GetMemoryChartYMax(_robotMemoryMbSamples));

        RenderDualLineChart(
            title: T("资源监控", "Resource Monitor"),
            topValue: T($"{serverMemoryMb:F0} MB / {robotMemoryMb:F0} MB", $"{serverMemoryMb:F0} MB / {robotMemoryMb:F0} MB"),
            summary: T("60 秒区间，蓝线为服务器总内存占用，绿线为 QQ 机器人内存占用。", "60-second range. Blue is total server memory usage; green is QQ robot memory usage."),
            primary: _serverMemoryMbSamples,
            secondary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            details:
            [
                (T("服务器总 CPU", "Total Server CPU"), hasRunningServer ? $"{serverCpu:F1}%" : "--"),
                (T("服务器总内存", "Total Server Memory"), hasRunningServer ? $"{serverMemoryMb:F0} MB" : "--"),
                (T("机器人 CPU", "Robot CPU"), robotStatus.IsRunning ? $"{robotCpu:F1}%" : "--"),
                (T("机器人内存", "Robot Memory"), robotStatus.IsRunning ? $"{robotMemoryMb:F0} MB" : "--")
            ]);
    }

    private void RenderServerChart(ServerRuntimeStatus status)
    {
        var cpu = _serverCpuSamples[^1];
        var memoryMb = _serverMemoryMbSamples[^1];
        var yMax = GetMemoryChartYMax(_serverMemoryMbSamples);
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderSingleLineChart(
            title: T("服务器状态", "Server Status"),
            topValue: status.IsRunning ? $"{memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为服务端进程内存 MB；CPU 仅在详情展示。", "60-second range. Blue is server process memory MB; CPU is shown in details only."),
            primary: _serverMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: false,
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
        var yMax = GetMemoryChartYMax(_robotMemoryMbSamples);
        var uptime = status.StartedAtUtc.HasValue
            ? FormatDuration(DateTimeOffset.UtcNow - status.StartedAtUtc.Value)
            : "--";

        RenderSingleLineChart(
            title: T("机器人状态", "Robot Status"),
            topValue: status.IsRunning ? $"{memoryMb:F0} MB" : T("未启动", "Stopped"),
            summary: T("60 秒区间，蓝线为 QQ 机器人内存 MB；CPU 仅在详情展示。", "60-second range. Blue is QQ robot memory MB; CPU is shown in details only."),
            primary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: yMax,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            showTicker: false,
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
    }

    private static double GetMemoryChartYMax(IReadOnlyList<double> memoryMbSamples)
    {
        return NiceCeiling(Math.Max(1, memoryMbSamples.Max()));
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
        _ = RefreshRestrictionAsync();
        _ = RefreshModsAsync();
        _ = RefreshAuthProfilesAsync();
    }

    private void RefreshLaunchOptions(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        RefreshLaunchTargetItems(profiles ?? _profileService.GetProfiles());
        RefreshLaunchButtonSummary();
    }

    private void RefreshLaunchTargetItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedIds = LoadLaunchProfileIds();
        _launchTargetItems.Clear();
        foreach (var profile in profileList.Where(profile => selectedIds.Contains(profile.Id)))
        {
            _launchTargetItems.Add(new LaunchTargetItem
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name
            });
        }

        _launchAddProfileItems.Clear();
        foreach (var profile in profileList.Where(profile => !selectedIds.Contains(profile.Id)))
        {
            _launchAddProfileItems.Add(profile);
        }
    }

    private HashSet<string> LoadLaunchProfileIds()
    {
        var preferences = _preferencesService.Load();
        var ids = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
        return ids.Count > 0 ? ids : [];
    }

    private string GetPrimaryLaunchProfileId()
    {
        var preferences = _preferencesService.Load();
        return SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault() ?? string.Empty;
    }

    private static HashSet<string> SplitProfileIds(string value)
    {
        return value
            .Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static HashSet<string> SplitProfileIds(IEnumerable<string>? values, string legacyValue = "")
    {
        var result = SplitProfileIds(legacyValue);
        foreach (var value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result;
    }

    private void SaveLaunchProfileIds(IEnumerable<string> profileIds)
    {
        var preferences = _preferencesService.Load();
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        preferences.DefaultLaunchProfileIds = ids;
        preferences.DefaultLaunchProfileId = string.Join(';', ids);
        _preferencesService.Save(preferences);
        RefreshLaunchTargetItems();
        RefreshLaunchButtonSummary();
    }

    private HashSet<string> LoadAutoStartProfileIds()
    {
        var preferences = _preferencesService.Load();
        return SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
    }

    private void SaveAutoStartProfileIds(IEnumerable<string> profileIds)
    {
        var preferences = _preferencesService.Load();
        var ids = profileIds.Where(id => !string.IsNullOrWhiteSpace(id)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        preferences.AutoStartServerProfileIds = ids;
        preferences.AutoStartServerProfileId = string.Join(';', ids);
        _preferencesService.Save(preferences);
        RefreshSettingsAutoStartTargetItems();
    }

    private void RefreshSettingsAutoStartTargetItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedIds = LoadAutoStartProfileIds();
        _settingsAutoStartTargetItems.Clear();
        foreach (var profile in profileList.Where(profile => selectedIds.Contains(profile.Id)))
        {
            _settingsAutoStartTargetItems.Add(new LaunchTargetItem
            {
                ProfileId = profile.Id,
                DisplayName = profile.Name
            });
        }

        _settingsAutoStartAddProfileItems.Clear();
        foreach (var profile in profileList.Where(profile => !selectedIds.Contains(profile.Id)))
        {
            _settingsAutoStartAddProfileItems.Add(profile);
        }
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
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingAutomationProfileId)
                ? _editingAutomationProfileId
                : AutomationProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                    ? selectedProfile.Id
                    : string.Empty;
            _automationProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _automationProfileItems.Add(profile);
            }

            RefreshAutomationConfigItems(profiles);
            AutomationProfileComboBox.ItemsSource = _automationProfileItems;
            if (_automationProfileItems.Count > 0)
            {
                var target = _automationProfileItems.FirstOrDefault(profile =>
                    !string.IsNullOrWhiteSpace(selectedProfileId) &&
                    profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                    ?? _automationProfileItems.FirstOrDefault(profile =>
                        SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).Contains(profile.Id))
                    ?? _automationProfileItems.FirstOrDefault();
                AutomationProfileComboBox.SelectedItem = target;
            }

            if (AutomationEditorPanel.IsVisible &&
                AutomationProfileComboBox.SelectedItem is InstanceProfile editorProfile)
            {
                var settings = await _automationSettingsService.LoadAsync(editorProfile);
                ApplyAutomationSettings(settings);
            }
            SetAutomationStatus(T("自动化配置已加载。", "Automation settings loaded."), notify: false);
            await SyncAutomationRuntimeLogsAsync();
        }
        catch (Exception ex)
        {
            SetAutomationStatus(T($"自动化加载失败：{ex.Message}", $"Automation load failed: {ex.Message}"));
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
        AutomationCommandEnabledCheckBox.IsChecked = settings.CommandEnabled;
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

        _automationCommandItems.Clear();
        foreach (var command in settings.ScheduledCommands ?? [])
        {
            _automationCommandItems.Add(ScheduledCommandItem.FromModel(command));
        }
        if (_automationCommandItems.Count == 0)
        {
            _automationCommandItems.Add(new ScheduledCommandItem());
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
            CommandEnabled = AutomationCommandEnabledCheckBox.IsChecked == true,
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
            ScheduledCommands = _automationCommandItems
                .Select(item => item.ToModel())
                .Where(item => !string.IsNullOrWhiteSpace(item.Command) || !string.IsNullOrWhiteSpace(item.Time))
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
            if (AutomationProfileComboBox.SelectedItem is not InstanceProfile profile)
            {
                SetAutomationStatus(T("请先选择档案。", "Select a profile first."));
                return;
            }

            var settings = CollectAutomationSettings();
            await _automationSettingsService.SaveAsync(profile, settings);
            await _automationService.ReloadAsync();
            RefreshAutomationConfigItems();
            SetAutomationStatus(T("自动化配置已保存。", "Automation settings saved."));
        }
        catch (Exception ex)
        {
            SetAutomationStatus(T($"自动化保存失败：{ex.Message}", $"Automation save failed: {ex.Message}"));
        }
    }

    private async Task RefreshRestrictionAsync()
    {
        if (_isRefreshingRestriction)
        {
            return;
        }

        _isRefreshingRestriction = true;
        try
        {
            var profiles = _profileService.GetProfiles();
            _restrictionConfigItems.Clear();
            foreach (var profile in profiles)
            {
                var settings = await _modRestrictionService.LoadAsync(profile);
                _restrictionConfigItems.Add(RestrictionProfileConfigItem.FromSettings(
                    profile,
                    settings,
                    _modRestrictionService.GetSettingsPath(profile),
                    _isChinese));
            }

            if (RestrictionEditorPanel.IsVisible &&
                !string.IsNullOrWhiteSpace(_editingRestrictionProfileId))
            {
                var editingProfile = _profileService.GetProfileById(_editingRestrictionProfileId);
                if (editingProfile is not null)
                {
                    ApplyRestrictionSettings(await _modRestrictionService.LoadAsync(editingProfile));
                    RestrictionProfileNameTextBlock.Text = editingProfile.Name;
                }
            }

            SetRestrictionStatus(T("限制配置已加载。", "Restriction settings loaded."), notify: false);
        }
        catch (Exception ex)
        {
            SetRestrictionStatus(T($"限制配置加载失败：{ex.Message}", $"Failed to load restrictions: {ex.Message}"));
        }
        finally
        {
            _isRefreshingRestriction = false;
        }
    }

    private void ApplyRestrictionSettings(ModRestrictionSettings settings)
    {
        RestrictionBlacklistEnabledCheckBox.IsChecked = settings.BlacklistEnabled;
        RestrictionForceWhitelistCheckBox.IsChecked = settings.ForceWhitelistEnabled;

        _restrictionWhitelistItems.Clear();
        foreach (var modId in settings.WhitelistModIds)
        {
            _restrictionWhitelistItems.Add(new RestrictionModIdItem(modId));
        }

        _restrictionBlacklistItems.Clear();
        foreach (var modId in settings.BlacklistModIds)
        {
            _restrictionBlacklistItems.Add(new RestrictionModIdItem(modId));
        }
    }

    private ModRestrictionSettings CollectRestrictionSettings()
    {
        return new ModRestrictionSettings
        {
            BlacklistEnabled = RestrictionBlacklistEnabledCheckBox.IsChecked == true,
            ForceWhitelistEnabled = RestrictionForceWhitelistCheckBox.IsChecked == true,
            WhitelistModIds = _restrictionWhitelistItems.Select(static item => item.ModId).ToList(),
            BlacklistModIds = _restrictionBlacklistItems.Select(static item => item.ModId).ToList()
        };
    }

    private async Task SaveRestrictionAsync()
    {
        var profile = _profileService.GetProfileById(_editingRestrictionProfileId);
        if (profile is null)
        {
            SetRestrictionStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            await _modRestrictionService.SaveAsync(profile, CollectRestrictionSettings());
            await RefreshRestrictionAsync();
            SetRestrictionStatus(T(
                "限制配置已保存；正在运行的服务器需重启后应用。",
                "Restrictions saved; restart a running server to apply them."));
        }
        catch (Exception ex)
        {
            SetRestrictionStatus(T($"限制配置保存失败：{ex.Message}", $"Failed to save restrictions: {ex.Message}"));
        }
    }

    private void ShowRestrictionList()
    {
        _editingRestrictionProfileId = string.Empty;
        RestrictionListPanel.IsVisible = true;
        RestrictionEditorPanel.IsVisible = false;
    }

    private async Task ShowRestrictionEditorAsync(InstanceProfile profile)
    {
        _editingRestrictionProfileId = profile.Id;
        RestrictionListPanel.IsVisible = false;
        RestrictionEditorPanel.IsVisible = true;
        RestrictionProfileNameTextBlock.Text = profile.Name;
        ApplyRestrictionSettings(await _modRestrictionService.LoadAsync(profile));
        SetRestrictionStatus(T($"正在编辑限制配置：{profile.Name}", $"Editing restrictions: {profile.Name}"), notify: false);
    }

    private void SetRestrictionStatus(string message, bool notify = true)
    {
        RestrictionStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetAutomationStatus(string message, bool notify = true)
    {
        AutomationStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetModStatus(string message, bool notify = true)
    {
        ModStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SetAuthStatus(string message, bool notify = true)
    {
        AuthStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void ShowAutomationList()
    {
        _editingAutomationProfileId = string.Empty;
        AutomationListPanel.IsVisible = true;
        AutomationEditorPanel.IsVisible = false;
        RefreshAutomationConfigItems();
    }

    private async Task ShowAutomationEditorAsync(InstanceProfile profile)
    {
        _editingAutomationProfileId = profile.Id;
        AutomationListPanel.IsVisible = false;
        AutomationEditorPanel.IsVisible = true;
        AutomationProfileComboBox.SelectedItem = _automationProfileItems.FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        var settings = await _automationSettingsService.LoadAsync(profile);
        ApplyAutomationSettings(settings);
        SetAutomationStatus(T($"正在编辑自动化配置：{profile.Name}", $"Editing automation: {profile.Name}"), notify: false);
    }

    private void ShowOpenInfoList()
    {
        _editingOpenInfoProfileId = string.Empty;
        OpenInfoListPanel.IsVisible = true;
        OpenInfoEditorPanel.IsVisible = false;
        OpenInfoGlobalSettingsPanel.IsVisible = true;
        OsqToggleButton.IsVisible = true;
        OsqBackButton.IsVisible = false;
        OsqConfigSaveButton.IsVisible = false;
        OsqConfigRefreshButton.IsVisible = false;
        DeployMapModButton.IsVisible = false;
        UpdateOsqToggleButtonText();
        RefreshOpenInfoConfigItems();
    }

    private void ShowOpenInfoEditor(InstanceProfile profile)
    {
        _editingOpenInfoProfileId = profile.Id;
        var settings = _preferencesService.Load().OpenServerQuery;
        var endpoint = FindOpenInfoEndpoint(settings, profile.Id) ?? BuildDefaultOpenInfoEndpoint(profile, settings);
        OpenInfoListPanel.IsVisible = false;
        OpenInfoEditorPanel.IsVisible = true;
        OpenInfoGlobalSettingsPanel.IsVisible = false;
        OsqToggleButton.IsVisible = false;
        OsqBackButton.IsVisible = true;
        OsqConfigSaveButton.IsVisible = true;
        OsqConfigRefreshButton.IsVisible = true;
        DeployMapModButton.IsVisible = true;
        ApplyOpenInfoEndpointConfig(endpoint);
        SetConnectionStatus(T($"正在编辑开放API配置：{profile.Name}", $"Editing Open API config: {profile.Name}"), notify: false);
    }

    private void ShowAuthList()
    {
        _editingAuthProfileId = string.Empty;
        AuthListPanel.IsVisible = true;
        AuthEditorPanel.IsVisible = false;
        AuthBackButton.IsVisible = false;
        AuthSaveButton.IsVisible = false;
        AuthDeployButton.IsVisible = false;
        RefreshAuthConfigItems();
    }

    private async Task ShowAuthEditorAsync(InstanceProfile profile)
    {
        _editingAuthProfileId = profile.Id;
        AuthListPanel.IsVisible = false;
        AuthEditorPanel.IsVisible = true;
        AuthBackButton.IsVisible = true;
        AuthSaveButton.IsVisible = true;
        AuthDeployButton.IsVisible = true;
        AuthProfileComboBox.SelectedItem = _authProfileItems.FirstOrDefault(item =>
            item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase)) ?? profile;
        await LoadAuthForProfileAsync(profile);
    }

    private static AutomationSettings BuildClearedAutomationSettings(string profileId)
    {
        return new AutomationSettings
        {
            TargetProfileId = profileId,
            RestartSchedulerEnabled = false,
            BackupEnabled = false,
            BroadcastEnabled = false,
            CommandEnabled = false,
            ExportLogEnabled = false,
            BackupBeforeShutdown = false,
            ExportBeforeShutdown = false,
            ExportIncludeChat = false,
            ExportIncludeServerInfo = false,
            ActionWindows = [],
            BackupTimes = [],
            BroadcastMessages = [],
            ScheduledCommands = [],
            ExportTimes = []
        };
    }

    private static RobotIntegrationSettings BuildClearedRobotSettings()
    {
        return new RobotIntegrationSettings
        {
            OneBotWsUrl = "ws://127.0.0.1:3001/",
            AccessToken = string.Empty,
            BoundGroupIdsText = string.Empty,
            ReconnectIntervalSec = 5,
            DatabasePath = string.Empty,
            PollIntervalSec = 1.0,
            DefaultEncoding = "utf-8",
            FallbackEncoding = "gbk",
            SuperUsersText = string.Empty,
            OsqPollIntervalSec = 20,
            OsqRequestTimeoutSec = 8
        };
    }

    private static ServerAuthSettings BuildClearedAuthSettings()
    {
        return new ServerAuthSettings
        {
            Enabled = false,
            LoginTimeoutSeconds = 60,
            RememberSessionMinutes = 0,
            Discourse = new ServerAuthDiscourseSettings
            {
                Enabled = false,
                BaseUrl = string.Empty,
                SharedSecret = string.Empty,
                PublicCallbackBaseUrl = "http://127.0.0.1:18092/",
                ListenPrefix = "http://127.0.0.1:18092/"
            }
        };
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
            SetModStatus(T($"模组加载失败：{ex.Message}", $"Mod load failed: {ex.Message}"));
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
            SetModStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."), notify: false);
            return;
        }

        var mods = await _instanceModService.GetModsAsync(profile);
        _modItems.Clear();
        foreach (var mod in mods)
        {
            _modItems.Add(ModListItem.FromModel(mod));
        }

        var enabledCount = mods.Count(static mod => !mod.IsDisabled);
        var disabledCount = mods.Count - enabledCount;
        SetModStatus(T(
            $"已加载 {mods.Count} 个模组，启用 {enabledCount} 个，关闭 {disabledCount} 个。",
            $"Loaded {mods.Count} mods, {enabledCount} enabled, {disabledCount} disabled."), notify: false);
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
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingAuthProfileId)
                ? _editingAuthProfileId
                : AuthProfileComboBox.SelectedItem is InstanceProfile selectedProfile
                    ? selectedProfile.Id
                    : string.Empty;
            _authProfileItems.Clear();
            foreach (var profile in profiles)
            {
                _authProfileItems.Add(profile);
            }

            RefreshAuthConfigItems(profiles);
            AuthProfileComboBox.ItemsSource = _authProfileItems;
            if (_authProfileItems.Count == 0)
            {
                _authPlayerItems.Clear();
                SetAuthStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."), notify: false);
                return;
            }

            var target = _authProfileItems.FirstOrDefault(profile =>
                !string.IsNullOrWhiteSpace(selectedProfileId) &&
                profile.Id.Equals(selectedProfileId, StringComparison.OrdinalIgnoreCase))
                ?? _authProfileItems.FirstOrDefault();
            AuthProfileComboBox.SelectedItem = target;
            if (target is not null && AuthEditorPanel.IsVisible)
            {
                await LoadAuthForProfileAsync(target);
            }
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"认证加载失败：{ex.Message}", $"Auth load failed: {ex.Message}"));
        }
        finally
        {
            _isRefreshingAuth = false;
        }
    }

    private async Task LoadAuthForProfileAsync(InstanceProfile profile)
    {
        var settings = await _serverAuthService.LoadSettingsAsync(profile);
        ApplyAuthSettings(settings);
        await LoadAuthPlayersAsync(profile);
        var authModEnabled = await _serverAuthService.GetAuthModEnabledAsync(profile);
        SetAuthStatus(T(
            $"已加载认证配置，认证模组{(authModEnabled ? "已启用" : "未启用或未部署")}。",
            $"Auth settings loaded, auth mod {(authModEnabled ? "enabled" : "disabled or missing")}."), notify: false);
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

    private void RefreshLaunchButtonSummary()
    {
        var statuses = _serverProcessService.GetCachedStatuses();
        var runningStatuses = statuses.Where(static status => status.IsRunning).ToList();
        var runningIds = runningStatuses
            .Select(static status => status.ProfileId ?? string.Empty)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var selectedIds = LoadLaunchProfileIds();
        var pendingCount = selectedIds.Count(id => !runningIds.Contains(id));
        if (pendingCount > 0)
        {
            LaunchSelectionSummaryTextBlock.Text = runningStatuses.Count > 0
                ? T($"运行中 {runningStatuses.Count} 个 | 待启动 {pendingCount} 个", $"{runningStatuses.Count} running | {pendingCount} pending")
                : T($"准备启动 {pendingCount} 个", $"{pendingCount} selected");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        if (runningStatuses.Count > 0)
        {
            LaunchSelectionSummaryTextBlock.Text = T($"运行中 {runningStatuses.Count} 个 | 点击停止", $"{runningStatuses.Count} running | Click to stop");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        if (_launchTargetItems.Count == 0)
        {
            LaunchSelectionSummaryTextBlock.Text = T("未选择服务器", "No server selected");
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        LaunchSelectionSummaryTextBlock.Text = T($"准备启动 {_launchTargetItems.Count} 个", $"{_launchTargetItems.Count} selected");
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private bool HasPendingLaunchTargets(IReadOnlyList<ServerRuntimeStatus> statuses)
    {
        var selectedIds = LoadLaunchProfileIds();
        if (selectedIds.Count == 0)
        {
            return false;
        }

        var runningIds = statuses
            .Where(static status => status.IsRunning)
            .Select(static status => status.ProfileId ?? string.Empty)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return selectedIds.Any(id => !runningIds.Contains(id));
    }

    private void RefreshAuthConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        _authConfigItems.Clear();
        foreach (var profile in profileList)
        {
            _authConfigItems.Add(ProfileConfigListItem.FromPath(
                profile,
                GetAuthSettingsPath(profile)));
        }
    }

    private void RefreshAutomationConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        _automationConfigItems.Clear();
        foreach (var profile in profileList)
        {
            _automationConfigItems.Add(ProfileConfigListItem.FromPath(
                profile,
                _automationSettingsService.GetSettingsPath(profile)));
        }
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
                var profileForSave = profiles.FirstOrDefault(profile => profile.Id.Equals(save.ProfileId, StringComparison.OrdinalIgnoreCase));
                var activeSavePath = NormalizeFullPath(profileForSave?.ActiveSaveFile);
                var isLocked = !string.IsNullOrWhiteSpace(activeSavePath) &&
                               NormalizeFullPath(save.FullPath).Equals(activeSavePath, StringComparison.OrdinalIgnoreCase);
                if (!isLocked && !string.IsNullOrWhiteSpace(lockedSavePath))
                {
                    var launchIds = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
                    isLocked = launchIds.Contains(save.ProfileId) &&
                               NormalizeFullPath(save.FullPath).Equals(lockedSavePath, StringComparison.OrdinalIgnoreCase);
                }
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

        SetDownloadStatus(T("正在加载服务端版本列表...", "Loading server versions..."), notify: false);
        try
        {
            _catalogEntries.Clear();
            _catalogEntries.AddRange(await _serverPackageService.GetServerDownloadEntriesAsync());
            _downloadCatalogLoaded = true;
            RebuildDownloadVersionItems();
            SetDownloadStatus(
                T($"已加载 {_catalogEntries.Count} 个服务端版本。", $"Loaded {_catalogEntries.Count} server versions."),
                notify: forceReload);
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

    private void SetDownloadStatus(string message, bool notify = true)
    {
        DownloadStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private void SelectTab(MainTab tab)
    {
        var previousTab = _selectedTab;
        _selectedTab = tab;
        _logsNavSelected = false;

        HomePanel.IsVisible = false;
        NonHomePanelHost.IsVisible = true;
        MonitorPanel.IsVisible = tab == MainTab.Monitor;
        ConsolePanel.IsVisible = tab == MainTab.Console;
        InstanceManagePanel.IsVisible = tab == MainTab.InstanceManage;
        SettingsPanel.IsVisible = tab == MainTab.Settings;
        ConnectionPanel.IsVisible = tab == MainTab.Connection;

        RefreshSidebarSelection();

        if (tab == MainTab.Monitor)
        {
            RenderSelectedMetricChart();
        }

        if (tab == MainTab.Console)
        {
            RefreshConsoleServerItems(_serverProcessService.GetCachedStatuses());
            RefreshConsoleText();
        }

        if (tab == MainTab.Connection)
        {
            RefreshConnectionSettingsEditor();
            RefreshConnectionRuntimeStatus();
        }

        ShowNonHomePanel(previousTab == MainTab.Home);
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
        RenderSelectedMetricChart();
    }

    private void SelectInstanceManageTab(InstanceManageTab tab)
    {
        _selectedInstanceManageTab = tab;
        ProfilesPanel.IsVisible = tab == InstanceManageTab.Profiles;
        ConfigPanel.IsVisible = tab == InstanceManageTab.Config;
        SavesPanel.IsVisible = tab == InstanceManageTab.Saves;
        AutomationPanel.IsVisible = tab == InstanceManageTab.Automation;
        RestrictionPanel.IsVisible = tab == InstanceManageTab.Restriction;
        ModsPanel.IsVisible = tab == InstanceManageTab.Mods;
        DownloadVersionsPanel.IsVisible = tab == InstanceManageTab.DownloadVersions;
        RefreshSidebarSelection();

        if (tab == InstanceManageTab.Config)
        {
            if (string.IsNullOrWhiteSpace(_pendingConfigLoadProfileId))
            {
                _ = RefreshConfigProfilesAsync();
            }
        }
        else if (tab == InstanceManageTab.Automation)
        {
            ShowAutomationList();
            _ = RefreshAutomationAsync();
        }
        else if (tab == InstanceManageTab.Restriction)
        {
            ShowRestrictionList();
            _ = RefreshRestrictionAsync();
        }
        else if (tab == InstanceManageTab.Mods)
        {
            _ = RefreshModsAsync();
        }
        else if (tab == InstanceManageTab.DownloadVersions)
        {
            _ = RefreshDownloadVersionsAsync(forceReload: false);
        }
    }

    private void SelectSettingsTab(SettingsTab tab)
    {
        _selectedSettingsTab = tab;
        RefreshSidebarSelection();
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
        RefreshSidebarSelection();
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
        if (tab == ConnectionTab.OpenInfo)
        {
            ShowOpenInfoList();
        }

        if (tab == ConnectionTab.Robot)
        {
            RefreshRobotProfileItems();
        }

        if (tab == ConnectionTab.Auth)
        {
            ShowAuthList();
            _ = RefreshAuthProfilesAsync();
        }
    }

    private void RefreshSidebarSelection()
    {
        SetSelectedClass(MonitorNavButton, !_logsNavSelected && _selectedTab == MainTab.Monitor);
        SetSelectedClass(ConsoleNavButton, !_logsNavSelected && _selectedTab == MainTab.Console);
        SetSelectedClass(ProfilesTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Profiles);
        SetSelectedClass(ConfigTabButton, false);
        SetSelectedClass(SavesTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Saves);
        SetSelectedClass(AutomationTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Automation);
        SetSelectedClass(RestrictionTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Restriction);
        SetSelectedClass(ModsTabButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.Mods);
        SetSelectedClass(DownloadVersionsTabButton, false);
        SetSelectedClass(DownloadVersionsNavButton, !_logsNavSelected && _selectedTab == MainTab.InstanceManage && _selectedInstanceManageTab == InstanceManageTab.DownloadVersions);
        SetSelectedClass(ConnectionAuthTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Auth);
        SetSelectedClass(LogsNavButton, _logsNavSelected);
        SetSelectedClass(ConnectionFrpTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Frp);
        SetSelectedClass(ConnectionOpenInfoTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.OpenInfo);
        SetSelectedClass(ConnectionRobotTabButton, !_logsNavSelected && _selectedTab == MainTab.Connection && _selectedConnectionTab == ConnectionTab.Robot);
        SetSelectedClass(ServerSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Server);
        SetSelectedClass(AppearanceSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Appearance);
        SetSelectedClass(NetworkSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Network);
        SetSelectedClass(AdvancedSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Advanced);
        SetSelectedClass(AboutSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.About);
        SetSelectedClass(SponsorsSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Sponsors);
        SetSelectedClass(ContributorsSettingsTabButton, !_logsNavSelected && _selectedTab == MainTab.Settings && _selectedSettingsTab == SettingsTab.Contributors);
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
                     SettingsAutoStartServerCheckBox,
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
                     OsqEnabledCheckBox
                 })
        {
            check.IsCheckedChanged += OnOpenInfoAutoSaveChanged;
        }

        RobotOneBotTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotAccessTokenTextBox.LostFocus += OnRobotAutoSaveChanged;
        RobotBoundGroupsTextBox.LostFocus += OnRobotAutoSaveChanged;
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
            var profiles = _profileService.GetProfiles();
            SettingsWorkspaceDirectoryTextBox.Text = preferences.WorkspaceRoot;
            SettingsQuickCommandsTextBox.Text = FormatQuickCommands(preferences.QuickCommands);
            SettingsStartWithWindowsCheckBox.IsChecked = preferences.StartWithWindows;
            SettingsCloseToTrayCheckBox.IsChecked = preferences.CloseToTrayOnExit;
            SettingsStartHiddenCheckBox.IsChecked = preferences.StartHiddenOnLaunch;
            SettingsAutoStartServerCheckBox.IsChecked = preferences.AutoStartServerOnLaunch;
            SettingsAutoStartOsqCheckBox.IsChecked = preferences.AutoStartOpenServerQueryOnLaunch;
            SettingsAutoStartRobotCheckBox.IsChecked = preferences.AutoStartRobotOnLaunch;
            SettingsAutoStartFrpCheckBox.IsChecked = preferences.AutoStartFrpOnLaunch;
            SettingsAutoStartThirdPartyFrpcCheckBox.IsChecked = preferences.AutoStartThirdPartyFrpcOnLaunch;
            SettingsAutoStartServerProfileComboBox.ItemsSource = profiles;
            var autoStartIds = SplitProfileIds(preferences.AutoStartServerProfileIds, preferences.AutoStartServerProfileId);
            SettingsAutoStartServerProfileComboBox.SelectedItem = profiles.FirstOrDefault(profile =>
                autoStartIds.Contains(profile.Id))
                ?? profiles.FirstOrDefault(profile =>
                    SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).Contains(profile.Id))
                ?? profiles.FirstOrDefault();
            RefreshSettingsAutoStartTargetItems(profiles);
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
        var autoStartIds = LoadAutoStartProfileIds().ToList();
        preferences.WorkspaceRoot = SettingsWorkspaceDirectoryTextBox.Text?.Trim() ?? string.Empty;
        preferences.QuickCommands = ParseQuickCommands(SettingsQuickCommandsTextBox.Text);
        preferences.StartWithWindows = SettingsStartWithWindowsCheckBox.IsChecked == true;
        preferences.CloseToTrayOnExit = SettingsCloseToTrayCheckBox.IsChecked == true;
        preferences.StartHiddenOnLaunch = SettingsStartHiddenCheckBox.IsChecked == true;
        preferences.AutoStartServerOnLaunch = SettingsAutoStartServerCheckBox.IsChecked == true;
        preferences.AutoStartServerProfileIds = autoStartIds;
        preferences.AutoStartServerProfileId = string.Join(';', autoStartIds);
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
            RefreshRobotProfileItems();

            var preferences = _preferencesService.Load();
            ApplyFrpSettings(preferences.Frp);
            ApplyOpenServerQuerySettings(preferences.OpenServerQuery);
            ApplyRobotSettings(preferences.Robot);
            RefreshRobotProfileItems();
            RefreshOpenInfoConfigItems();
            RefreshAuthConfigItems();
        }
        finally
        {
            _isApplyingConnectionSettings = false;
        }
    }

    private void RefreshRobotProfileItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var selectedByItem = _robotBindingItems
            .Select(item => (Item: item, ProfileId: item.SelectedProfile?.Id ?? item.ProfileId))
            .ToList();

        _robotProfileItems.Clear();
        foreach (var profile in profileList)
        {
            _robotProfileItems.Add(profile);
        }

        foreach (var item in _robotBindingItems)
        {
            var selectedId = selectedByItem.FirstOrDefault(entry => ReferenceEquals(entry.Item, item)).ProfileId ?? item.ProfileId;
            item.ProfileOptions = _robotProfileItems;
            item.SelectedProfile = _robotProfileItems.FirstOrDefault(profile =>
                profile.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        }
    }

    private void RefreshOpenInfoConfigItems(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        var profileList = profiles ?? _profileService.GetProfiles();
        var settings = _preferencesService.Load().OpenServerQuery;
        _openInfoConfigItems.Clear();
        foreach (var profile in profileList.OrderBy(static profile => profile.Name, StringComparer.OrdinalIgnoreCase))
        {
            var endpoint = FindOpenInfoEndpoint(settings, profile.Id) ?? BuildDefaultOpenInfoEndpoint(profile, settings);
            EnsureOpenInfoProfileConfigFile(profile, endpoint);
            _openInfoConfigItems.Add(OpenServerQueryProfileConfigItem.FromProfile(profile, endpoint, GetOpenInfoSettingsPath(profile)));
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
    }

    private void ApplyRobotSettings(RobotIntegrationSettings settings)
    {
        RobotOneBotTextBox.Text = settings.OneBotWsUrl;
        RobotAccessTokenTextBox.Text = settings.AccessToken;
        RobotBoundGroupsTextBox.Text = settings.BoundGroupIdsText;
        SetNumericValue(RobotReconnectNumericUpDown, settings.ReconnectIntervalSec);
        SetNumericValue(RobotPollIntervalNumericUpDown, settings.PollIntervalSec);
        RobotDatabasePathTextBox.Text = settings.DatabasePath;
        RobotDefaultEncodingTextBox.Text = settings.DefaultEncoding;
        RobotFallbackEncodingTextBox.Text = settings.FallbackEncoding;
        SetNumericValue(RobotOsqPollNumericUpDown, settings.OsqPollIntervalSec);
        SetNumericValue(RobotOsqTimeoutNumericUpDown, settings.OsqRequestTimeoutSec);
        RobotSuperUsersTextBox.Text = settings.SuperUsersText;
        RebuildRobotBindingItems(settings);
    }

    private void RebuildRobotBindingItems(RobotIntegrationSettings settings)
    {
        RefreshRobotProfileItems();
        _robotBindingItems.Clear();

        foreach (var binding in settings.ProfileBindings ?? [])
        {
            _robotBindingItems.Add(new RobotProfileBindingItem(
                _robotProfileItems,
                binding.ProfileId,
                binding.GroupId,
                binding.SuperUserId));
        }

        if (_robotBindingItems.Count == 0)
        {
            var groups = ParseQqIds(settings.BoundGroupIdsText).Select(static id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var admins = ParseQqIds(settings.SuperUsersText).Select(static id => id.ToString(CultureInfo.InvariantCulture)).ToList();
            var count = Math.Max(groups.Count, admins.Count);
            for (var i = 0; i < count; i++)
            {
                _robotBindingItems.Add(new RobotProfileBindingItem(
                    _robotProfileItems,
                    _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
                    i < groups.Count ? groups[i] : string.Empty,
                    i < admins.Count ? admins[i] : string.Empty));
            }
        }

        if (_robotBindingItems.Count == 0)
        {
            _robotBindingItems.Add(new RobotProfileBindingItem(
                _robotProfileItems,
                _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
                string.Empty,
                string.Empty));
        }
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
        SaveOpenInfoProfileConfigFiles(preferences.OpenServerQuery);
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

    private async Task SaveRobotSettingsAndReloadIfRunningAsync(bool updateStatus = true, bool refreshEditor = true)
    {
        SaveRobotSettings(updateStatus, refreshEditor);
        var preferences = _preferencesService.Load();
        await _robotService.SaveSettingsAsync(ToRobotSettings(preferences.Robot, preferences.OpenServerQuery));

        if (!_robotService.GetCurrentStatus().IsRunning)
        {
            return;
        }

        try
        {
            await _robotService.StopAsync(TimeSpan.FromSeconds(5));
            await _robotService.StartAsync(ToRobotSettings(preferences.Robot, preferences.OpenServerQuery));
            SetConnectionStatus(T("QQ机器人配置已保存，并已重新加载。", "QQ robot configuration saved and reloaded."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"QQ机器人配置已保存，但重新加载失败：{ex.Message}", $"QQ robot configuration saved, but reload failed: {ex.Message}"));
        }
        finally
        {
            UpdateRobotToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
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
        var current = _preferencesService.Load().OpenServerQuery;
        var endpoints = CollectOpenServerQueryEndpoints(current);
        var firstEndpoint = endpoints.FirstOrDefault();
        return new OpenServerQuerySettings
        {
            Enabled = OsqEnabledCheckBox.IsChecked == true,
            ListenPrefix = string.IsNullOrWhiteSpace(OsqListenPrefixTextBox.Text)
                ? "http://127.0.0.1:18089/"
                : OsqListenPrefixTextBox.Text.Trim(),
            AllowInsecureHttp = current.AllowInsecureHttp,
            RequestTimeoutSec = GetNumericValue(OsqRequestTimeoutNumericUpDown, 8),
            IncludeServerInfo = current.IncludeServerInfo,
            IncludePlayers = current.IncludePlayers,
            IncludePlayerEvents = current.IncludePlayerEvents,
            IncludeChats = current.IncludeChats,
            IncludeNotifications = current.IncludeNotifications,
            IncludeMapData = current.IncludeMapData,
            Endpoints = endpoints,
            EndpointHost = firstEndpoint?.ServerHost ?? string.Empty,
            EndpointToken = firstEndpoint?.Token ?? string.Empty
        };
    }

    private List<OpenServerQueryEndpointConfig> CollectOpenServerQueryEndpoints(OpenServerQuerySettings currentSettings)
    {
        var byProfile = currentSettings.Endpoints
            .Where(static endpoint => !string.IsNullOrWhiteSpace(endpoint.ProfileId))
            .ToDictionary(static endpoint => endpoint.ProfileId, StringComparer.OrdinalIgnoreCase);

        foreach (var item in _openInfoConfigItems)
        {
            if (!byProfile.TryGetValue(item.ProfileId, out var endpoint))
            {
                endpoint = BuildDefaultOpenInfoEndpoint(item.ProfileId, currentSettings);
            }

            endpoint.Enabled = item.Enabled;
            byProfile[item.ProfileId] = endpoint;
        }

        if (!string.IsNullOrWhiteSpace(_editingOpenInfoProfileId))
        {
            var profile = _profileService.GetProfileById(_editingOpenInfoProfileId);
            if (profile is not null)
            {
                var edited = CollectOpenInfoEndpointConfig(profile);
                byProfile[profile.Id] = edited;
            }
        }

        if (byProfile.Count == 0)
        {
            foreach (var endpoint in currentSettings.Endpoints ?? [])
            {
                if (!string.IsNullOrWhiteSpace(endpoint.ServerHost) || !string.IsNullOrWhiteSpace(endpoint.Token))
                {
                    byProfile[endpoint.ProfileId] = endpoint;
                }
            }
        }

        return byProfile.Values
            .Where(static endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.ProfileId) ||
                !string.IsNullOrWhiteSpace(endpoint.ServerHost) ||
                !string.IsNullOrWhiteSpace(endpoint.Token))
            .OrderBy(static endpoint => endpoint.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private RobotIntegrationSettings CollectRobotSettings()
    {
        var bindings = CollectRobotProfileBindings();
        return new RobotIntegrationSettings
        {
            OneBotWsUrl = string.IsNullOrWhiteSpace(RobotOneBotTextBox.Text)
                ? "ws://127.0.0.1:3001/"
                : RobotOneBotTextBox.Text.Trim(),
            AccessToken = RobotAccessTokenTextBox.Text?.Trim() ?? string.Empty,
            BoundGroupIdsText = FormatQqIdText(bindings.Select(static binding => binding.GroupId)),
            ReconnectIntervalSec = GetNumericValue(RobotReconnectNumericUpDown, 5),
            DatabasePath = RobotDatabasePathTextBox.Text?.Trim() ?? string.Empty,
            PollIntervalSec = GetNumericDoubleValue(RobotPollIntervalNumericUpDown, 1.0),
            DefaultEncoding = string.IsNullOrWhiteSpace(RobotDefaultEncodingTextBox.Text)
                ? "utf-8"
                : RobotDefaultEncodingTextBox.Text.Trim(),
            FallbackEncoding = string.IsNullOrWhiteSpace(RobotFallbackEncodingTextBox.Text)
                ? "gbk"
                : RobotFallbackEncodingTextBox.Text.Trim(),
            SuperUsersText = FormatQqIdText(bindings.Select(static binding => binding.SuperUserId)),
            ProfileBindings = bindings,
            OsqPollIntervalSec = GetNumericValue(RobotOsqPollNumericUpDown, 20),
            OsqRequestTimeoutSec = GetNumericValue(RobotOsqTimeoutNumericUpDown, 8)
        };
    }

    private List<RobotProfileBinding> CollectRobotProfileBindings()
    {
        var bindings = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in _robotBindingItems)
        {
            var profileId = item.SelectedProfile?.Id ?? item.ProfileId;
            var groupId = NormalizeQqId(item.GroupId);
            var superUserId = NormalizeQqId(item.SuperUserId);
            if (string.IsNullOrWhiteSpace(profileId) &&
                string.IsNullOrWhiteSpace(groupId) &&
                string.IsNullOrWhiteSpace(superUserId))
            {
                continue;
            }

            var key = $"{profileId}|{groupId}|{superUserId}";
            if (!seen.Add(key))
            {
                continue;
            }

            bindings.Add(new RobotProfileBinding
            {
                ProfileId = profileId?.Trim() ?? string.Empty,
                GroupId = groupId,
                SuperUserId = superUserId
            });
        }

        return bindings;
    }

    private static string FormatQqIdText(IEnumerable<string?> values)
    {
        var ids = values
            .Select(NormalizeQqId)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return ids.Count == 0 ? string.Empty : string.Join(Environment.NewLine, ids);
    }

    private static string NormalizeQqId(string? value)
    {
        var raw = value?.Trim() ?? string.Empty;
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private OpenServerQueryEndpointConfig CollectOpenInfoEndpointConfig(InstanceProfile profile)
    {
        var preferences = _preferencesService.Load();
        var existing = FindOpenInfoEndpoint(preferences.OpenServerQuery, profile.Id) ??
                       BuildDefaultOpenInfoEndpoint(profile, preferences.OpenServerQuery);

        return new OpenServerQueryEndpointConfig
        {
            ProfileId = profile.Id,
            ServerHost = OsqEndpointHostTextBox.Text?.Trim() ?? existing.ServerHost,
            Token = OsqEndpointTokenTextBox.Text?.Trim() ?? existing.Token,
            Enabled = existing.Enabled,
            AllowInsecureHttp = OsqAllowInsecureHttpCheckBox.IsChecked == true,
            IncludeServerInfo = OsqIncludeServerInfoCheckBox.IsChecked == true,
            IncludePlayers = OsqIncludePlayersCheckBox.IsChecked == true,
            IncludePlayerEvents = OsqIncludeEventsCheckBox.IsChecked == true,
            IncludeChats = OsqIncludeChatsCheckBox.IsChecked == true,
            IncludeNotifications = OsqIncludeNotificationsCheckBox.IsChecked == true,
            IncludeMapData = OsqIncludeMapCheckBox.IsChecked == true
        };
    }

    private void ApplyOpenInfoEndpointConfig(OpenServerQueryEndpointConfig endpoint)
    {
        OsqEndpointHostTextBox.Text = endpoint.ServerHost;
        OsqEndpointTokenTextBox.Text = endpoint.Token;
        OsqAllowInsecureHttpCheckBox.IsChecked = endpoint.AllowInsecureHttp;
        OsqIncludeServerInfoCheckBox.IsChecked = endpoint.IncludeServerInfo;
        OsqIncludePlayersCheckBox.IsChecked = endpoint.IncludePlayers;
        OsqIncludeEventsCheckBox.IsChecked = endpoint.IncludePlayerEvents;
        OsqIncludeChatsCheckBox.IsChecked = endpoint.IncludeChats;
        OsqIncludeNotificationsCheckBox.IsChecked = endpoint.IncludeNotifications;
        OsqIncludeMapCheckBox.IsChecked = endpoint.IncludeMapData;
    }

    private static OpenServerQueryEndpointConfig? FindOpenInfoEndpoint(OpenServerQuerySettings settings, string profileId)
    {
        return (settings.Endpoints ?? [])
            .FirstOrDefault(endpoint => endpoint.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
    }

    private OpenServerQueryEndpointConfig BuildDefaultOpenInfoEndpoint(InstanceProfile profile, OpenServerQuerySettings settings)
    {
        return BuildDefaultOpenInfoEndpoint(profile.Id, settings);
    }

    private OpenServerQueryEndpointConfig BuildDefaultOpenInfoEndpoint(string profileId, OpenServerQuerySettings settings)
    {
        var legacy = (settings.Endpoints ?? []).FirstOrDefault(endpoint => string.IsNullOrWhiteSpace(endpoint.ProfileId));
        return new OpenServerQueryEndpointConfig
        {
            ProfileId = profileId,
            ServerHost = legacy?.ServerHost ?? settings.EndpointHost ?? string.Empty,
            Token = legacy?.Token ?? settings.EndpointToken ?? string.Empty,
            Enabled = legacy?.Enabled ?? false,
            AllowInsecureHttp = settings.AllowInsecureHttp,
            IncludeServerInfo = settings.IncludeServerInfo,
            IncludePlayers = settings.IncludePlayers,
            IncludePlayerEvents = settings.IncludePlayerEvents,
            IncludeChats = settings.IncludeChats,
            IncludeNotifications = settings.IncludeNotifications,
            IncludeMapData = settings.IncludeMapData
        };
    }

    private void EnsureOpenInfoProfileConfigFile(InstanceProfile profile, OpenServerQueryEndpointConfig endpoint)
    {
        var path = GetOpenInfoSettingsPath(profile);
        if (File.Exists(path))
        {
            return;
        }

        SaveOpenInfoProfileConfigFile(profile, endpoint);
    }

    private void SaveOpenInfoProfileConfigFile(InstanceProfile profile, OpenServerQueryEndpointConfig endpoint)
    {
        var path = GetOpenInfoSettingsPath(profile);
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var json = JsonSerializer.Serialize(endpoint, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json);
    }

    private void SaveOpenInfoProfileConfigFiles(OpenServerQuerySettings settings)
    {
        foreach (var endpoint in settings.Endpoints ?? [])
        {
            if (string.IsNullOrWhiteSpace(endpoint.ProfileId))
            {
                continue;
            }

            var profile = _profileService.GetProfileById(endpoint.ProfileId);
            if (profile is null)
            {
                continue;
            }

            SaveOpenInfoProfileConfigFile(profile, endpoint);
        }
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
                ProfileId = endpoint.ProfileId?.Trim() ?? string.Empty,
                ServerHost = host,
                Token = token,
                Enabled = endpoint.Enabled,
                AllowInsecureHttp = endpoint.AllowInsecureHttp,
                IncludeServerInfo = endpoint.IncludeServerInfo,
                IncludePlayers = endpoint.IncludePlayers,
                IncludePlayerEvents = endpoint.IncludePlayerEvents,
                IncludeChats = endpoint.IncludeChats,
                IncludeNotifications = endpoint.IncludeNotifications,
                IncludeMapData = endpoint.IncludeMapData
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
                Enabled = true,
                AllowInsecureHttp = settings.AllowInsecureHttp,
                IncludeServerInfo = settings.IncludeServerInfo,
                IncludePlayers = settings.IncludePlayers,
                IncludePlayerEvents = settings.IncludePlayerEvents,
                IncludeChats = settings.IncludeChats,
                IncludeNotifications = settings.IncludeNotifications,
                IncludeMapData = settings.IncludeMapData
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
            BoundGroupIds = ParseQqIds(settings.BoundGroupIdsText),
            ProfileBindings = settings.ProfileBindings ?? [],
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

        if (!_isTogglingFrp)
        {
            ConnectionFrpToggleButton.Content = frpStatus.IsRunning
                ? T("停止常规", "Stop Regular")
                : T("启动常规", "Start Regular");
        }

        if (!_isTogglingThirdPartyFrpc)
        {
            ConnectionThirdPartyFrpcToggleButton.Content = thirdPartyStatus.IsRunning
                ? T("停止第三方", "Stop Third-party")
                : T("启动第三方", "Start Third-party");
        }
    }

    private bool IsConnectionProcessToggling(ConnectionProcessKind kind)
    {
        return kind == ConnectionProcessKind.Frp ? _isTogglingFrp : _isTogglingThirdPartyFrpc;
    }

    private void SetConnectionProcessToggling(ConnectionProcessKind kind, bool toggling)
    {
        if (kind == ConnectionProcessKind.Frp)
        {
            _isTogglingFrp = toggling;
            ConnectionFrpToggleButton.IsEnabled = !toggling;
            return;
        }

        _isTogglingThirdPartyFrpc = toggling;
        ConnectionThirdPartyFrpcToggleButton.IsEnabled = !toggling;
    }

    private void SetConnectionProcessToggleText(ConnectionProcessKind kind, bool runningText)
    {
        if (kind == ConnectionProcessKind.Frp)
        {
            ConnectionFrpToggleButton.Content = runningText
                ? T("停止常规", "Stop Regular")
                : T("启动常规", "Start Regular");
            return;
        }

        ConnectionThirdPartyFrpcToggleButton.Content = runningText
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

        SetConnectionStatus(currentStatus, notify: false);
        UpdateCardValues(_serverProcessService.GetCachedStatus());
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

    private void SetConnectionStatus(string message, bool notify = true)
    {
        ConnectionStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
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
        if (IsConnectionProcessToggling(kind))
            return;

        SetConnectionProcessToggling(kind, true);
        try
        {
            if (kind == ConnectionProcessKind.Frp
                    ? _frpService.GetCurrentStatus().IsRunning
                    : _thirdPartyFrpcService.GetCurrentStatus().IsRunning)
            {
                SetConnectionProcessToggleText(kind, runningText: false);
                await StopConnectionProcessAsync(kind);
                return;
            }

            SaveFrpSettings(updateStatus: false, refreshEditor: false);
            SetConnectionProcessToggleText(kind, runningText: true);
            await StartConnectionProcessAsync(kind);
        }
        finally
        {
            SetConnectionProcessToggling(kind, false);
            UpdateConnectionFrpActionButtons();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
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
        if (string.IsNullOrWhiteSpace(line)
            || IsSystemConsoleLine(line)
            || ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TrackPlayerEventText(line);
        });
    }

    private void OnServerProfileOutputReceived(object? sender, ServerOutputLine output)
    {
        if (string.IsNullOrWhiteSpace(output.Line)
            || IsSystemConsoleLine(output.Line)
            || ShouldSuppressConsoleLineForUi(output.Line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AppendConsoleLine(output);
            TrackPlayerEventText(output.Line);
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
                SetConnectionStatus(line, notify: false);
            }
        });
    }

    private async Task HandleServerLogTailAsync(ServerRuntimeStatus status)
    {
        try
        {
            var profileId = status.ProfileId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId))
            {
                return;
            }

            if (!status.IsRunning)
            {
                if (_tailedProfileIds.Remove(profileId))
                {
                    await _logTailService.StopAsync(profileId);
                }

                return;
            }

            if (_tailedProfileIds.Contains(profileId))
            {
                return;
            }

            var profile = _profileService.GetProfileById(profileId);
            if (profile is null)
            {
                return;
            }

            var replayExisting = ShouldReplayExistingServerLogs(status, profile);
            await _logTailService.StartAsync(profile, replayExisting);
            _tailedProfileIds.Add(profile.Id);
            if (replayExisting)
            {
                _replayedLogProfileIds.Add(profile.Id);
                _consoleReplayLoadedProfileIds.Add(profile.Id);
            }
        }
        catch
        {
            // 日志跟随失败不影响主流程。
        }
    }

    private bool ShouldReplayExistingServerLogs(ServerRuntimeStatus status, InstanceProfile profile)
    {
        if (_replayedLogProfileIds.Contains(profile.Id))
        {
            return false;
        }

        return status.StartedAtUtc.HasValue &&
               status.StartedAtUtc.Value < _windowStartedAtUtc.AddSeconds(-RunningServerLogReplayGraceSeconds);
    }

    private void OnLogTailLineReceived(object? sender, string line)
    {
        if (string.IsNullOrWhiteSpace(line)
            || IsSystemConsoleLine(line)
            || ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            TrackPlayerEventText(line);
        });
    }

    private void OnProfileLogTailLineReceived(object? sender, ProfileLogLine output)
    {
        if (string.IsNullOrWhiteSpace(output.Line)
            || string.IsNullOrWhiteSpace(output.ProfileId)
            || IsSystemConsoleLine(output.Line)
            || ShouldSuppressConsoleLineForUi(output.Line))
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            AppendConsoleProfileLine(
                output.ProfileId,
                string.IsNullOrWhiteSpace(output.ProfileName) ? output.ProfileId : output.ProfileName,
                $"[log] {output.Line}");
            TrackPlayerEventText(output.Line);
        });
    }

    private void OnConsoleOutputScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        _consoleAutoScroll = IsConsoleScrolledToBottom();
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
                ScrollAutomationRuntimeLogsToEnd();
            }
        });
    }

    private void ScrollAutomationRuntimeLogsToEnd()
    {
        Dispatcher.UIThread.Post(() =>
        {
            var scrollViewer = AutomationRuntimeLogsListBox
                .GetVisualDescendants()
                .OfType<ScrollViewer>()
                .FirstOrDefault();
            scrollViewer?.ScrollToEnd();
        }, DispatcherPriority.Background);
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
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (IsSystemConsoleLine(line))
        {
            ShowToast(line);
            return;
        }

        if (ShouldSuppressConsoleLineForUi(line))
        {
            return;
        }

        var shouldAutoScroll = _consoleAutoScroll || IsConsoleScrolledToBottom();
        _consoleLines.Add(line);
        while (_consoleLines.Count > MaxConsoleLines)
        {
            _consoleLines.RemoveAt(0);
        }

        QueueConsoleRefresh();
    }

    private void AppendConsoleLine(ServerOutputLine output)
    {
        var profileId = string.IsNullOrWhiteSpace(output.ProfileId) ? "__unknown" : output.ProfileId;
        var profileName = string.IsNullOrWhiteSpace(output.ProfileName) ? profileId : output.ProfileName;
        AppendConsoleProfileLine(profileId, profileName, output.Line);
    }

    private void AppendConsoleProfileLine(string profileId, string profileName, string rawLine)
    {
        if (string.IsNullOrWhiteSpace(rawLine)
            || IsSystemConsoleLine(rawLine)
            || ShouldSuppressConsoleLineForUi(rawLine))
        {
            return;
        }

        if (!_consoleLinesByProfile.TryGetValue(profileId, out var lines))
        {
            lines = [];
            _consoleLinesByProfile[profileId] = lines;
        }

        var line = $"[{profileName}] {rawLine}";
        lines.Add(line);
        while (lines.Count > MaxConsoleLines)
        {
            lines.RemoveAt(0);
        }

        if (string.IsNullOrWhiteSpace(_selectedConsoleProfileId))
        {
            _selectedConsoleProfileId = profileId;
        }

        if (_selectedConsoleProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
        {
            QueueConsoleRefresh();
        }
    }

    private async void QueueConsoleRefresh()
    {
        if (_consoleRefreshQueued)
        {
            return;
        }

        _consoleRefreshQueued = true;
        await Task.Delay(ConsoleRefreshDelayMs);
        _consoleRefreshQueued = false;
        RefreshConsoleText();
    }

    private void RefreshConsoleText()
    {
        if (_selectedTab != MainTab.Console)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(_selectedConsoleProfileId) ||
            !_consoleLinesByProfile.TryGetValue(_selectedConsoleProfileId, out var lines))
        {
            ConsoleOutputTextBlock.Text = string.Join(Environment.NewLine, _consoleLines);
            return;
        }

        var shouldAutoScroll = _consoleAutoScroll || IsConsoleScrolledToBottom();
        ConsoleOutputTextBlock.Text = string.Join(Environment.NewLine, lines);
        if (shouldAutoScroll)
        {
            Dispatcher.UIThread.Post(() =>
            {
                ConsoleOutputScrollViewer.ScrollToEnd();
                _consoleAutoScroll = true;
            }, DispatcherPriority.Background);
        }
    }

    private async Task EnsureConsoleReplayLoadedAsync(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId) || _consoleReplayLoadedProfileIds.Contains(profileId))
        {
            return;
        }

        var profile = _profileService.GetProfileById(profileId);
        if (profile is null)
        {
            return;
        }

        IReadOnlyList<string> lines;
        try
        {
            lines = await Task.Run(() => ReadConsoleProfileReplayLines(profile));
        }
        catch
        {
            return;
        }

        if (lines.Count == 0)
        {
            return;
        }

        _consoleReplayLoadedProfileIds.Add(profileId);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var line in lines)
            {
                AppendConsoleProfileLine(profile.Id, profile.Name, $"[log] {line}");
                TrackPlayerEventText(line);
            }

            RefreshConsoleText();
        });
    }

    private static IReadOnlyList<string> ReadConsoleProfileReplayLines(InstanceProfile profile)
    {
        var logsPath = Path.Combine(profile.DirectoryPath, "Logs");
        var paths = new[]
        {
            Path.Combine(logsPath, "server-main.log"),
            Path.Combine(logsPath, "server-chat.log"),
            Path.Combine(logsPath, "server-audit.log")
        };

        return paths
            .SelectMany(path => ReadTailLines(path, ConsoleProfileReplayLogBytes, ConsoleProfileReplayLogLines))
            .Where(line => !string.IsNullOrWhiteSpace(line) && !ShouldSuppressConsoleLineForUi(line))
            .TakeLast(ConsoleProfileReplayLogLines)
            .ToList();
    }

    private static IReadOnlyList<string> ReadTailLines(string path, int maxBytes, int maxLines)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (start > 0)
            {
                _ = reader.ReadLine();
            }

            var lines = new Queue<string>();
            while (!reader.EndOfStream)
            {
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Enqueue(line);
                while (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return lines.ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static bool IsSystemConsoleLine(string? line)
    {
        return !string.IsNullOrWhiteSpace(line) &&
               line.TrimStart().StartsWith("[system]", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldSuppressConsoleLineForUi(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = StripLauncherConsolePrefix(line.Trim());
        if (normalized.Length == 0)
        {
            return false;
        }

        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("[audit]", StringComparison.Ordinal))
        {
            if (lower.Contains("shift clicked slot", StringComparison.Ordinal)
                || lower.Contains("left clicked slot", StringComparison.Ordinal)
                || lower.Contains("right clicked slot", StringComparison.Ordinal)
                || lower.Contains("middle clicked slot", StringComparison.Ordinal)
                || lower.Contains("slot ", StringComparison.Ordinal) && lower.Contains(" in ", StringComparison.Ordinal)
                || lower.Contains("before: (", StringComparison.Ordinal)
                || lower.Contains("after: (", StringComparison.Ordinal)
                || lower.Contains("harvestablecontents-", StringComparison.Ordinal)
                || lower.Contains("backpack-", StringComparison.Ordinal)
                || lower.Contains("hotbar-", StringComparison.Ordinal)
                || lower.Contains("ground-", StringComparison.Ordinal)
                || lower.Contains("mouse-", StringComparison.Ordinal)
                || lower.Contains(" killed game:", StringComparison.Ordinal))
            {
                return true;
            }
        }

        if (lower.Contains("[talk]", StringComparison.Ordinal)
            || lower.Contains("[chat]", StringComparison.Ordinal)
            || ConsoleChatLineRegex().IsMatch(normalized)
            || ConsoleNotificationLineRegex().IsMatch(normalized)
            || ConsoleJoinLeaveLineRegex().IsMatch(normalized)
            || ConsoleDeathLineRegex().IsMatch(normalized)
            || ConsoleAdminLineRegex().IsMatch(normalized)
            || ConsoleLifecycleLineRegex().IsMatch(normalized)
            || ConsoleSpecialEventLineRegex().IsMatch(normalized))
        {
            return false;
        }

        if (lower.Contains("[warning]", StringComparison.Ordinal)
            || lower.Contains("[error]", StringComparison.Ordinal)
            || lower.Contains("exception", StringComparison.Ordinal)
            || lower.Contains("fatal", StringComparison.Ordinal)
            || lower.Contains("unhandled", StringComparison.Ordinal)
            || lower.Contains("stack trace", StringComparison.Ordinal))
        {
            return false;
        }

        if (lower.Contains(" killed ", StringComparison.Ordinal)
            && !lower.Contains(" killed game:", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static string StripLauncherConsolePrefix(string line)
    {
        const string prefix = "[log]";
        return line.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? line[prefix.Length..].TrimStart()
            : line;
    }

    private bool IsConsoleScrolledToBottom()
    {
        var scrollableHeight = Math.Max(0, ConsoleOutputScrollViewer.Extent.Height - ConsoleOutputScrollViewer.Viewport.Height);
        if (scrollableHeight <= ConsoleAutoScrollThreshold)
        {
            return true;
        }

        return scrollableHeight - ConsoleOutputScrollViewer.Offset.Y <= ConsoleAutoScrollThreshold;
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

    private void OnRepositoryClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/vscn-studio/LauncherGo");

    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/vscn-studio/LauncherGo/issues");

    private void OnSponsorClick(object? sender, RoutedEventArgs e) => OpenUrl("https://vscn.studio/sponsors");

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHomeNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Monitor);

    private void OnMonitorNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Monitor);

    private void OnConsoleNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Console);

    private void OnInstanceManageNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.InstanceManage);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Settings);

    private void OnConnectionNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Connection);

    private void OnServerStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Server);

    private void OnRobotStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Robot);

    private void OnOnlinePlayersCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Players);

    private void OnNetworkStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Network);

    private void OnProfilesSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
    }

    private void OnConfigSubTabClick(object? sender, RoutedEventArgs e)
    {
        _editingConfigProfileId = string.Empty;
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Config);
    }

    private async void OnEditProfileConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileListItem item })
        {
            return;
        }

        await OpenProfileConfigEditorAsync(item.Id);
    }

    private void OnSavesSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Saves);
    }

    private void OnAutomationSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Automation);
    }

    private void OnRestrictionSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Restriction);
    }

    private void OnModsSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.Mods);
    }

    private void OnDownloadVersionsSubTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.InstanceManage);
        SelectInstanceManageTab(InstanceManageTab.DownloadVersions);
    }

    private void OnServerSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Server);
    }

    private void OnAppearanceSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Appearance);
    }

    private void OnNetworkSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Network);
    }

    private void OnAdvancedSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Advanced);
    }

    private void OnAboutSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.About);
    }

    private void OnSponsorsSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Sponsors);
    }

    private void OnContributorsSettingsTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Settings);
        SelectSettingsTab(SettingsTab.Contributors);
    }

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

    private void OnSettingsAutoStartServerProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isApplyingServerSettings)
        {
            return;
        }

        SaveServerSettings(refreshEditor: false);
    }

    private void OnSettingsAutoStartAddProfileClick(object? sender, RoutedEventArgs e)
    {
        if (SettingsAutoStartAddProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var ids = LoadAutoStartProfileIds();
        ids.Add(profile.Id);
        SaveAutoStartProfileIds(ids);
        SettingsAutoStartAddProfileComboBox.SelectedIndex = -1;
    }

    private void OnSettingsAutoStartRemoveSelectedProfileClick(object? sender, RoutedEventArgs e)
    {
        var selected = _settingsAutoStartTargetItems.FirstOrDefault(static item => item.IsSelected)
                       ?? _settingsAutoStartTargetItems.LastOrDefault();
        if (selected is null)
        {
            return;
        }

        var ids = LoadAutoStartProfileIds();
        ids.Remove(selected.ProfileId);
        SaveAutoStartProfileIds(ids);
    }

    private void OnSettingsAutoStartTargetChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: LaunchTargetItem item } button)
        {
            return;
        }

        foreach (var target in _settingsAutoStartTargetItems)
        {
            target.IsSelected = false;
        }

        item.IsSelected = button.IsChecked == true;
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

    private void OnConnectionFrpTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Frp);
    }

    private void OnConnectionOpenInfoTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.OpenInfo);
    }

    private void OnConnectionRobotTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Robot);
    }

    private void OnConnectionAuthTabClick(object? sender, RoutedEventArgs e)
    {
        SelectTab(MainTab.Connection);
        SelectConnectionTab(ConnectionTab.Auth);
    }

    private async void OnLogsNavClick(object? sender, RoutedEventArgs e)
    {
        _logsNavSelected = true;
        RefreshSidebarSelection();
        await OpenAppLogsAsync();
    }

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
        if (_selectedConnectionTab == ConnectionTab.OpenInfo)
        {
            RefreshOpenInfoConfigItems();
        }

        if (_selectedConnectionTab == ConnectionTab.Robot)
        {
            RefreshRobotProfileItems();
        }

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

    private async void OnAutomationEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfigListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
        {
            await ShowAutomationEditorAsync(profile);
        }
    }

    private void OnAutomationBackClick(object? sender, RoutedEventArgs e)
    {
        ShowAutomationList();
    }

    private async void OnAutomationClearClick(object? sender, RoutedEventArgs e)
    {
        var selected = _automationConfigItems.Where(static item => item.IsSelected).ToList();
        foreach (var item in selected)
        {
            var profile = _profileService.GetProfileById(item.ProfileId);
            if (profile is null)
            {
                continue;
            }

            await _automationSettingsService.SaveAsync(profile, BuildClearedAutomationSettings(profile.Id));
        }

        if (selected.Count > 0)
        {
            await _automationService.ReloadAsync();
            RefreshAutomationConfigItems();
            SetAutomationStatus(T($"已清空 {selected.Count} 个自动化配置。", $"Cleared {selected.Count} automation configs."));
        }
    }

    private async void OnRestrictionRefreshClick(object? sender, RoutedEventArgs e)
    {
        await RefreshRestrictionAsync();
    }

    private async void OnRestrictionSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveRestrictionAsync();
    }

    private async void OnRestrictionEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RestrictionProfileConfigItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is null)
        {
            return;
        }

        try
        {
            await ShowRestrictionEditorAsync(profile);
        }
        catch (Exception ex)
        {
            SetRestrictionStatus(T($"限制配置加载失败：{ex.Message}", $"Failed to load restrictions: {ex.Message}"));
        }
    }

    private void OnRestrictionBackClick(object? sender, RoutedEventArgs e)
    {
        ShowRestrictionList();
    }

    private void OnRestrictionAddWhitelistClick(object? sender, RoutedEventArgs e)
    {
        AddRestrictionModId(
            RestrictionWhitelistInputTextBox,
            _restrictionWhitelistItems,
            _restrictionBlacklistItems,
            isBlacklist: false);
    }

    private void OnRestrictionAddBlacklistClick(object? sender, RoutedEventArgs e)
    {
        AddRestrictionModId(
            RestrictionBlacklistInputTextBox,
            _restrictionBlacklistItems,
            _restrictionWhitelistItems,
            isBlacklist: true);
    }

    private void OnRestrictionRemoveWhitelistClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RestrictionModIdItem item })
        {
            _restrictionWhitelistItems.Remove(item);
        }
    }

    private void OnRestrictionRemoveBlacklistClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RestrictionModIdItem item })
        {
            _restrictionBlacklistItems.Remove(item);
        }
    }

    private void AddRestrictionModId(
        TextBox input,
        ObservableCollection<RestrictionModIdItem> target,
        ObservableCollection<RestrictionModIdItem> opposite,
        bool isBlacklist)
    {
        var modId = NormalizeRestrictionModId(input.Text);
        if (string.IsNullOrWhiteSpace(modId))
        {
            SetRestrictionStatus(T("请输入 Mod ID。", "Enter a Mod ID."));
            return;
        }

        if (isBlacklist && modId.Equals("launchergorestriction", StringComparison.OrdinalIgnoreCase))
        {
            SetRestrictionStatus(T("不能将 launchergorestriction 加入黑名单。", "launchergorestriction cannot be blacklisted."));
            return;
        }

        if (target.Any(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
        {
            SetRestrictionStatus(T($"{modId} 已存在。", $"{modId} already exists."));
            return;
        }

        if (RestrictionBlacklistEnabledCheckBox.IsChecked == true &&
            RestrictionForceWhitelistCheckBox.IsChecked == true &&
            opposite.Any(item => item.ModId.Equals(modId, StringComparison.OrdinalIgnoreCase)))
        {
            SetRestrictionStatus(T(
                $"{modId} 已在另一份启用的名单中。",
                $"{modId} is already in the other enabled list."));
            return;
        }

        target.Add(new RestrictionModIdItem(modId));
        input.Text = string.Empty;
        SetRestrictionStatus(T($"已添加：{modId}", $"Added: {modId}"), notify: false);
    }

    private static string NormalizeRestrictionModId(string? value)
    {
        var normalized = value?.Trim().Trim('"', '\'', ',', ';') ?? string.Empty;
        var versionSeparator = normalized.IndexOf('@');
        if (versionSeparator > 0)
        {
            normalized = normalized[..versionSeparator];
        }

        return normalized.Trim().ToLowerInvariant();
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

    private void OnAutomationAddCommandClick(object? sender, RoutedEventArgs e)
    {
        _automationCommandItems.Add(new ScheduledCommandItem());
    }

    private void OnAutomationRemoveCommandClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: ScheduledCommandItem item })
        {
            _automationCommandItems.Remove(item);
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
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var path = ModZipPathTextBox.Text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path))
        {
            SetModStatus(T("请输入 Mod ZIP 路径。", "Enter a Mod ZIP path."));
            return;
        }

        try
        {
            var imported = await _instanceModService.ImportModZipAsync(profile, path);
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T($"已导入：{imported.ModId}", $"Imported: {imported.ModId}"));
        }
        catch (Exception ex)
        {
            SetModStatus(T($"导入失败：{ex.Message}", $"Import failed: {ex.Message}"));
        }
    }

    private async void OnDeployMapModClick(object? sender, RoutedEventArgs e)
    {
        InstanceProfile? profile = null;
        if (!string.IsNullOrWhiteSpace(_editingOpenInfoProfileId))
        {
            profile = _profileService.GetProfileById(_editingOpenInfoProfileId);
        }

        if (profile is null && !TryGetLockedLaunchTarget(out profile, out _))
        {
            var runningProfileId = _serverProcessService.GetCachedStatuses()
                .FirstOrDefault(static status => status.IsRunning && !string.IsNullOrWhiteSpace(status.ProfileId))
                ?.ProfileId;
            profile = string.IsNullOrWhiteSpace(runningProfileId)
                ? null!
                : _profileService.GetProfileById(runningProfileId);
            if (profile is null)
            {
                SetConnectionStatus(T("请先进入某个开放API配置页面，或启动一个服务器后再部署地图模组。", "Open an Open API profile config page, or start a server before deploying the map mod."));
                return;
            }
        }

        try
        {
            await _serverMapService.EnsureMapModDeployedAsync(profile);
            SetConnectionStatus(T(
                $"地图模组已部署到：{profile.Name}。默认只监听 127.0.0.1；远程 ServerMap 通过开放信息上报接收地图数据。首次完整渲染可在游戏内执行 /servermap colormap 后再执行 /servermap fullrender。",
                $"Map mod deployed to: {profile.Name}. It listens on 127.0.0.1 by default; remote ServerMap receives map data through Open Info reports. For the first full render, run /servermap colormap in game, then /servermap fullrender."));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"部署地图模组失败：{ex.Message}", $"Map mod deploy failed: {ex.Message}"));
        }
    }

    private async void OnDeleteSelectedModsClick(object? sender, RoutedEventArgs e)
    {
        if (ModProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetModStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var selected = ModsListBox.SelectedItems?
            .OfType<ModListItem>()
            .Select(ModListItem.ToModel)
            .ToList() ?? [];
        if (selected.Count == 0)
        {
            SetModStatus(T("请先选择模组。", "Select mods first."));
            return;
        }

        try
        {
            var deleted = await _instanceModService.DeleteModsAsync(profile, selected);
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T($"已删除 {deleted} 个模组。", $"Deleted {deleted} mods."));
        }
        catch (Exception ex)
        {
            SetModStatus(T($"删除失败：{ex.Message}", $"Delete failed: {ex.Message}"));
        }
    }

    private async void OnRefreshModsClick(object? sender, RoutedEventArgs e)
    {
        await RefreshModsAsync();
    }

    private void OnOpenModConfigPathClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
        {
            SetModStatus(T("配置路径无效。", "Invalid config path."));
            return;
        }

        try
        {
            var primaryPath = path.Split(" | ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(primaryPath) || (!File.Exists(primaryPath) && !Directory.Exists(primaryPath)))
            {
                SetModStatus(T($"配置路径不存在：{path}", $"Config path not found: {path}"));
                return;
            }

            OpenLocalFile(primaryPath);
        }
        catch (Exception ex)
        {
            SetModStatus(T($"打开配置路径失败：{ex.Message}", $"Failed to open config path: {ex.Message}"));
        }
    }

    private void OnOpenConfigFileClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
        {
            ShowToast(T("配置路径无效。", "Invalid config path."));
            return;
        }

        try
        {
            if (File.Exists(path) || Directory.Exists(path))
            {
                OpenLocalFile(path);
                return;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
            {
                OpenLocalFile(directory);
                return;
            }

            ShowToast(T($"配置路径不存在：{path}", $"Config path not found: {path}"));
        }
        catch (Exception ex)
        {
            ShowToast(T($"打开配置失败：{ex.Message}", $"Open config failed: {ex.Message}"));
        }
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
            await LoadModsForSelectedProfileAsync();
            SetModStatus(T($"切换失败：{ex.Message}", $"Toggle failed: {ex.Message}"));
        }
    }

    private async void OnAuthProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_isRefreshingAuth)
        {
            return;
        }

        if (AuthEditorPanel.IsVisible && AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthForProfileAsync(profile);
        }
    }

    private async void OnAuthSaveClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetAuthStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectAuthSettings();
            await _serverAuthService.SaveSettingsAsync(profile, settings);
            if (settings.Enabled)
            {
                await _serverAuthService.EnsureAuthModDeployedAsync(profile, enableMod: true);
            }
            else
            {
                await _serverAuthService.SetAuthModEnabledAsync(profile, enabled: false);
            }

            await LoadAuthForProfileAsync(profile);
            SetAuthStatus(T("认证配置已保存。", "Auth settings saved."));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"保存失败：{ex.Message}", $"Save failed: {ex.Message}"));
        }
    }

    private async void OnAuthRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (AuthEditorPanel.IsVisible && AuthProfileComboBox.SelectedItem is InstanceProfile profile)
        {
            await LoadAuthForProfileAsync(profile);
            return;
        }

        await RefreshAuthProfilesAsync();
    }

    private async void OnAuthEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ProfileConfigListItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
        {
            await ShowAuthEditorAsync(profile);
        }
    }

    private void OnAuthBackClick(object? sender, RoutedEventArgs e)
    {
        ShowAuthList();
    }

    private async void OnAuthClearClick(object? sender, RoutedEventArgs e)
    {
        var selected = _authConfigItems.Where(static item => item.IsSelected).ToList();
        foreach (var item in selected)
        {
            var profile = _profileService.GetProfileById(item.ProfileId);
            if (profile is null)
            {
                continue;
            }

            await _serverAuthService.SaveSettingsAsync(profile, BuildClearedAuthSettings());
            await _serverAuthService.SetAuthModEnabledAsync(profile, enabled: false);
        }

        if (selected.Count > 0)
        {
            RefreshAuthConfigItems();
            SetAuthStatus(T($"已清空 {selected.Count} 个安全配置。", $"Cleared {selected.Count} security configs."));
        }
    }

    private async void OnAuthDeployClick(object? sender, RoutedEventArgs e)
    {
        if (AuthProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            SetAuthStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var settings = CollectAuthSettings();
            await _serverAuthService.EnsureAuthModDeployedAsync(profile, enableMod: settings.Enabled);
            await LoadAuthForProfileAsync(profile);
            SetAuthStatus(settings.Enabled
                ? T("认证模组已部署并启用。", "Auth mod deployed and enabled.")
                : T("认证模组已部署，但认证未启用，模组保持禁用。", "Auth mod deployed, but auth is disabled so the mod remains disabled."));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"部署失败：{ex.Message}", $"Deploy failed: {ex.Message}"));
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
            SetAuthStatus(changed
                ? T($"已清空 {item.PlayerName} 的密码。", $"Cleared password for {item.PlayerName}.")
                : T($"未找到玩家：{item.PlayerName}", $"Player not found: {item.PlayerName}"));
        }
        catch (Exception ex)
        {
            SetAuthStatus(T($"清空失败：{ex.Message}", $"Clear failed: {ex.Message}"));
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

    private void OnOsqBackClick(object? sender, RoutedEventArgs e)
    {
        ShowOpenInfoList();
    }

    private async void OnOsqToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_isTogglingOsq)
            return;

        _isTogglingOsq = true;
        OsqToggleButton.IsEnabled = false;
        try
        {
            if (_openServerQueryService.GetRuntimeStatus().IsListening)
            {
                OsqEnabledCheckBox.IsChecked = false;
                SaveOpenServerQuerySettings(updateStatus: false, refreshEditor: false);
                await StopOpenInfoAsync();
                return;
            }

            OsqEnabledCheckBox.IsChecked = true;
            SaveOpenServerQuerySettings(updateStatus: false, refreshEditor: false);
            await StartOpenInfoAsync();

            if (!_openServerQueryService.GetRuntimeStatus().IsListening)
            {
                OsqEnabledCheckBox.IsChecked = false;
                SaveOpenServerQuerySettings(updateStatus: false, refreshEditor: false);
            }
        }
        finally
        {
            _isTogglingOsq = false;
            OsqToggleButton.IsEnabled = true;
            UpdateOsqToggleButtonText();
        }
    }

    private async void OnOsqConfigSaveClick(object? sender, RoutedEventArgs e)
    {
        await SaveOpenInfoEditorAsync();
    }

    private void OnOsqConfigRefreshClick(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(_editingOpenInfoProfileId))
        {
            ShowOpenInfoList();
            return;
        }

        var profile = _profileService.GetProfileById(_editingOpenInfoProfileId);
        if (profile is not null)
        {
            ShowOpenInfoEditor(profile);
        }
    }

    private void OnOsqEditConfigClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: OpenServerQueryProfileConfigItem item })
        {
            return;
        }

        var profile = _profileService.GetProfileById(item.ProfileId);
        if (profile is not null)
        {
            ShowOpenInfoEditor(profile);
        }
    }

    private async void OnOsqEndpointSwitchClick(object? sender, RoutedEventArgs e)
    {
        if (_isApplyingConnectionSettings)
        {
            return;
        }

        if (sender is not ToggleSwitch { Tag: OpenServerQueryProfileConfigItem item } toggleSwitch)
        {
            return;
        }

        item.Enabled = toggleSwitch.IsChecked == true;

        await SaveOpenInfoSettingsAndReloadIfRunningAsync(updateStatus: false, refreshEditor: false);
        SetConnectionStatus(BuildOpenInfoRuntimeStatusText());
    }

    private async Task StartOpenInfoAsync()
    {
        SaveOpenServerQuerySettings(updateStatus: false, refreshEditor: false);
        try
        {
            var settings = _preferencesService.Load().OpenServerQuery;
            if (!settings.Enabled)
            {
                SetConnectionStatus(T("开放信息未启用。", "Open Info is disabled."));
                return;
            }

            await _openServerQueryService.StartAsync(ToOpenServerQueryRuntimeSettings(settings));
            var status = await WaitForOpenInfoListeningAsync(TimeSpan.FromSeconds(2));
            SetConnectionStatus(status.IsListening
                ? BuildOpenInfoRuntimeStatusText()
                : T(
                    $"开放信息正在启动：{settings.ListenPrefix}",
                    $"Open Info is starting: {settings.ListenPrefix}"));
        }
        catch (Exception ex)
        {
            SetConnectionStatus(T($"开放信息启动失败：{ex.Message}", $"Open Info start failed: {ex.Message}"));
        }
        finally
        {
            UpdateOsqToggleButtonText();
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async Task SaveOpenInfoEditorAsync()
    {
        if (string.IsNullOrWhiteSpace(_editingOpenInfoProfileId))
        {
            SaveOpenServerQuerySettings();
            return;
        }

        var profile = _profileService.GetProfileById(_editingOpenInfoProfileId);
        if (profile is null)
        {
            SetConnectionStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var preferences = _preferencesService.Load();
        var endpoint = CollectOpenInfoEndpointConfig(profile);
        var endpoints = preferences.OpenServerQuery.Endpoints
            .Where(existing => !existing.ProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
            .ToList();
        endpoints.Add(endpoint);
        preferences.OpenServerQuery.Endpoints = endpoints;
        preferences.OpenServerQuery.AllowInsecureHttp = OsqAllowInsecureHttpCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludeServerInfo = OsqIncludeServerInfoCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludePlayers = OsqIncludePlayersCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludePlayerEvents = OsqIncludeEventsCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludeChats = OsqIncludeChatsCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludeNotifications = OsqIncludeNotificationsCheckBox.IsChecked == true;
        preferences.OpenServerQuery.IncludeMapData = OsqIncludeMapCheckBox.IsChecked == true;
        _preferencesService.Save(preferences);
        SaveOpenInfoProfileConfigFile(profile, endpoint);
        await ReloadOpenInfoIfRunningAsync();
        RefreshOpenInfoConfigItems();
        SetConnectionStatus(T("开放API配置已保存。", "Open API config saved."));
    }

    private async Task SaveOpenInfoSettingsAndReloadIfRunningAsync(bool updateStatus = true, bool refreshEditor = true)
    {
        SaveOpenServerQuerySettings(updateStatus: false, refreshEditor);
        await ReloadOpenInfoIfRunningAsync();

        if (updateStatus)
        {
            SetConnectionStatus(T("开放API配置已保存。", "Open API config saved."));
        }
    }

    private async Task ReloadOpenInfoIfRunningAsync()
    {
        var wasListening = _openServerQueryService.GetRuntimeStatus().IsListening;
        if (wasListening)
        {
            await _openServerQueryService.StopAsync(TimeSpan.FromSeconds(5));
        }

        var settings = _preferencesService.Load().OpenServerQuery;
        if (settings.Enabled)
        {
            await _openServerQueryService.StartAsync(ToOpenServerQueryRuntimeSettings(settings));
            await WaitForOpenInfoListeningAsync(TimeSpan.FromSeconds(2));
        }

        UpdateOsqToggleButtonText();
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private async Task<OpenServerQueryRuntimeStatus> WaitForOpenInfoListeningAsync(TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow + timeout;
        OpenServerQueryRuntimeStatus status;
        do
        {
            status = _openServerQueryService.GetRuntimeStatus();
            if (status.IsListening)
                return status;

            await Task.Delay(100);
        } while (DateTimeOffset.UtcNow < deadline);

        return _openServerQueryService.GetRuntimeStatus();
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
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async void OnRobotSaveClick(object? sender, RoutedEventArgs e) => await SaveRobotSettingsAndReloadIfRunningAsync();

    private void OnRobotRefreshClick(object? sender, RoutedEventArgs e)
    {
        RefreshConnectionSettingsEditor();
        RefreshConnectionRuntimeStatus();
    }

    private void OnRobotClearClick(object? sender, RoutedEventArgs e)
    {
        var preferences = _preferencesService.Load();
        preferences.Robot = BuildClearedRobotSettings();
        _preferencesService.Save(preferences);
        ApplyRobotSettings(preferences.Robot);
        SetConnectionStatus(T("QQ机器人配置已清空。", "QQ robot configuration cleared."));
    }

    private void OnRobotBindingAddClick(object? sender, RoutedEventArgs e)
    {
        _robotBindingItems.Add(new RobotProfileBindingItem(
            _robotProfileItems,
            _robotProfileItems.FirstOrDefault()?.Id ?? string.Empty,
            string.Empty,
            string.Empty));
    }

    private void OnRobotBindingRemoveClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RobotProfileBindingItem item })
        {
            _robotBindingItems.Remove(item);
        }

        if (_robotBindingItems.Count == 0)
        {
            OnRobotBindingAddClick(sender, e);
        }
    }

    private async void OnRobotToggleClick(object? sender, RoutedEventArgs e)
    {
        if (_isTogglingRobot)
            return;

        _isTogglingRobot = true;
        RobotToggleButton.IsEnabled = false;
        try
        {
            if (_robotService.GetCurrentStatus().IsRunning)
            {
                RobotToggleButton.Content = T("启动", "Start");
                await StopRobotAsync();
                return;
            }

            RobotToggleButton.Content = T("停止", "Stop");
            await StartRobotAsync();
        }
        finally
        {
            _isTogglingRobot = false;
            RobotToggleButton.IsEnabled = true;
            UpdateRobotToggleButtonText();
        }
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
            UpdateCardValues(_serverProcessService.GetCachedStatus());
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
            UpdateCardValues(_serverProcessService.GetCachedStatus());
        }
    }

    private async void OnLaunchServerClick(object? sender, RoutedEventArgs e)
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var statuses = _serverProcessService.GetCachedStatuses();
        if (!statuses.Any(static status => status.IsRunning) || HasPendingLaunchTargets(statuses))
        {
            await StartSelectedServersAsync();
            return;
        }

        await StopServerFromLaunchButtonAsync();
    }

    private async void OnDashboardServerActionClick(object? sender, RoutedEventArgs e)
    {
        if (_isStoppingOrStarting || sender is not Button { Tag: DashboardServerItem item })
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(item.ProfileId))
        {
            return;
        }

        if (item.IsRunning)
        {
            await StopDashboardServerAsync(item.ProfileId);
            return;
        }

        await StartDashboardServerAsync(item.ProfileId);
    }

    private async Task StartDashboardServerAsync(string profileId)
    {
        var profile = _profileService.GetProfileById(profileId.Trim());
        if (profile is null)
        {
            ShowToast(T("未找到服务器档案。", "Server profile not found."));
            return;
        }

        var savePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(savePath))
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            ShowToast(T($"{profile.Name} 未绑定存档，请先绑定后启动。", $"{profile.Name} has no save bound. Bind a save before starting."));
            return;
        }

        SetLaunchOperationBusy(T("启动中...", "Starting..."));
        try
        {
            if (_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
            {
                ShowToast(T($"{profile.Name} 已在运行。", $"{profile.Name} is already running."));
                return;
            }

            var launchableProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
            await StartServerProfileWithTimeoutAsync(launchableProfile);
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 启动/停止失败：{ex.Message}");
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private async Task StopDashboardServerAsync(string profileId)
    {
        var profile = _profileService.GetProfileById(profileId.Trim());
        SetLaunchOperationBusy(T("停止中...", "Stopping..."));
        try
        {
            AppendConsoleLine(T(
                $"[system] 正在停止服务器：{profile?.Name ?? profileId}",
                $"[system] Stopping server: {profile?.Name ?? profileId}"));
            await _serverProcessService.StopAsync(profileId, TimeSpan.FromSeconds(20));
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 启动/停止失败：{ex.Message}");
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private void OnLaunchServerPointerEntered(object? sender, PointerEventArgs e)
    {
        if (_serverProcessService.GetCachedStatuses().Any(static status => status.IsRunning) || _launchTargetItems.Count > 0)
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
        SetLaunchOperationBusy(T("停止中...", "Stopping..."));
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
            ClearLaunchOperationBusy();
        }
    }

    private async Task StartSelectedServersAsync()
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var selectedIds = LoadLaunchProfileIds();
        if (selectedIds.Count == 0)
        {
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Saves);
            LaunchSelectionSummaryTextBlock.Text = T("请先添加要启动的服务器", "Add servers to start first");
            return;
        }

        SetLaunchOperationBusy(T("启动中...", "Starting..."));
        try
        {
            var runningIds = _serverProcessService.GetCachedStatuses()
                .Where(static status => status.IsRunning)
                .Select(static status => status.ProfileId ?? string.Empty)
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var startedCount = 0;
            foreach (var profileId in selectedIds)
            {
                if (runningIds.Contains(profileId))
                {
                    continue;
                }

                var profile = _profileService.GetProfileById(profileId);
                if (profile is null)
                {
                    continue;
                }

                var savePath = NormalizeFullPath(profile.ActiveSaveFile);
                if (string.IsNullOrWhiteSpace(savePath))
                {
                    SelectTab(MainTab.InstanceManage);
                    SelectInstanceManageTab(InstanceManageTab.Saves);
                    ShowToast(T($"{profile.Name} 未绑定存档，请先绑定后启动。", $"{profile.Name} has no save bound. Bind a save before starting."));
                    return;
                }

                var launchableProfile = await EnsureLaunchableProfileSaveAsync(profile, savePath);
                await StartServerProfileWithTimeoutAsync(launchableProfile);
                startedCount++;
            }

            if (startedCount == 0)
            {
                ShowToast(T("选择的服务器均已运行。", "Selected servers are already running."));
            }
        }
        catch (Exception ex)
        {
            AppendConsoleLine($"[system] 启动/停止失败：{ex.Message}");
        }
        finally
        {
            ClearLaunchOperationBusy();
        }
    }

    private async Task<InstanceProfile> EnsureLaunchableProfileSaveAsync(InstanceProfile profile, string preferredSavePath)
    {
        var normalizedPreferredSavePath = NormalizeFullPath(preferredSavePath);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredSavePath))
        {
            var saves = await _saveService.GetSavesAsync(profile);
            var preferredSave = saves.FirstOrDefault(save =>
                NormalizeFullPath(save.FullPath).Equals(normalizedPreferredSavePath, StringComparison.OrdinalIgnoreCase));
            if (preferredSave is not null)
            {
                await PrepareProfileSaveForLaunchAsync(profile, preferredSave.FullPath);
                return _profileService.GetProfileById(profile.Id) ?? profile;
            }

            await PrepareProfileSaveForLaunchAsync(profile, normalizedPreferredSavePath);
            return _profileService.GetProfileById(profile.Id) ?? profile;
        }

        var currentSavePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (!string.IsNullOrWhiteSpace(currentSavePath))
        {
            await PrepareProfileSaveForLaunchAsync(profile, currentSavePath);
        }

        return _profileService.GetProfileById(profile.Id) ?? profile;
    }

    private async Task PrepareProfileSaveForLaunchAsync(InstanceProfile profile, string savePath)
    {
        var normalizedSavePath = NormalizeFullPath(savePath);
        if (string.IsNullOrWhiteSpace(normalizedSavePath))
        {
            return;
        }

        if (File.Exists(normalizedSavePath))
        {
            var fileInfo = new FileInfo(normalizedSavePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedSavePath);
            }
        }

        await _saveService.SetActiveSaveAsync(profile, normalizedSavePath);
    }

    private async Task StartServerProfileWithTimeoutAsync(InstanceProfile profile)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(ServerStartTimeoutSeconds));
        try
        {
            var startTask = Task.Run(
                () => _serverProcessService.StartAsync(profile, timeoutCts.Token),
                CancellationToken.None);
            var completedTask = await Task.WhenAny(
                startTask,
                Task.Delay(TimeSpan.FromSeconds(ServerStartTimeoutSeconds)));
            if (!ReferenceEquals(completedTask, startTask))
            {
                await timeoutCts.CancelAsync();
                throw new TimeoutException(T(
                    $"启动服务器超时：{profile.Name}",
                    $"Server start timed out: {profile.Name}"));
            }

            await startTask;
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
        {
            throw new TimeoutException(T(
                $"启动服务器超时：{profile.Name}",
                $"Server start timed out: {profile.Name}"));
        }
    }

    private void SetLaunchOperationBusy(string text)
    {
        _isStoppingOrStarting = true;
        UpdateDashboardStatus(_serverProcessService.GetCachedStatus());
    }

    private void ClearLaunchOperationBusy()
    {
        _isStoppingOrStarting = false;
        UpdateCardValues(_serverProcessService.GetCachedStatus());
    }

    private async void OnSendCommandClick(object? sender, RoutedEventArgs e)
    {
        await SendCommandFromInputAsync();
    }

    private void OnLaunchAddProfileClick(object? sender, RoutedEventArgs e)
    {
        if (LaunchAddProfileComboBox.SelectedItem is not InstanceProfile profile)
        {
            return;
        }

        var ids = LoadLaunchProfileIds();
        ids.Add(profile.Id);
        SaveLaunchProfileIds(ids);
        LaunchAddProfileComboBox.SelectedIndex = -1;
    }

    private void OnLaunchRemoveSelectedProfileClick(object? sender, RoutedEventArgs e)
    {
        var selected = _launchTargetItems.FirstOrDefault(static item => item.IsSelected)
                       ?? _launchTargetItems.LastOrDefault();
        if (selected is null)
        {
            return;
        }

        var ids = LoadLaunchProfileIds();
        ids.Remove(selected.ProfileId);
        SaveLaunchProfileIds(ids);
    }

    private void OnLaunchTargetChipClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton { Tag: LaunchTargetItem item } button)
        {
            return;
        }

        foreach (var target in _launchTargetItems)
        {
            target.IsSelected = false;
        }

        item.IsSelected = button.IsChecked == true;
    }

    private void OnConsoleServerSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (ConsoleServerComboBox.SelectedItem is not ConsoleServerItem item)
        {
            return;
        }

        _selectedConsoleProfileId = item.ProfileId;
        _ = EnsureConsoleReplayLoadedAsync(item.ProfileId);
        RefreshConsoleText();
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
            if (ConsoleServerComboBox.SelectedItem is ConsoleServerItem item &&
                !string.IsNullOrWhiteSpace(item.ProfileId))
            {
                await _serverProcessService.SendCommandAsync(item.ProfileId, command);
                return;
            }

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
            var selectedProfileId = !string.IsNullOrWhiteSpace(_editingConfigProfileId)
                ? _editingConfigProfileId
                : (ConfigProfileComboBox.SelectedItem as InstanceProfile)?.Id;
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
            SetConfigPath(null);
            ConfigContentHost.IsEnabled = false;
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(T("暂无档案，请先创建档案。", "No profile found. Create a profile first."));
            return;
        }

        await LoadConfigForProfileAsync(targetProfile);
    }

    private async Task OpenProfileConfigEditorAsync(string profileId)
    {
        var normalizedProfileId = profileId.Trim();
        if (string.IsNullOrWhiteSpace(normalizedProfileId))
        {
            SetConfigStatus(T("未找到要修改的档案。", "Profile to edit was not found."));
            return;
        }

        var profile = _profileService.GetProfileById(normalizedProfileId);
        if (profile is null)
        {
            SetConfigStatus(T("未找到要修改的档案。", "Profile to edit was not found."));
            return;
        }

        _editingConfigProfileId = profile.Id;
        _pendingConfigLoadProfileId = profile.Id;
        SelectInstanceManageTab(InstanceManageTab.Config);
        var profiles = _profileService.GetProfiles();
        profile = profiles.FirstOrDefault(item =>
                      item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
                  ?? profile;
        ConfigProfileComboBox.ItemsSource = profiles;
        ConfigProfileComboBox.SelectedItem = profile;
        SetConfigHasProfiles(profiles.Count > 0);
        try
        {
            await LoadConfigForProfileAsync(profile);
        }
        finally
        {
            if (_pendingConfigLoadProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
            {
                _pendingConfigLoadProfileId = string.Empty;
            }
        }
    }

    private void SetConfigHasProfiles(bool hasProfiles)
    {
        ConfigScrollViewer.IsVisible = hasProfiles;
        ConfigEmptyPanel.IsVisible = !hasProfiles;
        ConfigRefreshButton.IsEnabled = true;
        ConfigImportButton.IsEnabled = hasProfiles;
        ConfigSaveButton.IsEnabled = hasProfiles && _isConfigLoaded;
        ConfigContentHost.IsEnabled = hasProfiles && _isConfigLoaded;
    }

    private async Task LoadConfigForProfileAsync(InstanceProfile selectedProfile)
    {
        var profile = _profileService.GetProfileById(selectedProfile.Id) ?? selectedProfile;
        var loadVersion = ++_configLoadVersion;
        var configPath = GetConfigPath(profile);
        _isLoadingConfig = true;
        _isConfigLoaded = false;
        _loadedConfigProfileId = string.Empty;
        ConfigSaveButton.IsEnabled = false;
        ConfigContentHost.IsEnabled = false;
        SetConfigPath(profile);
        try
        {
            var rawJson = await _instanceServerConfigService.LoadRawJsonAsync(profile);
            var root = ParseConfigRootForUi(rawJson, configPath);
            var serverSettings = BuildConfigServerSettings(root);
            var worldSettings = BuildConfigWorldSettings(profile, root);
            var worldRules = BuildConfigWorldRules(root);

            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            LoadConfigGameLanguageZh(profile);
            ApplyConfigServerSettings(serverSettings);
            if (!await LoadConfigSavesAsync(profile, worldSettings.SaveFileLocation, loadVersion))
            {
                return;
            }

            ApplyConfigWorldSettings(worldSettings);
            RebuildConfigWorldRules(worldRules);
            UpdateConfigWorldGeneratedState();
            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            _isConfigLoaded = true;
            _loadedConfigProfileId = profile.Id;
            ConfigSaveButton.IsEnabled = true;
            ConfigContentHost.IsEnabled = true;
            SetConfigStatus(
                T($"已加载配置：{profile.Name}", $"Loaded configuration: {profile.Name}") + Environment.NewLine +
                T($"配置路径：{configPath}", $"Config path: {configPath}"));
        }
        catch (Exception ex)
        {
            if (!IsActiveConfigLoad(loadVersion, profile.Id))
            {
                return;
            }

            ClearConfigForm();
            ConfigContentHost.IsEnabled = false;
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(FormatConfigLoadFailure(profile, ex));
        }
        finally
        {
            if (loadVersion == _configLoadVersion)
            {
                ConfigContentHost.IsEnabled = _isConfigLoaded;
                _isLoadingConfig = false;
            }
        }
    }

    private bool IsActiveConfigLoad(long loadVersion, string profileId)
    {
        return loadVersion == _configLoadVersion &&
               (string.IsNullOrWhiteSpace(_editingConfigProfileId) ||
                _editingConfigProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase));
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

    private async Task<bool> LoadConfigSavesAsync(
        InstanceProfile profile,
        string preferredSavePath,
        long? loadVersion = null)
    {
        var saves = await _saveService.GetSavesAsync(profile);
        if (loadVersion.HasValue && !IsActiveConfigLoad(loadVersion.Value, profile.Id))
        {
            return false;
        }

        _configSaveItems.Clear();
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
        return true;
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

    private static JsonObject ParseConfigRootForUi(string rawJson, string configPath)
    {
        try
        {
            return JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidDataException($"配置根节点必须是 JSON 对象：{configPath}");
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"配置文件无法解析：{configPath}", ex);
        }
    }

    private ServerCommonSettings BuildConfigServerSettings(JsonObject root)
    {
        return new ServerCommonSettings
        {
            ServerName = ReadConfigString(root["ServerName"], "Vintage Story Server"),
            ServerDescription = ReadConfigNullableString(root["ServerDescription"]),
            ServerUrl = ReadConfigNullableString(root["ServerUrl"]),
            Ip = ReadConfigNullableString(root["Ip"]),
            Port = ReadConfigInt(root["Port"], 42420),
            MaxClients = ReadConfigInt(root["MaxClients"], 16),
            MaxClientsInQueue = ReadConfigInt(root["MaxClientsInQueue"], 0),
            Password = ReadConfigNullableString(root["Password"]),
            AdvertiseServer = ReadConfigBool(root["AdvertiseServer"], false),
            WhitelistMode = ReadConfigInt(root["WhitelistMode"], 0),
            Upnp = ReadConfigBool(root["Upnp"], false),
            AllowPvP = ReadConfigBool(root["AllowPvP"], true),
            AllowFireSpread = ReadConfigBool(root["AllowFireSpread"], true),
            AllowFallingBlocks = ReadConfigBool(root["AllowFallingBlocks"], true),
            PassTimeWhenEmpty = ReadConfigBool(root["PassTimeWhenEmpty"], false),
            WarnClientsAfterAfkSeconds = ReadConfigInt(root["WarnClientsAfterAfkSeconds"], 0),
            KickClientsAfterAfkSeconds = ReadConfigInt(root["KickClientsAfterAfkSeconds"], 0),
            ClientConnectionTimeout = ReadConfigInt(root["ClientConnectionTimeout"], 150),
            MaxChunkRadius = ReadConfigInt(root["MaxChunkRadius"], 12),
            DieBelowDiskSpaceMb = ReadConfigInt(root["DieBelowDiskSpaceMb"], 400),
            CorruptionProtection = ReadConfigBool(root["CorruptionProtection"], true),
            RegenerateCorruptChunks = ReadConfigBool(root["RegenerateCorruptChunks"], false),
            StartupCommands = ReadConfigString(root["StartupCommands"], string.Empty),
            VerifyPlayerAuth = ReadConfigBool(root["VerifyPlayerAuth"], true),
            ServerLanguage = ReadConfigString(root["ServerLanguage"], ResolveDefaultServerLanguage()),
            DefaultRoleCode = ReadConfigString(root["DefaultRoleCode"], "suplayer"),
            WelcomeMessage = ReadConfigString(root["WelcomeMessage"], string.Empty)
        };
    }

    private static WorldSettings BuildConfigWorldSettings(InstanceProfile profile, JsonObject root)
    {
        var worldConfig = root["WorldConfig"] as JsonObject ?? [];
        var worldRules = worldConfig["WorldConfiguration"] as JsonObject ?? [];
        var mapSizeY = ReadConfigNullableInt(worldConfig["MapSizeY"]) ?? ReadConfigNullableInt(worldRules["worldHeight"]);

        return new WorldSettings
        {
            Seed = ReadConfigString(worldConfig["Seed"], "123456789"),
            WorldName = ReadConfigString(worldConfig["WorldName"], "A new world"),
            SaveFileLocation = ReadConfigString(worldConfig["SaveFileLocation"], ResolveCurrentConfigSaveFilePath(profile)),
            PlayStyle = ReadConfigString(worldConfig["PlayStyle"], "surviveandbuild"),
            WorldType = ReadConfigString(worldConfig["WorldType"], "standard"),
            WorldHeight = mapSizeY ?? 256
        };
    }

    private static IReadOnlyList<WorldRuleValue> BuildConfigWorldRules(JsonObject root)
    {
        var worldConfig = root["WorldConfig"] as JsonObject ?? [];
        var worldRules = worldConfig["WorldConfiguration"] as JsonObject ?? [];

        return WorldRuleCatalog.DefaultRules
            .Select(rule => new WorldRuleValue
            {
                Definition = rule,
                Value = ReadConfigFlexibleString(worldRules[rule.Key])
                        ?? ReadConfigRuleFallbackValue(rule.Key, root, worldConfig)
                        ?? rule.DefaultValue
            })
            .ToList();
    }

    private void ClearConfigForm()
    {
        _isConfigLoaded = false;
        _loadedConfigProfileId = string.Empty;
        ConfigSaveButton.IsEnabled = false;
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
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            await RefreshConfigProfilesAsync();
            return;
        }

        await LoadConfigForProfileAsync(profile);
    }

    private void OnConfigBackClick(object? sender, RoutedEventArgs e)
    {
        _editingConfigProfileId = string.Empty;
        _pendingConfigLoadProfileId = string.Empty;
        SelectInstanceManageTab(InstanceManageTab.Profiles);
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
            InvalidateDashboardSettingsCache(profile);
            await LoadConfigForProfileAsync(profile);
            SetConfigStatus(
                T($"已导入配置：{Path.GetFileName(path)}", $"Configuration imported: {Path.GetFileName(path)}") +
                Environment.NewLine +
                T($"配置路径：{GetConfigPath(profile)}", $"Config path: {GetConfigPath(profile)}"));
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

        if (!_isConfigLoaded || !_loadedConfigProfileId.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
        {
            ConfigSaveButton.IsEnabled = false;
            SetConfigStatus(
                T("配置尚未成功加载，已禁止保存以避免覆盖原文件。", "Configuration has not loaded successfully; saving is disabled to avoid overwriting the original file.") +
                Environment.NewLine +
                T($"配置路径：{GetConfigPath(profile)}", $"Config path: {GetConfigPath(profile)}"));
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
            UpdateDashboardSettingsCache(profile, serverSettings);

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
            _isConfigLoaded = true;
            _loadedConfigProfileId = profile.Id;
            SetConfigStatus(
                T("配置已保存。", "Configuration saved.") + Environment.NewLine +
                T($"配置路径：{GetConfigPath(profile)}", $"Config path: {GetConfigPath(profile)}"));
        }
        catch (Exception ex)
        {
            _isConfigLoaded = false;
            _loadedConfigProfileId = string.Empty;
            SetConfigStatus(T($"保存配置失败：{ex.Message}", $"Failed to save configuration: {ex.Message}"));
        }
        finally
        {
            ConfigSaveButton.IsEnabled = _isConfigLoaded;
            ConfigContentHost.IsEnabled = _isConfigLoaded;
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

    private void LoadConfigGameLanguageZh(InstanceProfile profile)
    {
        if (!_isChinese)
        {
            _configGameLanguageZh.Clear();
            _configGameLanguageZhPath = string.Empty;
            return;
        }

        var languagePath = ResolveConfigGameLanguageZhPath(profile) ?? string.Empty;
        if (languagePath.Equals(_configGameLanguageZhPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _configGameLanguageZh.Clear();
        _configGameLanguageZhPath = languagePath;
        if (string.IsNullOrWhiteSpace(languagePath))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(languagePath));
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!property.Name.StartsWith("worldattribute-", StringComparison.OrdinalIgnoreCase) &&
                    !property.Name.StartsWith("worldconfig-", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var text = property.Value.ValueKind == JsonValueKind.String
                    ? NormalizeGameLanguageText(property.Value.GetString())
                    : string.Empty;
                if (!string.IsNullOrWhiteSpace(text))
                {
                    _configGameLanguageZh[property.Name] = text;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load Vintage Story Chinese language file: {Path}", languagePath);
        }
    }

    private string? ResolveConfigGameLanguageZhPath(InstanceProfile profile)
    {
        var preferences = _preferencesService.Load();
        var candidates = new List<string>();

        if (!string.IsNullOrWhiteSpace(preferences.ServerDirectory) &&
            !string.IsNullOrWhiteSpace(profile.Version))
        {
            var installedRoot = Path.Combine(preferences.ServerDirectory, "installed");
            var versionDirectory = Path.Combine(installedRoot, SanitizeConfigPathSegment(profile.Version));
            candidates.Add(Path.Combine(versionDirectory, "assets", "game", "lang", "zh-cn.json"));

            if (Directory.Exists(installedRoot))
            {
                foreach (var directory in Directory.EnumerateDirectories(installedRoot))
                {
                    if (Path.GetFileName(directory).Equals(profile.Version, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(directory).Equals(SanitizeConfigPathSegment(profile.Version), StringComparison.OrdinalIgnoreCase))
                    {
                        candidates.Add(Path.Combine(directory, "assets", "game", "lang", "zh-cn.json"));
                    }
                }
            }
        }

        var current = string.IsNullOrWhiteSpace(profile.DirectoryPath)
            ? null
            : new DirectoryInfo(profile.DirectoryPath);
        for (var depth = 0; current is not null && depth < 6; depth++, current = current.Parent)
        {
            candidates.Add(Path.Combine(current.FullName, "assets", "game", "lang", "zh-cn.json"));
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string SanitizeConfigPathSegment(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join('_', value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? value.Trim() : sanitized.Trim();
    }

    private static string NormalizeGameLanguageText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var normalized = text
            .Replace("<br>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</font>", string.Empty, StringComparison.OrdinalIgnoreCase);
        normalized = Regex.Replace(normalized, "<font[^>]*>", string.Empty, RegexOptions.IgnoreCase);
        return normalized.Trim();
    }

    private bool TryGetConfigGameLanguageText(string key, out string text)
    {
        return _configGameLanguageZh.TryGetValue(key, out text!) &&
               !string.IsNullOrWhiteSpace(text);
    }

    private string ResolveConfigRuleLabelZh(WorldRuleDefinition definition)
    {
        return TryGetConfigGameLanguageText($"worldattribute-{definition.Key}", out var label)
            ? label
            : definition.LabelZh;
    }

    private void RebuildConfigWorldRules(IReadOnlyList<WorldRuleValue> rules)
    {
        _configWorldRuleItems.Clear();
        foreach (var rule in rules)
        {
            var value = rule.Value ?? string.Empty;
            var item = new ConfigWorldRuleItem(
                rule.Definition,
                value,
                _isChinese,
                BuildConfigRuleChoiceOptions(rule.Definition, value),
                ResolveConfigRuleLabelZh(rule.Definition))
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

        if (TryGetConfigGameLanguageText($"worldconfig-{key}-{name}", out var localizedName))
        {
            return localizedName;
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
            "bodytemperatureresistance" when double.TryParse(name, NumberStyles.Float, CultureInfo.InvariantCulture, out _) => $"{name}°C",
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
        var normalized = name.Trim();
        if (TryResolveCommonConfigChoicePattern(normalized, out var patterned))
        {
            return patterned;
        }

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
            "Extremly rare" => "极其稀有",
            "Never" => "不存在",
            "None" => "无",
            "Survival" => "生存",
            "Creative" => "创造",
            "Aggressive" => "主动",
            "Passive" => "被动",
            "Never hostile" => "友好",
            "Hot (28-32°C)" => "炎热 (28~32°C)",
            "Warm (19-23 °C)" => "温暖 (19~23°C)",
            "Temperate (6-14 °C)" => "温和 (6~14°C)",
            "Cool (-5 to 1 °C)" => "寒冷 (-5~1°C)",
            "Icy (-15 to -10°C)" => "严寒 (-15~-10°C)",
            "Sand and gravel" => "沙子和砂砾",
            "Sand, gravel and soil with sideways instability" => "沙子、砂砾和边缘不稳定泥土",
            "Stone and Wood" => "石头、木头和石砖",
            "Most cubic blocks" => "大部分方形方块",
            "ifrepaired" => "只有先用胶水修补时可获取",
            "yes" => "可以，拆除即可获取",
            "no" => "否，拆除总会碎掉",
            "Realistic" => "真实",
            "Patchy" => "片状",
            "Blocked" => "被阻挡",
            "Traversable (Can fall down)" => "可越过/可掉落",
            "Scorching hot" => "灼热",
            "Very hot" => "炎热",
            "Hot" => "热",
            "Cold" => "冷",
            "Very Cold" => "很冷",
            "Snowball earth" => "雪球地球",
            "Super humid" => "潮湿",
            "Very humid" => "湿润",
            "Humid" => "湿",
            "Semi-Arid" => "半干旱",
            "Arid" => "干旱",
            "Hyperarid" => "干燥",
            "Forest World (+100%)" => "森林世界/+100%",
            "Extremely forested (+90%)" => "极多树木/+90%",
            "Very highly forested (+75%)" => "很多树木/+75%",
            "Highly forested (+50%)" => "较多树木/+50%",
            "Somewhat more forest (+25%)" => "略多树木/+25%",
            "Somewhat less forest (-25%)" => "略少树木/-25%",
            "Significantly less forested (-50%)" => "较少树木/-50%",
            "Much less forested (-75%)" => "很少树木/-75%",
            "Near Tree-less (-90%)" => "极少树木/-90%",
            "Tree-less World (-100%)" => "无树世界/-100%",
            _ => name
        };
    }

    private static bool TryResolveCommonConfigChoicePattern(string name, out string label)
    {
        label = string.Empty;
        var blocksMatch = Regex.Match(name, @"^(?<value>[0-9.]+)\s*(?<unit>k|mil)? blocks$", RegexOptions.IgnoreCase);
        if (blocksMatch.Success)
        {
            var value = blocksMatch.Groups["value"].Value;
            var unit = blocksMatch.Groups["unit"].Value.ToLowerInvariant();
            label = unit switch
            {
                "mil" => $"{value}百万个方块",
                "k" => $"{value}千个方块",
                _ => $"{value}个方块"
            };
            return true;
        }

        var hpMatch = Regex.Match(name, @"^(?<value>[0-9.]+) hp$", RegexOptions.IgnoreCase);
        if (hpMatch.Success)
        {
            label = $"{hpMatch.Groups["value"].Value}hp";
            return true;
        }

        var secondsMatch = Regex.Match(name, @"^(?<value>[0-9.]+) seconds?$", RegexOptions.IgnoreCase);
        if (secondsMatch.Success)
        {
            label = $"{secondsMatch.Groups["value"].Value} 秒";
            return true;
        }

        var minutesMatch = Regex.Match(name, @"^(?<value>[0-9.]+) minutes?$", RegexOptions.IgnoreCase);
        if (minutesMatch.Success)
        {
            label = $"{minutesMatch.Groups["value"].Value} 分钟";
            return true;
        }

        if (name.Equals("1 hour", StringComparison.OrdinalIgnoreCase))
        {
            label = "1 小时";
            return true;
        }

        var timesMatch = Regex.Match(name, @"^(?<value>[0-9]+) times?$", RegexOptions.IgnoreCase);
        if (timesMatch.Success)
        {
            label = $"{timesMatch.Groups["value"].Value}次";
            return true;
        }

        if (name.Equals("One time", StringComparison.OrdinalIgnoreCase))
        {
            label = "1次";
            return true;
        }

        if (name.Equals("Infinite", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("infinite", StringComparison.OrdinalIgnoreCase))
        {
            label = "无限";
            return true;
        }

        var speedMatch = Regex.Match(name, @"^(?<label>Very fast|Fast|Slightly faster|Normal|Slightly slower|Slower|Much slower|Deadly|Very Strong|Strong|Weak|Very weak|Much longer|Longer|Slightly longer|Slightly shorter|Shorter|Much Shorter)\s*\((?<value>[^)]+)\)$", RegexOptions.IgnoreCase);
        if (speedMatch.Success)
        {
            var zh = speedMatch.Groups["label"].Value switch
            {
                "Very fast" => "很快",
                "Fast" => "较快",
                "Slightly faster" => "稍快",
                "Normal" => "正常",
                "Slightly slower" => "稍慢",
                "Slower" => "较慢",
                "Much slower" => "很慢",
                "Deadly" => "致命",
                "Very Strong" => "很强",
                "Strong" => "强力",
                "Weak" => "弱小",
                "Very weak" => "很弱",
                "Much longer" => "很长",
                "Longer" => "较长",
                "Slightly longer" => "稍长",
                "Slightly shorter" => "稍短",
                "Shorter" => "较短",
                "Much Shorter" => "很短",
                _ => speedMatch.Groups["label"].Value
            };
            label = $"{zh}（{speedMatch.Groups["value"].Value}）";
            return true;
        }

        return false;
    }

    private void RefreshConfigWorldRuleLabels()
    {
        foreach (var item in _configWorldRuleItems)
        {
            item.SetLanguage(
                _isChinese,
                BuildConfigRuleChoiceOptions(item.Definition, item.Value),
                ResolveConfigRuleLabelZh(item.Definition));
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

    private static string GetConfigPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "serverconfig.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private string GetRobotSettingsPath()
    {
        return Path.Combine(GetWorkspaceRootForUi(), "qqbot", "vs2qq-settings.json");
    }

    private static string GetOpenInfoSettingsPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "ModConfig", "openserverquery.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private static string GetAuthSettingsPath(InstanceProfile profile)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "ModConfig", "serverauth.json");
        try
        {
            return Path.GetFullPath(configPath);
        }
        catch
        {
            return configPath;
        }
    }

    private string GetWorkspaceRootForUi()
    {
        var root = _preferencesService.Load().WorkspaceRoot;
        if (string.IsNullOrWhiteSpace(root))
        {
            root = Environment.GetEnvironmentVariable("LAUNCHERGO_WORKSPACE");
        }

        if (string.IsNullOrWhiteSpace(root))
        {
            root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "LauncherGo");
        }

        try
        {
            return Path.GetFullPath(root);
        }
        catch
        {
            return root;
        }
    }

    private void SetConfigPath(InstanceProfile? profile)
    {
        ConfigPathTextBlock.Text = profile is null
            ? T("配置路径：未选择档案", "Config path: no profile selected")
            : T($"配置路径：{GetConfigPath(profile)}", $"Config path: {GetConfigPath(profile)}");
    }

    private string FormatConfigLoadFailure(InstanceProfile profile, Exception exception)
    {
        var configPath = GetConfigPath(profile);
        var status = exception switch
        {
            FileNotFoundException => T("配置状态：缺失", "Config status: missing"),
            InvalidDataException => T("配置状态：解析失败", "Config status: parse failed"),
            JsonException => T("配置状态：解析失败", "Config status: parse failed"),
            IOException => T("配置状态：读取失败", "Config status: read failed"),
            _ => T("配置状态：加载失败", "Config status: load failed")
        };

        return status + Environment.NewLine +
               T($"配置路径：{configPath}", $"Config path: {configPath}") + Environment.NewLine +
               T($"原因：{exception.Message}", $"Reason: {exception.Message}");
    }

    private void SetConfigStatus(string message, bool notify = true)
    {
        ConfigStatusTextBlock.Text = message;
        if (notify)
        {
            ShowToast(message);
        }
    }

    private static string ReadConfigString(JsonNode? node, string defaultValue)
    {
        return ReadConfigFlexibleString(node) ?? defaultValue;
    }

    private static string? ReadConfigNullableString(JsonNode? node)
    {
        return node is null ? null : ReadConfigFlexibleString(node);
    }

    private static int ReadConfigInt(JsonNode? node, int defaultValue)
    {
        if (ReadConfigNullableInt(node) is { } value)
        {
            return value;
        }

        return defaultValue;
    }

    private static int? ReadConfigNullableInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var numeric))
        {
            return numeric;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static bool ReadConfigBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            bool.TryParse(node.GetValue<string>(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static string? ReadConfigFlexibleString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => node.ToString(),
            _ => node.ToJsonString()
        };
    }

    private static string? ReadConfigRuleFallbackValue(string key, JsonObject root, JsonObject worldConfig)
    {
        return key switch
        {
            "worldWidth" => ReadConfigFlexibleString(root["MapSizeX"]) ?? ReadConfigFlexibleString(worldConfig["MapSizeX"]),
            "worldLength" => ReadConfigFlexibleString(root["MapSizeZ"]) ?? ReadConfigFlexibleString(worldConfig["MapSizeZ"]),
            "colorAccurateWorldmap" => ReadConfigFlexibleString(worldConfig["colorAccurateWorldmap"]),
            _ => null
        };
    }

    private static string ResolveCurrentConfigSaveFilePath(InstanceProfile profile)
    {
        var activeSaveFile = NormalizeFullPath(profile.ActiveSaveFile);
        var saveRoot = NormalizeFullPath(profile.SaveDirectory);
        if (!string.IsNullOrWhiteSpace(activeSaveFile) &&
            IsSameOrChildPath(activeSaveFile, saveRoot))
        {
            return activeSaveFile;
        }

        if (!string.IsNullOrWhiteSpace(saveRoot))
        {
            return Path.Combine(saveRoot, "default.vcdbs");
        }

        return Path.Combine(profile.DirectoryPath, "Saves", "default.vcdbs");
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

    private void OnOpenProfileDirectoryClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string directoryPath } || string.IsNullOrWhiteSpace(directoryPath))
        {
            AppendConsoleLine(T("[system] 档案目录无效。", "[system] Invalid profile directory."));
            return;
        }

        if (!Directory.Exists(directoryPath))
        {
            AppendConsoleLine(T($"[system] 档案目录不存在：{directoryPath}", $"[system] Profile directory not found: {directoryPath}"));
            return;
        }

        OpenLocalFile(directoryPath);
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
            var ids = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId);
            ids.Add(profile.Id);
            preferences.DefaultLaunchProfileIds = ids.ToList();
            preferences.DefaultLaunchProfileId = string.Join(';', ids);
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
        var targetProfileId = SplitProfileIds(preferences.DefaultLaunchProfileIds, preferences.DefaultLaunchProfileId).FirstOrDefault();
        if (string.IsNullOrWhiteSpace(targetProfileId))
        {
            return false;
        }

        var targetProfile = _profileService.GetProfileById(targetProfileId);
        if (targetProfile is null)
        {
            return false;
        }

        var targetSavePath = NormalizeFullPath(targetProfile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(targetSavePath))
        {
            targetSavePath = NormalizeFullPath(preferences.DefaultLaunchSaveFile);
        }
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
                SetDownloadStatus(
                    T($"正在下载 {item.Entry.Version} {value:P0}", $"Downloading {item.Entry.Version} {value:P0}"),
                    notify: false);
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

    private static long? ResolveProcessMemory(int? processId)
    {
        if (!processId.HasValue || processId.Value <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId.Value);
            return process.WorkingSet64;
        }
        catch
        {
            return null;
        }
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

    [GeneratedRegex(@"\[(?:Talk|Chat)\]|<[^>]+>\s*.+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleChatLineRegex();

    [GeneratedRegex(@"\[(?:Server\s+)?Notification\]|服务器通知|message to all in group", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleNotificationLineRegex();

    [GeneratedRegex(@"joins\.|joined\.|left\.|leaves\.|加入了服务器|离开了服务器|进入服务器|离开服务器|加入游戏|离开游戏", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleJoinLeaveLineRegex();

    [GeneratedRegex(@"died|has died|death message|death reason|fell from a high place|fell to (?:his|her|their) death|plummeted|已死亡|死亡消息|死因|摔死|从高处坠落而亡|坠落身亡", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleDeathLineRegex();

    [GeneratedRegex(@"kick(?:ed|ing)?|ban(?:ned|ning)?|whitelist|auth(?:entication)?.*(?:failed|failure|required|denied)|login.*failed|rejected|denied|白名单|认证失败|登录失败|踢出|封禁", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleAdminLineRegex();

    [GeneratedRegex(@"start(?:ing|ed)?|stop(?:ping|ped)?|shut(?:ting)?\s*down|shutdown|crash(?:ed)?|sav(?:e|ed|ing)|backup|正在保存|保存完成|备份完成|备份失败", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleLifecycleLineRegex();

    [GeneratedRegex(@"temporal|rift|storm|boss|特殊事件|时空|裂隙|风暴|首领", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConsoleSpecialEventLineRegex();

    [GeneratedRegex(@"joins\.|left\.|leaves\.|died|死亡|摔死|killed|离开|进入|加入|玩家", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
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
        Restriction,
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

    private enum ToastKind
    {
        Neutral,
        Success,
        Error
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

    public sealed class RestrictionProfileConfigItem
    {
        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string ConfigPath { get; init; } = string.Empty;

        public string SummaryText { get; init; } = string.Empty;

        public static RestrictionProfileConfigItem FromSettings(
            InstanceProfile profile,
            ModRestrictionSettings settings,
            string path,
            bool isChinese)
        {
            var whitelist = settings.ForceWhitelistEnabled
                ? isChinese ? $"强制白名单 {settings.WhitelistModIds.Count}" : $"Whitelist {settings.WhitelistModIds.Count}"
                : isChinese ? "白名单未强制" : "Whitelist not forced";
            var blacklist = settings.BlacklistEnabled
                ? isChinese ? $"黑名单 {settings.BlacklistModIds.Count}" : $"Blacklist {settings.BlacklistModIds.Count}"
                : isChinese ? "黑名单关闭" : "Blacklist off";
            return new RestrictionProfileConfigItem
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ConfigPath = path,
                SummaryText = $"{whitelist} / {blacklist}"
            };
        }
    }

    public sealed class RestrictionModIdItem : INotifyPropertyChanged
    {
        private string _modId;

        public RestrictionModIdItem(string modId)
        {
            _modId = modId;
        }

        public string ModId
        {
            get => _modId;
            set
            {
                var normalized = value ?? string.Empty;
                if (_modId == normalized)
                {
                    return;
                }

                _modId = normalized;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ModId)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    public sealed class ProfileConfigListItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string ConfigPath { get; init; } = string.Empty;

        public string ModifiedText { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static ProfileConfigListItem FromPath(InstanceProfile profile, string path)
        {
            var modifiedText = "-";
            try
            {
                if (File.Exists(path))
                {
                    modifiedText = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                modifiedText = "-";
            }

            return new ProfileConfigListItem
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                ConfigPath = path,
                ModifiedText = modifiedText
            };
        }
    }

    public sealed class RobotProfileBindingItem : INotifyPropertyChanged
    {
        private string _groupId = string.Empty;
        private string _superUserId = string.Empty;
        private InstanceProfile? _selectedProfile;
        private ObservableCollection<InstanceProfile> _profileOptions;

        public RobotProfileBindingItem(
            ObservableCollection<InstanceProfile> profileOptions,
            string profileId,
            string groupId,
            string superUserId)
        {
            _profileOptions = profileOptions;
            ProfileId = profileId;
            _groupId = groupId;
            _superUserId = superUserId;
            _selectedProfile = profileOptions.FirstOrDefault(profile =>
                profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase));
        }

        public string ProfileId { get; private set; }

        public ObservableCollection<InstanceProfile> ProfileOptions
        {
            get => _profileOptions;
            set
            {
                if (ReferenceEquals(_profileOptions, value))
                {
                    return;
                }

                _profileOptions = value;
                OnPropertyChanged();
            }
        }

        public InstanceProfile? SelectedProfile
        {
            get => _selectedProfile;
            set
            {
                if (ReferenceEquals(_selectedProfile, value))
                {
                    return;
                }

                _selectedProfile = value;
                ProfileId = value?.Id ?? string.Empty;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ProfileId));
            }
        }

        public string GroupId
        {
            get => _groupId;
            set
            {
                if (_groupId == value)
                {
                    return;
                }

                _groupId = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public string SuperUserId
        {
            get => _superUserId;
            set
            {
                if (_superUserId == value)
                {
                    return;
                }

                _superUserId = value ?? string.Empty;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class OpenServerQueryProfileConfigItem : INotifyPropertyChanged
    {
        private bool _isSelected;
        private bool _enabled;

        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string Version { get; init; } = string.Empty;

        public string ConfigPath { get; init; } = string.Empty;

        public string ModifiedText { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
            }
        }

        public bool Enabled
        {
            get => _enabled;
            set
            {
                if (_enabled == value)
                {
                    return;
                }

                _enabled = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public static OpenServerQueryProfileConfigItem FromProfile(
            InstanceProfile profile,
            OpenServerQueryEndpointConfig endpoint,
            string path)
        {
            var modifiedText = "-";
            try
            {
                if (File.Exists(path))
                {
                    modifiedText = File.GetLastWriteTime(path).ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
                }
            }
            catch
            {
                modifiedText = "-";
            }

            return new OpenServerQueryProfileConfigItem
            {
                ProfileId = profile.Id,
                ProfileName = profile.Name,
                Version = profile.Version,
                ConfigPath = path,
                ModifiedText = modifiedText,
                Enabled = endpoint.Enabled
            };
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
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

        public DateTime? StartDateValue
        {
            get => TryParseDateValue(_startDate);
            set => SetDateValue(ref _startDate, value, nameof(StartDateValue), nameof(StartDate));
        }

        public DateTime? EndDateValue
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

        private static DateTime? TryParseDateValue(string? value)
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

            return new DateTime(date.Year, date.Month, date.Day);
        }

        private bool SetDateValue(ref string field, DateTime? value, string datePropertyName, string textPropertyName)
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

    public sealed class ScheduledCommandItem : INotifyPropertyChanged
    {
        private string _time = "12:00";
        private string _command = string.Empty;
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

        public string Command
        {
            get => _command;
            set => SetField(ref _command, value);
        }

        public ScheduledServerCommand ToModel()
        {
            return new ScheduledServerCommand
            {
                Enabled = _enabled,
                Time = _time?.Trim() ?? string.Empty,
                Command = _command?.Trim() ?? string.Empty
            };
        }

        public static ScheduledCommandItem FromModel(ScheduledServerCommand model)
        {
            return new ScheduledCommandItem
            {
                Enabled = model.Enabled,
                Time = model.Time,
                Command = model.Command
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

        public required string ConfigPath { get; init; }

        public bool IsDisabled { get; init; }

        public bool ModEnabled => !IsDisabled;

        public required string DependenciesText { get; init; }

        public required string IssuesText { get; init; }

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
                ConfigPath = model.ConfigPath,
                IsDisabled = model.IsDisabled,
                DependenciesText = model.DependenciesText,
                IssuesText = BuildModIssuesText(model)
            };
        }

        public static ModEntry ToModel(ModListItem item)
        {
            return new ModEntry
            {
                ModId = item.ModId,
                Version = item.Version,
                FilePath = item.FilePath,
                ConfigPath = item.ConfigPath,
                Status = item.IsDisabled ? "Disabled" : "OK",
                IsDisabled = item.IsDisabled,
                Dependencies = [],
                DependencyIssues = []
            };
        }

        private static string BuildModIssuesText(ModEntry model)
        {
            var issues = model.DependencyIssues.ToList();
            if (!model.Status.Equals("OK", StringComparison.OrdinalIgnoreCase) &&
                !model.Status.Equals("MissingDependency", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(model.Status.Equals("InvalidMetadata", StringComparison.OrdinalIgnoreCase)
                    ? "元数据无效"
                    : model.Status);
            }

            return issues.Count == 0 ? "-" : string.Join("; ", issues);
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

    public sealed class DashboardServerItem : INotifyPropertyChanged
    {
        private string _profileName = string.Empty;
        private string _version = string.Empty;
        private bool _isRunning;
        private string _statusText = string.Empty;
        private IBrush _statusBrush = Brushes.Gray;
        private string _summaryText = string.Empty;
        private string _actionText = string.Empty;
        private bool _isActionEnabled = true;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProfileId { get; init; } = string.Empty;

        public string ProfileName
        {
            get => _profileName;
            set => SetField(ref _profileName, value);
        }

        public string Version
        {
            get => _version;
            set => SetField(ref _version, value);
        }

        public bool IsRunning
        {
            get => _isRunning;
            set => SetField(ref _isRunning, value);
        }

        public string StatusText
        {
            get => _statusText;
            set => SetField(ref _statusText, value);
        }

        public IBrush StatusBrush
        {
            get => _statusBrush;
            set => SetField(ref _statusBrush, value);
        }

        public string SummaryText
        {
            get => _summaryText;
            set => SetField(ref _summaryText, value);
        }

        public string ActionText
        {
            get => _actionText;
            set => SetField(ref _actionText, value);
        }

        public bool IsActionEnabled
        {
            get => _isActionEnabled;
            set => SetField(ref _isActionEnabled, value);
        }

        private void SetField<T>(ref T field, T value, [CallerMemberName] string propertyName = "")
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return;
            }

            field = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }

    public sealed class DashboardPlayerItem
    {
        public string PlayerName { get; init; } = string.Empty;

        public string ProfileName { get; init; } = string.Empty;

        public string JoinedAtText { get; init; } = string.Empty;

        public static DashboardPlayerItem FromModel(ServerOnlinePlayerInfo player)
        {
            return new DashboardPlayerItem
            {
                PlayerName = player.PlayerName,
                ProfileName = player.ProfileName,
                JoinedAtText = player.JoinedAtUtc.HasValue
                    ? player.JoinedAtUtc.Value.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)
                    : "--"
            };
        }
    }

    public sealed class DashboardUptimeItem
    {
        public string Name { get; init; } = string.Empty;

        public string UptimeText { get; init; } = string.Empty;
    }

    public sealed class ConsoleServerItem
    {
        public string ProfileId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;
    }

    public sealed class LaunchTargetItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public event PropertyChangedEventHandler? PropertyChanged;

        public string ProfileId { get; init; } = string.Empty;

        public string DisplayName { get; init; } = string.Empty;

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
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
            IReadOnlyList<ConfigChoiceOption> choiceOptions,
            string? labelZhOverride = null)
        {
            Definition = definition;
            Key = definition.Key;
            Type = definition.Type;
            ChoiceOptions = choiceOptions;
            _value = value;
            SetLanguage(isChinese, choiceOptions, labelZhOverride);
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

        public void SetLanguage(bool isChinese, IReadOnlyList<ConfigChoiceOption> choiceOptions, string? labelZhOverride = null)
        {
            var selectedValue = SelectedChoiceOption?.Value ?? Value;
            ChoiceOptions = choiceOptions;
            Label = isChinese ? labelZhOverride ?? Definition.LabelZh : Definition.LabelEn;
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
