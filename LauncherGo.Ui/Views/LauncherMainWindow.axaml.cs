using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
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

    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IServerPackageService _serverPackageService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceSaveService _saveService;
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

    public LauncherMainWindow()
        : this(
            ServiceLocator.GetRequiredService<ILauncherPreferencesService>(),
            ServiceLocator.GetRequiredService<IServerPackageService>(),
            ServiceLocator.GetRequiredService<IInstanceProfileService>(),
            ServiceLocator.GetRequiredService<IInstanceSaveService>(),
            ServiceLocator.GetRequiredService<IServerProcessService>())
    {
    }

    public LauncherMainWindow(
        ILauncherPreferencesService preferencesService,
        IServerPackageService serverPackageService,
        IInstanceProfileService profileService,
        IInstanceSaveService saveService,
        IServerProcessService serverProcessService)
    {
        _preferencesService = preferencesService;
        _serverPackageService = serverPackageService;
        _profileService = profileService;
        _saveService = saveService;
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
        LaunchPickerTitleTextBlock.Text = T("选择启动目标", "Select launch target");
        LaunchProfileTitleTextBlock.Text = T("档案", "Profile");
        LaunchSaveTitleTextBlock.Text = T("存档", "Save");
        LaunchPickerHintTextBlock.Text = T("未选择存档时使用档案默认存档。", "If no save is selected, the profile default save is used.");
        LaunchCancelButton.Content = T("取消", "Cancel");
        LaunchConfirmButton.Content = T("启动", "Start");
        CommandTextBox.PlaceholderText = T("输入服务器命令，回车发送", "Enter server command, press Enter to send");
        QuickCommandComboBox.PlaceholderText = T("快捷命令", "Quick command");
        SendCommandButton.Content = T("发送", "Send");

        ServerStatusCardTitleText.Text = T("服务器状态", "Server Status");
        RobotStatusCardTitleText.Text = T("机器人状态", "Robot Status");
        OnlinePlayersCardTitleText.Text = T("在线玩家", "Online Players");
        NetworkStatusCardTitleText.Text = T("网络状态", "Network Status");

        ProfilesTabButton.Content = T("档案列表", "Profiles");
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
        DownloadVersionsTitleText.Text = T("下载版本", "Download Versions");
        DownloadVersionsHintText.Text = T("下载或导入 Vintage Story Windows 服务端压缩包。", "Download or import Vintage Story Windows server packages.");
        ImportServerPackageButton.Content = T("导入", "Import");
        RefreshDownloadVersionsButton.Content = T("刷新", "Refresh");

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
    }

    private void RefreshLaunchOptions(IReadOnlyList<InstanceProfile>? profiles = null)
    {
        profiles ??= _profileService.GetProfiles();
        var selectedProfileId = (LaunchProfileListBox.SelectedItem as InstanceProfile)?.Id;
        LaunchProfileListBox.ItemsSource = profiles;
        LaunchProfileListBox.SelectedItem = profiles.FirstOrDefault(profile => profile.Id == selectedProfileId) ?? profiles.FirstOrDefault();
        RefreshLaunchSaveOptions();
        RefreshLaunchButtonSummary();
    }

    private async void OnLaunchProfileSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        await RefreshLaunchSaveOptionsAsync();
    }

    private void OnLaunchSaveSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        RefreshLaunchButtonSummary();
    }

    private void RefreshLaunchSaveOptions()
    {
        _ = RefreshLaunchSaveOptionsAsync();
    }

    private async Task RefreshLaunchSaveOptionsAsync()
    {
        if (LaunchProfileListBox.SelectedItem is not InstanceProfile profile)
        {
            LaunchSaveListBox.ItemsSource = Array.Empty<SaveFileEntry>();
            RefreshLaunchButtonSummary();
            return;
        }

        var selectedSavePath = (LaunchSaveListBox.SelectedItem as SaveFileEntry)?.FullPath;
        var saves = await _saveService.GetSavesAsync(profile);
        LaunchSaveListBox.ItemsSource = saves;
        LaunchSaveListBox.SelectedItem =
            saves.FirstOrDefault(save => save.FullPath.Equals(selectedSavePath, StringComparison.OrdinalIgnoreCase))
            ?? saves.FirstOrDefault(save => save.FullPath.Equals(profile.ActiveSaveFile, StringComparison.OrdinalIgnoreCase))
            ?? saves.FirstOrDefault();
        RefreshLaunchButtonSummary();
    }

    private void RefreshLaunchButtonSummary(bool? isRunning = null)
    {
        if (isRunning ?? _serverProcessService.GetCurrentStatus().IsRunning)
        {
            LaunchSelectionSummaryTextBlock.Text = T("运行中 | 点击停止", "Running | Click to stop");
            LaunchConfirmButton.IsEnabled = false;
            return;
        }

        var profile = LaunchProfileListBox.SelectedItem as InstanceProfile;
        var save = LaunchSaveListBox.SelectedItem as SaveFileEntry;
        var profileName = string.IsNullOrWhiteSpace(profile?.Name) ? T("未选择档案", "No profile") : profile.Name;
        var saveName = string.IsNullOrWhiteSpace(save?.FileName) ? T("未固定存档", "No fixed save") : save.FileName;
        LaunchSelectionSummaryTextBlock.Text = $"{profileName} | {saveName}";
        LaunchConfirmButton.IsEnabled = profile is not null;
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
            _saveItems.Add(SaveListItem.FromSave(save));
        }

        await RefreshLaunchSaveOptionsAsync();
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

        foreach (var entry in _catalogEntries)
        {
            var isDownloaded = installedVersions.Contains(entry.Version) ||
                               File.Exists(Path.Combine(preferences.ServerDirectory, entry.FileName));
            _downloadVersionItems.Add(new DownloadVersionListItem(
                entry,
                entry.Version,
                isDownloaded ? T("已下载", "Downloaded") : T("下载", "Download"),
                !isDownloaded));
        }
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
        SavesPanel.IsVisible = tab == InstanceManageTab.Saves;
        DownloadVersionsPanel.IsVisible = tab == InstanceManageTab.DownloadVersions;
        SetSelectedClass(ProfilesTabButton, tab == InstanceManageTab.Profiles);
        SetSelectedClass(SavesTabButton, tab == InstanceManageTab.Saves);
        SetSelectedClass(DownloadVersionsTabButton, tab == InstanceManageTab.DownloadVersions);
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
            RefreshLaunchOptions();
            LaunchSelectionPopup.IsOpen = true;
            return;
        }

        await StopServerFromLaunchButtonAsync();
    }

    private void OnLaunchServerPointerEntered(object? sender, PointerEventArgs e)
    {
        LaunchSelectionPillHost.Classes.Set("expanded", true);
    }

    private void OnLaunchServerPointerExited(object? sender, PointerEventArgs e)
    {
        LaunchSelectionPillHost.Classes.Set("expanded", false);
    }

    private void OnLaunchCancelClick(object? sender, RoutedEventArgs e)
    {
        LaunchSelectionPopup.IsOpen = false;
    }

    private async void OnLaunchConfirmClick(object? sender, RoutedEventArgs e)
    {
        await StartSelectedServerAsync();
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

    private async Task StartSelectedServerAsync()
    {
        if (_isStoppingOrStarting)
        {
            return;
        }

        var profile = LaunchProfileListBox.SelectedItem as InstanceProfile
                      ?? _profileService.GetProfiles().FirstOrDefault();
        if (profile is null)
        {
            LaunchSelectionPopup.IsOpen = false;
            AppendConsoleLine("[system] 请先在实例中创建档案。");
            SelectTab(MainTab.InstanceManage);
            SelectInstanceManageTab(InstanceManageTab.Profiles);
            return;
        }

        if (LaunchSaveListBox.SelectedItem is SaveFileEntry save)
        {
            profile.ActiveSaveFile = save.FullPath;
        }

        _isStoppingOrStarting = true;
        LaunchServerButton.IsEnabled = false;
        LaunchConfirmButton.IsEnabled = false;
        try
        {
            LaunchSelectionPopup.IsOpen = false;
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
        public required string FullPath { get; init; }

        public required string FileName { get; init; }

        public required string ProfileName { get; init; }

        public required string Description { get; init; }

        public static SaveListItem FromSave(SaveFileEntry save)
        {
            return new SaveListItem
            {
                FullPath = save.FullPath,
                FileName = save.FileName,
                ProfileName = save.ProfileName,
                Description = $"{FormatFileSize(save.SizeBytes)}  {save.LastWriteTimeUtc.LocalDateTime:yyyy-MM-dd HH:mm}  {save.FullPath}"
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
        string version,
        string actionText,
        bool canDownload)
    {
        public ServerDownloadEntry Entry { get; } = entry;

        public string Version { get; } = version;

        public string ActionText { get; } = actionText;

        public bool CanDownload { get; } = canDownload;
    }
}
