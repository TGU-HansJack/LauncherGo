using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using Avalonia.Threading;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow : Window
{
    private const int RealtimeRangeSeconds = 60;
    private const int NetworkRangeCount = 144;
    private const double ChartWidth = 640;
    private const double ChartHeight = 248;
    private const double ThumbnailWidth = 76;
    private const double ThumbnailHeight = 50;
    private const string LaunchStartIconData =
        "M187.2 100.9C174.8 94.1 159.8 94.4 147.6 101.6C135.4 108.8 128 121.9 128 136L128 504C128 518.1 135.5 531.2 147.6 538.4C159.7 545.6 174.8 545.9 187.2 539.1L523.2 355.1C536 348.1 544 334.6 544 320C544 305.4 536 291.9 523.2 284.9L187.2 100.9z";
    private const string LaunchStopIconData =
        "M160 96L480 96C515.3 96 544 124.7 544 160L544 480C544 515.3 515.3 544 480 544L160 544C124.7 544 96 515.3 96 480L96 160C96 124.7 124.7 96 160 96z";
    private static readonly string[] QuickCommands = ["/stop", "/autosavenow", "/list"];

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

    private static readonly string[] ConfigImagePatterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.gif", "*.bmp"];

    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IServerPackageService _serverPackageService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceSaveService _saveService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IServerImageService _serverImageService;
    private readonly IServerProcessService _serverProcessService;
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
    private readonly ObservableCollection<ConfigServerImageItem> _configShowcaseImageItems = [];
    private readonly List<ServerDownloadEntry> _catalogEntries = [];

    private MainTab _selectedTab = MainTab.Home;
    private HomeMetric _selectedMetric = HomeMetric.Server;
    private InstanceManageTab _selectedInstanceManageTab = InstanceManageTab.Profiles;
    private SettingsTab _selectedSettingsTab = SettingsTab.Server;
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
    private string _configSaveFileLocation = string.Empty;
    private string _pendingCoverImportPath = string.Empty;
    private string _pendingShowcaseImportPath = string.Empty;
    private ConfigServerImageItem? _configCoverImage;

    public LauncherMainWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>(),
            ServiceLocator.GetRequiredService<IInstanceProfileService>(),
            ServiceLocator.GetRequiredService<IInstanceSaveService>(),
            ServiceLocator.GetRequiredService<IInstanceServerConfigService>(),
            ServiceLocator.GetRequiredService<IServerImageService>(),
            ServiceLocator.GetRequiredService<IServerProcessService>())
    {
    }

    public LauncherMainWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService,
        IInstanceProfileService profileService,
        IInstanceSaveService saveService,
        IInstanceServerConfigService instanceServerConfigService,
        IServerImageService serverImageService,
        IServerProcessService serverProcessService)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;
        _profileService = profileService;
        _saveService = saveService;
        _instanceServerConfigService = instanceServerConfigService;
        _serverImageService = serverImageService;
        _serverProcessService = serverProcessService;

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

        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
        InitializeSeries();
        InitializeCollections();
        RefreshProfiles();
        _ = RefreshSavesAsync();
        _ = RefreshDownloadVersionsAsync(forceReload: false);

        SelectTab(MainTab.Home);
        SelectMetric(HomeMetric.Server);
        SelectInstanceManageTab(InstanceManageTab.Profiles);
        SelectSettingsTab(SettingsTab.Server);

        _dataTimer.Start();
        _tickerTimer.Start();
        _homeSloganTimer.Start();

        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _tickerTimer.Stop();
            _homeSloganTimer.Stop();
            _serverProcessService.OutputReceived -= OnServerOutputReceived;
            _serverProcessService.StatusChanged -= OnServerStatusChanged;
        };
    }

    private void InitializeStaticTexts()
    {
        HomeNavButton.Content = T("主页", "Home");
        MonitorNavButton.Content = T("监控", "Monitor");
        ConsoleNavButton.Content = T("控制台", "Console");
        InstanceManageNavButton.Content = T("实例", "Instance");
        RobotNavButton.Content = T("机器人", "Robot");
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
        DownloadVersionsTabButton.Content = T("下载版本", "Downloads");
        ProfileNameTextBox.PlaceholderText = T("档案名称", "Profile name");
        CreateProfileButton.Content = T("创建", "Create");
        ImportProfileButton.Content = T("导入", "Import");
        DeleteProfileButton.Content = T("删除", "Delete");
        RefreshProfilesButton.Content = T("刷新", "Refresh");
        ImportSaveButton.Content = T("导入", "Import");
        DeleteSaveButton.Content = T("删除", "Delete");
        RefreshSavesButton.Content = T("刷新", "Refresh");
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

        Title = T("LauncherGo 主窗口", "LauncherGo Main Window");
        ToolTip.SetTip(RepositoryButton, T("仓库", "Repository"));
        ToolTip.SetTip(FeedbackButton, T("反馈", "Feedback"));
        ToolTip.SetTip(SponsorButton, T("赞助", "Sponsor"));
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
        ConfigAdvertiseServerCheckBox.Content = T("公开到服务器列表", "List on Public Server Browser");
        ConfigUpnpCheckBox.Content = T("启用 UPnP 自动端口映射", "Enable UPnP Port Mapping");
        ConfigSecurityTitleTextBlock.Text = T("安全与维护", "Security & Maintenance");
        ConfigPasswordLabelTextBlock.Text = T("加入密码", "Join Password");
        ConfigPasswordHintTextBlock.Text = T("留空表示不设置密码。", "Leave empty to disable password.");
        ConfigWhitelistModeLabelTextBlock.Text = T("白名单模式", "Whitelist Mode");
        ConfigWarnAfkSecondsLabelTextBlock.Text = T("AFK 警告秒数", "AFK Warning Seconds");
        ConfigKickAfkSecondsLabelTextBlock.Text = T("AFK 踢出秒数", "AFK Kick Seconds");
        ConfigClientConnectionTimeoutLabelTextBlock.Text = T("连接超时秒数", "Connection Timeout Seconds");
        ConfigMaxChunkRadiusLabelTextBlock.Text = T("最大区块视距半径", "Max Chunk View Radius");
        ConfigDieBelowDiskSpaceMbLabelTextBlock.Text = T("低于磁盘空间时关闭（MB）", "Shutdown Below Disk Space (MB)");
        ConfigVerifyPlayerAuthCheckBox.Content = T("启用官方账号验证", "Enable Official Auth");
        ConfigAllowPvPCheckBox.Content = T("允许PvP", "Allow PvP");
        ConfigAllowFireSpreadCheckBox.Content = T("允许火势蔓延", "Allow Fire Spread");
        ConfigAllowFallingBlocksCheckBox.Content = T("允许方块掉落", "Allow Falling Blocks");
        ConfigPassTimeWhenEmptyCheckBox.Content = T("无人在线时继续流逝时间", "Pass Time When Empty");
        ConfigCorruptionProtectionCheckBox.Content = T("启用存档损坏保护", "Enable Corruption Protection");
        ConfigRegenerateCorruptChunksCheckBox.Content = T("重新生成损坏区块", "Regenerate Corrupt Chunks");
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
        ConfigServerImagesTitleTextBlock.Text = T("服务器图片", "Server Images");
        ConfigServerImagesRootLabelTextBlock.Text = T("图片目录", "Image Folder");
        ConfigCoverTitleTextBlock.Text = T("封面图（cover）", "Cover Image");
        ConfigShowcaseTitleTextBlock.Text = T("展示图（showcase）", "Showcase Images");
        ConfigShowcaseHintTextBlock.Text = T("选择列表项后可预览。", "Click an item to preview it.");
        ConfigCoverBrowseButton.Content = T("浏览", "Browse");
        ConfigCoverImportButton.Content = T("导入", "Import");
        ConfigCoverPreviewButton.Content = T("预览", "Preview");
        ConfigCoverDeleteButton.Content = T("删除", "Delete");
        ConfigShowcaseBrowseButton.Content = T("浏览", "Browse");
        ConfigShowcaseAddButton.Content = T("添加", "Add");
        ConfigShowcaseImportFolderButton.Content = T("导入", "Import");
        ConfigShowcasePreviewButton.Content = T("预览", "Preview");
        ConfigShowcaseDeleteButton.Content = T("删除", "Delete");
        ConfigWorldRulesTitleTextBlock.Text = T("世界规则", "World Rules");
        ConfigAdvancedJsonTitleTextBlock.Text = T("高级 JSON", "Advanced JSON");
        ConfigAdvancedJsonButton.Content = T("编辑高级 JSON", "Edit Advanced JSON");
        ConfigNoProfileTextBlock.Text = T("暂无档案，请先创建档案。", "No profile found. Create a profile first.");
        RebuildConfigChoiceOptions();
        RefreshConfigWorldRuleLabels();
        RefreshConfigImageTexts();
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
        QuickCommandComboBox.ItemsSource = QuickCommands;
        QuickCommandComboBox.SelectedIndex = -1;
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
        ConfigShowcaseImagesListBox.ItemsSource = _configShowcaseImageItems;
        RebuildConfigChoiceOptions();
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
        PushNextSample(_serverCpuSamples, status.IsRunning ? status.CpuPercent : 0);
        PushNextSample(_serverMemoryMbSamples, status.IsRunning ? BytesToMb(status.MemoryBytes) : 0);
        PushNextSample(_playersSamples, status.IsRunning ? status.OnlinePlayers : 0);

        // 机器人和连接服务尚未接入，保持 0，避免展示假数据。
        PushNextSample(_robotCpuSamples, 0);
        PushNextSample(_robotMemoryMbSamples, 0);
        if (DateTime.UtcNow.Second % 5 == 0)
        {
            PushNextSample(_networkLatencySamples, 0, NetworkRangeCount);
        }

        UpdateCardValues(status);

        if (_selectedTab == MainTab.Monitor)
        {
            RenderSelectedMetricChart();
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

        RobotStatusCardValueText.Text = T("未接入  CPU 0%  内存 0 MB", "Not connected  CPU 0%  Mem 0 MB");

        var currentPlayers = (int)Math.Round(_playersSamples[^1]);
        var peakPlayers = Math.Max(status.PeakOnlinePlayers, (int)Math.Round(_playersSamples.Max()));
        OnlinePlayersCardValueText.Text = T(
            $"在线 {currentPlayers}  最高 {peakPlayers}",
            $"Online {currentPlayers}  Peak {peakPlayers}");

        NetworkStatusCardValueText.Text = T("未配置连接监控", "Connection monitor not configured");
        LaunchActionTextBlock.Text = status.IsRunning ? T("停止服务器", "Stop Server") : T("启动服务器", "Start Server");
        LaunchActionIconPath.Data = Geometry.Parse(status.IsRunning ? LaunchStopIconData : LaunchStartIconData);
        LaunchServerButton.Classes.Set("running", status.IsRunning);
        RefreshLaunchButtonSummary(status.IsRunning);

        RenderThumbnailCharts();
    }

    private void RenderSelectedMetricChart()
    {
        var status = _serverProcessService.GetCurrentStatus();
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
        RenderDualLineChart(
            title: T("机器人状态", "Robot Status"),
            topValue: T("未接入", "Not connected"),
            summary: T("机器人进程尚未接入，当前不展示模拟数据。", "Robot process is not connected; no simulated data is shown."),
            primary: _robotCpuSamples,
            secondary: _robotMemoryMbSamples,
            yMin: 0,
            yMax: 100,
            yAxisFormatter: value => $"{value:F0}",
            xHint: T("60 秒", "60 seconds"),
            details:
            [
                (T("CPU", "CPU"), "0%"),
                (T("内存", "Memory"), "0 MB"),
                (T("状态", "Status"), T("未接入", "Not connected")),
                (T("运行时间", "Uptime"), "--")
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
    }

    private void RefreshLaunchOptions(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        RefreshLaunchButtonSummary();
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
            LaunchSelectionPillHost.Classes.Set("expanded", true);
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
        _selectedTab = tab;

        HomePanel.IsVisible = tab == MainTab.Home;
        MonitorPanel.IsVisible = tab == MainTab.Monitor;
        ConsolePanel.IsVisible = tab == MainTab.Console;
        InstanceManagePanel.IsVisible = tab == MainTab.InstanceManage;
        SettingsPanel.IsVisible = tab == MainTab.Settings;
        RobotPanel.IsVisible = tab == MainTab.Robot;
        ConnectionPanel.IsVisible = tab == MainTab.Connection;

        SetSelectedClass(HomeNavButton, tab == MainTab.Home);
        SetSelectedClass(MonitorNavButton, tab == MainTab.Monitor);
        SetSelectedClass(ConsoleNavButton, tab == MainTab.Console);
        SetSelectedClass(InstanceManageNavButton, tab == MainTab.InstanceManage);
        SetSelectedClass(SettingsNavButton, tab == MainTab.Settings);
        SetSelectedClass(RobotNavButton, tab == MainTab.Robot);
        SetSelectedClass(ConnectionNavButton, tab == MainTab.Connection);

        if (tab == MainTab.Monitor)
        {
            RenderSelectedMetricChart();
        }
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
        DownloadVersionsPanel.IsVisible = tab == InstanceManageTab.DownloadVersions;
        SetSelectedClass(ProfilesTabButton, tab == InstanceManageTab.Profiles);
        SetSelectedClass(ConfigTabButton, tab == InstanceManageTab.Config);
        SetSelectedClass(SavesTabButton, tab == InstanceManageTab.Saves);
        SetSelectedClass(DownloadVersionsTabButton, tab == InstanceManageTab.DownloadVersions);

        if (tab == InstanceManageTab.Config)
        {
            _ = RefreshConfigProfilesAsync();
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
        var isAppearance = tab == SettingsTab.Appearance;
        SettingsAppearancePanel.IsVisible = isAppearance;
        SettingsBlankPanel.IsVisible = !isAppearance;

        if (isAppearance)
        {
            RefreshAppearanceSettingsEditor();
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
        _isChinese = languageCode.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        InitializeStaticTexts();
        RefreshAppearanceSettingsEditor();
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
            AppendConsoleLine(line);
            TrackPlayerEventText(line);
        });
    }

    private void OnServerStatusChanged(object? sender, ServerRuntimeStatus status)
    {
        Dispatcher.UIThread.Post(() =>
        {
            UpdateCardValues(status);
            if (_selectedTab == MainTab.Monitor)
            {
                RenderSelectedMetricChart();
            }
        });
    }

    private void AppendConsoleLine(string line)
    {
        _consoleLines.Add(line);
        while (_consoleLines.Count > 500)
        {
            _consoleLines.RemoveAt(0);
        }

        var text = string.Join(Environment.NewLine, _consoleLines);
        ConsoleOutputTextBlock.Text = text;
        ConsoleOutputScrollViewer.ScrollToEnd();
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
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || ShouldSkipWindowDrag(e.Source))
        {
            return;
        }

        BeginMoveDrag(e);
    }

    private static bool ShouldSkipWindowDrag(object? source)
    {
        var current = source as StyledElement;
        while (current is not null)
        {
            if (current is Button
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

    private void OnRobotNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Robot);

    private void OnConnectionNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Connection);

    private void OnServerStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Server);

    private void OnRobotStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Robot);

    private void OnOnlinePlayersCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Players);

    private void OnNetworkStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Network);

    private void OnProfilesSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Profiles);

    private void OnConfigSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Config);

    private void OnSavesSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.Saves);

    private void OnDownloadVersionsSubTabClick(object? sender, RoutedEventArgs e) => SelectInstanceManageTab(InstanceManageTab.DownloadVersions);

    private void OnServerSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Server);

    private void OnAppearanceSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Appearance);

    private void OnNetworkSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Network);

    private void OnAdvancedSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Advanced);

    private void OnAboutSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.About);

    private void OnSponsorsSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Sponsors);

    private void OnContributorsSettingsTabClick(object? sender, RoutedEventArgs e) => SelectSettingsTab(SettingsTab.Contributors);

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
        if (_serverProcessService.GetCurrentStatus().IsRunning || TryGetLockedLaunchTarget(out _, out _))
        {
            LaunchSelectionPillHost.Classes.Set("expanded", false);
            return;
        }

        LaunchSelectionPillHost.Classes.Set("expanded", true);
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
            AppendConsoleLine(T(
                "[system] 未锁定默认存档。请在“存档管理”中点击右侧“锁定默认”。",
                "[system] No default save is locked. Go to Saves and click 'Set default'."));
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
            AppendConsoleLine(T(
                "[system] 默认锁定存档不存在，请重新锁定默认存档。",
                "[system] Locked default save does not exist. Lock a default save again."));
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
            await LoadConfigImagesAsync(profile);
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

    private async Task LoadConfigImagesAsync(InstanceProfile profile)
    {
        ConfigImageRootPathTextBlock.Text = _serverImageService.GetImageRootPath(profile);
        var images = await _serverImageService.LoadServerImagesAsync(profile);
        _configCoverImage = images
            .Where(image => image.Kind == ServerImageKind.Cover)
            .Select(ConfigServerImageItem.FromImage)
            .FirstOrDefault();

        var selectedShowcasePath = (ConfigShowcaseImagesListBox.SelectedItem as ConfigServerImageItem)?.FullPath;
        _configShowcaseImageItems.Clear();
        foreach (var image in images.Where(image => image.Kind == ServerImageKind.Showcase))
        {
            _configShowcaseImageItems.Add(ConfigServerImageItem.FromImage(image));
        }

        ConfigShowcaseImagesListBox.SelectedItem = _configShowcaseImageItems.FirstOrDefault(item =>
            !string.IsNullOrWhiteSpace(selectedShowcasePath) &&
            item.FullPath.Equals(selectedShowcasePath, StringComparison.OrdinalIgnoreCase));
        RefreshConfigImageTexts();
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
        _configCoverImage = null;
        _configShowcaseImageItems.Clear();
        _pendingCoverImportPath = string.Empty;
        _pendingShowcaseImportPath = string.Empty;
        ConfigImageRootPathTextBlock.Text = string.Empty;
        RefreshConfigImageTexts();
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

    private async void OnConfigAdvancedJsonClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        try
        {
            var rawJson = await _instanceServerConfigService.LoadRawJsonAsync(profile);
            var editedJson = await ShowAdvancedJsonEditorAsync(T("高级 JSON", "Advanced JSON"), rawJson);
            if (editedJson is null)
            {
                SetConfigStatus(T("已取消高级 JSON 编辑。", "Advanced JSON edit canceled."));
                return;
            }

            await _instanceServerConfigService.SaveRawJsonAsync(profile, editedJson);
            await LoadConfigForProfileAsync(profile);
            SetConfigStatus(T("高级 JSON 已保存。", "Advanced JSON saved."));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"保存高级 JSON 失败：{ex.Message}", $"Failed to save advanced JSON: {ex.Message}"));
        }
    }

    private async void OnConfigCoverBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickConfigImageFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _pendingCoverImportPath = path;
        RefreshConfigImageTexts();
        SetConfigStatus(T($"已选择图片：{Path.GetFileName(path)}", $"Selected image: {Path.GetFileName(path)}"));
    }

    private async void OnConfigCoverImportClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingCoverImportPath))
        {
            _pendingCoverImportPath = await PickConfigImageFileAsync() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_pendingCoverImportPath))
        {
            return;
        }

        try
        {
            var imported = await _serverImageService.ImportImageAsync(profile, _pendingCoverImportPath, ServerImageKind.Cover);
            _pendingCoverImportPath = string.Empty;
            await LoadConfigImagesAsync(profile);
            SetConfigStatus(T($"封面图已导入：{imported.FileName}", $"Cover image imported: {imported.FileName}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"导入封面图失败：{ex.Message}", $"Failed to import cover image: {ex.Message}"));
        }
    }

    private void OnConfigCoverPreviewClick(object? sender, RoutedEventArgs e)
    {
        if (_configCoverImage is null)
        {
            SetConfigStatus(T("暂无封面图。", "No cover image yet."));
            return;
        }

        OpenLocalFile(_configCoverImage.FullPath);
    }

    private async void OnConfigCoverDeleteClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null || _configCoverImage is null)
        {
            SetConfigStatus(T("暂无封面图。", "No cover image yet."));
            return;
        }

        try
        {
            await _serverImageService.DeleteImageAsync(profile, _configCoverImage.ToDomain(ServerImageKind.Cover));
            await LoadConfigImagesAsync(profile);
            SetConfigStatus(T("封面图已删除。", "Cover image deleted."));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"删除封面图失败：{ex.Message}", $"Failed to delete cover image: {ex.Message}"));
        }
    }

    private async void OnConfigShowcaseBrowseClick(object? sender, RoutedEventArgs e)
    {
        var path = await PickConfigImageFileAsync();
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _pendingShowcaseImportPath = path;
        RefreshConfigImageTexts();
        SetConfigStatus(T($"已选择图片：{Path.GetFileName(path)}", $"Selected image: {Path.GetFileName(path)}"));
    }

    private async void OnConfigShowcaseAddClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        if (string.IsNullOrWhiteSpace(_pendingShowcaseImportPath))
        {
            _pendingShowcaseImportPath = await PickConfigImageFileAsync() ?? string.Empty;
        }

        if (string.IsNullOrWhiteSpace(_pendingShowcaseImportPath))
        {
            return;
        }

        try
        {
            var imported = await _serverImageService.ImportImageAsync(profile, _pendingShowcaseImportPath, ServerImageKind.Showcase);
            _pendingShowcaseImportPath = string.Empty;
            await LoadConfigImagesAsync(profile);
            SetConfigStatus(T($"已添加展示图：{imported.FileName}", $"Showcase image added: {imported.FileName}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"导入展示图失败：{ex.Message}", $"Failed to import showcase image: {ex.Message}"));
        }
    }

    private async void OnConfigShowcaseImportFolderClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null)
        {
            SetConfigStatus(T("请先选择档案。", "Select a profile first."));
            return;
        }

        var folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = T("选择图片文件夹", "Select image folder"),
            AllowMultiple = false
        });

        var path = TryGetLocalPath(folders.FirstOrDefault());
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            var count = await _serverImageService.ImportImagesFromFolderAsync(profile, path);
            await LoadConfigImagesAsync(profile);
            SetConfigStatus(count == 0
                ? T("所选目录中没有可导入的图片文件。", "No image files found in the selected folder.")
                : T($"已导入 {count} 张展示图。", $"Imported {count} showcase image(s)."));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"导入展示图失败：{ex.Message}", $"Failed to import showcase image: {ex.Message}"));
        }
    }

    private void OnConfigShowcasePreviewClick(object? sender, RoutedEventArgs e)
    {
        if (ConfigShowcaseImagesListBox.SelectedItem is not ConfigServerImageItem image)
        {
            SetConfigStatus(T("请先选择一张展示图。", "Please select a showcase image first."));
            return;
        }

        OpenLocalFile(image.FullPath);
    }

    private async void OnConfigShowcaseDeleteClick(object? sender, RoutedEventArgs e)
    {
        var profile = GetSelectedConfigProfile();
        if (profile is null || ConfigShowcaseImagesListBox.SelectedItem is not ConfigServerImageItem image)
        {
            SetConfigStatus(T("请先选择一张展示图。", "Please select a showcase image first."));
            return;
        }

        try
        {
            await _serverImageService.DeleteImageAsync(profile, image.ToDomain(ServerImageKind.Showcase));
            await LoadConfigImagesAsync(profile);
            SetConfigStatus(T($"展示图已删除：{image.FileName}", $"Showcase image deleted: {image.FileName}"));
        }
        catch (Exception ex)
        {
            SetConfigStatus(T($"删除展示图失败：{ex.Message}", $"Failed to delete showcase image: {ex.Message}"));
        }
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

    private void RefreshConfigImageTexts()
    {
        ConfigCoverImageTextBlock.Text = _configCoverImage is null
            ? T("暂无封面图。", "No cover image yet.")
            : $"{_configCoverImage.RelativePath} ({_configCoverImage.SizeLabel})";
        ConfigPendingCoverImportTextBlock.Text = string.IsNullOrWhiteSpace(_pendingCoverImportPath)
            ? string.Empty
            : _pendingCoverImportPath;
        ConfigPendingShowcaseImportTextBlock.Text = string.IsNullOrWhiteSpace(_pendingShowcaseImportPath)
            ? string.Empty
            : _pendingShowcaseImportPath;
        ConfigNoShowcaseTextBlock.IsVisible = _configShowcaseImageItems.Count == 0;
        ConfigShowcaseHintTextBlock.IsVisible = _configShowcaseImageItems.Count > 0;
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

    private async Task<string?> PickConfigImageFileAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = T("选择图片", "Select Image"),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(T("图片文件", "Image Files"))
                {
                    Patterns = ConfigImagePatterns
                }
            ]
        });

        return TryGetLocalPath(files.FirstOrDefault());
    }

    private async Task<string?> ShowAdvancedJsonEditorAsync(string title, string rawJson)
    {
        var editor = new TextBox
        {
            Text = rawJson,
            AcceptsReturn = true,
            TextWrapping = Avalonia.Media.TextWrapping.NoWrap,
            FontFamily = new Avalonia.Media.FontFamily("Consolas"),
            FontSize = 12,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Stretch
        };

        var saveButton = new Button { Content = T("保存", "Save"), Classes = { "ActionButton" } };
        var cancelButton = new Button { Content = T("取消", "Cancel"), Classes = { "SecondaryActionButton" } };
        string? result = null;
        var buttonPanel = new StackPanel
        {
            Orientation = Avalonia.Layout.Orientation.Horizontal,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Right,
            Spacing = 8,
            Children = { cancelButton, saveButton }
        };
        Grid.SetRow(buttonPanel, 1);

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("*,Auto"),
            Margin = new Thickness(12),
            RowSpacing = 10,
            Children = { editor, buttonPanel }
        };

        var dialog = new Window
        {
            Title = title,
            Width = 760,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = content
        };

        saveButton.Click += (_, _) =>
        {
            result = editor.Text ?? string.Empty;
            dialog.Close();
        };
        cancelButton.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(this);
        return result;
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

    private static int GetNumericValue(NumericUpDown control, int fallback)
    {
        return control.Value.HasValue
            ? decimal.ToInt32(control.Value.Value)
            : fallback;
    }

    private static int TryParseInt(string? value, int fallback)
    {
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
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

    private static string FormatConfigFileSize(long bytes)
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
            var count = await _saveService.DeleteSavesAsync(selectedPaths);
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
        Robot,
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
            return new SaveListItem
            {
                ProfileId = save.ProfileId,
                FullPath = save.FullPath,
                FileName = save.FileName,
                ProfileName = save.ProfileName,
                Description = $"{FormatFileSize(save.SizeBytes)}  {save.LastWriteTimeUtc.LocalDateTime:yyyy-MM-dd HH:mm}  {save.FullPath}",
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

    public sealed class ConfigChoiceOption(string value, string label)
    {
        public string Value { get; } = value;

        public string Label { get; } = label;

        public override string ToString() => Label;
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

    public sealed class ConfigServerImageItem
    {
        public required ServerImageKind Kind { get; init; }

        public required string FullPath { get; init; }

        public required string RelativePath { get; init; }

        public required string FileName { get; init; }

        public long SizeBytes { get; init; }

        public string SizeLabel => FormatConfigFileSize(SizeBytes);

        public static ConfigServerImageItem FromImage(ServerImageFileInfo image)
        {
            return new ConfigServerImageItem
            {
                Kind = image.Kind,
                FullPath = image.FullPath,
                RelativePath = image.RelativePath,
                FileName = image.FileName,
                SizeBytes = image.SizeBytes
            };
        }

        public ServerImageFileInfo ToDomain(ServerImageKind kind)
        {
            return new ServerImageFileInfo
            {
                Kind = kind,
                FullPath = FullPath,
                RelativePath = RelativePath,
                FileName = FileName,
                SizeBytes = SizeBytes,
                LastWriteUtc = DateTimeOffset.UtcNow
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
