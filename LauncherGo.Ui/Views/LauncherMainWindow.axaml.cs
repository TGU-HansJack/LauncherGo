using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Transformation;
using Avalonia.Threading;

namespace LauncherGo.Ui.Views;

public partial class LauncherMainWindow : Window
{
    private const int RealtimeRangeSeconds = 60;
    private const int NetworkRangeCount = 144;
    private const double ChartWidth = 640;
    private const double ChartHeight = 248;
    private const double ThumbnailWidth = 76;
    private const double ThumbnailHeight = 50;
    private const double HostMemoryGb = 8.0;
    private const double RobotMemoryGb = 2.0;

    private readonly Random _random = new();
    private readonly DispatcherTimer _dataTimer;
    private readonly DispatcherTimer _tickerTimer;

    private readonly List<double> _serverCpuSamples = [];
    private readonly List<double> _serverMemPercentSamples = [];
    private readonly List<double> _robotCpuSamples = [];
    private readonly List<double> _robotMemPercentSamples = [];
    private readonly List<double> _playersSamples = [];
    private readonly List<double> _networkLatencySamples = [];

    private readonly List<string> _playerEvents = [];

    private readonly DateTime _serverStartUtc = DateTime.UtcNow;
    private readonly DateTime _robotStartUtc = DateTime.UtcNow;

    private MainTab _selectedTab = MainTab.Home;
    private HomeMetric _selectedMetric = HomeMetric.Server;
    private int _tickerIndex;
    private bool _tickerAnimating;
    private bool _isChinese;

    public LauncherMainWindow()
    {
        InitializeComponent();
        AddHandler(InputElement.PointerPressedEvent, OnWindowPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);

        _isChinese = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        _dataTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _dataTimer.Tick += OnDataTimerTick;

        _tickerTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(2.3)
        };
        _tickerTimer.Tick += OnTickerTimerTick;

        InitializeStaticTexts();
        InitializeLaunchOptions();
        InitializeMockSeries();

        SelectTab(MainTab.Home);
        SelectMetric(HomeMetric.Server);

        _dataTimer.Start();
        _tickerTimer.Start();

        Closed += (_, _) =>
        {
            _dataTimer.Stop();
            _tickerTimer.Stop();
        };
    }

    private void InitializeStaticTexts()
    {
        HomeNavButton.Content = T("主页", "Home");
        ConsoleNavButton.Content = T("控制台", "Console");
        ProfileNavButton.Content = T("档案列表", "Profiles");
        DownloadNavButton.Content = T("下载", "Download");
        SettingsNavButton.Content = T("设置", "Settings");

        LaunchServerButton.Content = T("启动服务器", "Start Server");

        ServerStatusCardTitleText.Text = T("服务器状态", "Server Status");
        RobotStatusCardTitleText.Text = T("机器人状态", "Robot Status");
        OnlinePlayersCardTitleText.Text = T("在线玩家", "Online Players");
        NetworkStatusCardTitleText.Text = T("网络状态", "Network Status");

        ConsolePlaceholderText.Text = T("控制台页面占位", "Console page placeholder");
        ProfilePlaceholderText.Text = T("档案列表页面占位", "Profile list page placeholder");
        DownloadPlaceholderText.Text = T("下载页面占位", "Download page placeholder");
        SettingsPlaceholderText.Text = T("设置页面占位", "Settings page placeholder");

        Title = T("LauncherGo 主窗口", "LauncherGo Main Window");
        ToolTip.SetTip(RepositoryButton, T("仓库", "Repository"));
        ToolTip.SetTip(FeedbackButton, T("反馈", "Feedback"));
        ToolTip.SetTip(SponsorButton, T("赞助", "Sponsor"));
    }

    private void InitializeLaunchOptions()
    {
        InstanceComboBox.ItemsSource = _isChinese
            ? new List<string> { "实例 A", "实例 B", "实例 C" }
            : new List<string> { "Instance A", "Instance B", "Instance C" };
        SaveComboBox.ItemsSource = _isChinese
            ? new List<string> { "存档 世界-1", "存档 世界-2", "存档 世界-3" }
            : new List<string> { "Save World-1", "Save World-2", "Save World-3" };

        InstanceComboBox.SelectedIndex = 0;
        SaveComboBox.SelectedIndex = 0;
    }

    private void InitializeMockSeries()
    {
        FillSeries(_serverCpuSamples, RealtimeRangeSeconds, 20, 72);
        FillSeries(_serverMemPercentSamples, RealtimeRangeSeconds, 33, 70);
        FillSeries(_robotCpuSamples, RealtimeRangeSeconds, 6, 38);
        FillSeries(_robotMemPercentSamples, RealtimeRangeSeconds, 22, 58);
        FillSeries(_playersSamples, RealtimeRangeSeconds, 2, 26, integerOnly: true);
        FillSeries(_networkLatencySamples, NetworkRangeCount, 11, 58);

        _playerEvents.Add(T("[12:00:05] HansJack 玩家进入服务器", "[12:00:05] HansJack joined the server"));
        _playerEvents.Add(T("[12:00:31] VSCN 玩家离开服务器", "[12:00:31] VSCN left the server"));
        _playerEvents.Add(T("[12:01:12] NightFox 玩家进入服务器", "[12:01:12] NightFox joined the server"));

        EventTickerCurrentText.Text = _playerEvents[0];
        EventTickerNextText.Text = _playerEvents.Count > 1 ? _playerEvents[1] : _playerEvents[0];

        UpdateCardValues();
    }

    private void FillSeries(List<double> target, int count, double min, double max, bool integerOnly = false)
    {
        target.Clear();
        for (var i = 0; i < count; i++)
        {
            var value = min + _random.NextDouble() * (max - min);
            target.Add(integerOnly ? Math.Round(value) : value);
        }
    }

    private void OnDataTimerTick(object? sender, EventArgs e)
    {
        PushNextSample(_serverCpuSamples, NextWithDrift(_serverCpuSamples[^1], 5.2, 10, 90));
        PushNextSample(_serverMemPercentSamples, NextWithDrift(_serverMemPercentSamples[^1], 2.8, 26, 86));
        PushNextSample(_robotCpuSamples, NextWithDrift(_robotCpuSamples[^1], 3.7, 3, 65));
        PushNextSample(_robotMemPercentSamples, NextWithDrift(_robotMemPercentSamples[^1], 2.2, 18, 70));

        var playerDelta = _random.Next(-1, 2);
        var currentPlayers = Math.Max(0, _playersSamples[^1] + playerDelta);
        PushNextSample(_playersSamples, currentPlayers);

        var shouldPushNetwork = DateTime.UtcNow.Second % 5 == 0;
        if (shouldPushNetwork)
        {
            PushNextSample(_networkLatencySamples, NextWithDrift(_networkLatencySamples[^1], 6.5, 8, 170), NetworkRangeCount);
        }

        if (playerDelta != 0)
        {
            var userName = PickRandomName();
            var eventText = playerDelta > 0
                ? T($"[{DateTime.Now:HH:mm:ss}] {userName} 玩家进入服务器", $"[{DateTime.Now:HH:mm:ss}] {userName} joined the server")
                : T($"[{DateTime.Now:HH:mm:ss}] {userName} 玩家离开服务器", $"[{DateTime.Now:HH:mm:ss}] {userName} left the server");
            _playerEvents.Insert(0, eventText);
            if (_playerEvents.Count > 24)
            {
                _playerEvents.RemoveAt(_playerEvents.Count - 1);
            }
        }

        UpdateCardValues();

        if (_selectedTab == MainTab.Home)
        {
            RenderSelectedMetricChart();
        }
    }

    private async void OnTickerTimerTick(object? sender, EventArgs e)
    {
        if (_selectedMetric != HomeMetric.Players || !_playerEvents.Any() || _tickerAnimating)
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

    private void UpdateCardValues()
    {
        var serverCpu = _serverCpuSamples[^1];
        var serverMemGb = HostMemoryGb * _serverMemPercentSamples[^1] / 100.0;
        ServerStatusCardValueText.Text = T(
            $"CPU {serverCpu:F1}%  内存 {serverMemGb:F2}/{HostMemoryGb:F1}GB",
            $"CPU {serverCpu:F1}%  Mem {serverMemGb:F2}/{HostMemoryGb:F1}GB");

        var robotCpu = _robotCpuSamples[^1];
        var robotMemGb = RobotMemoryGb * _robotMemPercentSamples[^1] / 100.0;
        RobotStatusCardValueText.Text = T(
            $"CPU {robotCpu:F1}%  内存 {robotMemGb:F2}/{RobotMemoryGb:F1}GB",
            $"CPU {robotCpu:F1}%  Mem {robotMemGb:F2}/{RobotMemoryGb:F1}GB");

        var currentPlayers = (int)Math.Round(_playersSamples[^1]);
        var peakPlayers = (int)Math.Round(_playersSamples.Max());
        OnlinePlayersCardValueText.Text = T(
            $"在线 {currentPlayers}  最高 {peakPlayers}",
            $"Online {currentPlayers}  Peak {peakPlayers}");

        var latency = _networkLatencySamples[^1];
        var packetLoss = Math.Clamp((int)Math.Round(latency / 90.0), 0, 4);
        NetworkStatusCardValueText.Text = T(
            $"延迟 {latency:F0}ms  丢包 {packetLoss}/4",
            $"Latency {latency:F0}ms  Loss {packetLoss}/4");

        RenderThumbnailCharts();
    }

    private void RenderSelectedMetricChart()
    {
        switch (_selectedMetric)
        {
            case HomeMetric.Server:
                var serverCpu = _serverCpuSamples[^1];
                var serverMemGb = HostMemoryGb * _serverMemPercentSamples[^1] / 100.0;
                var hostFreeGb = HostMemoryGb - serverMemGb;
                RenderDualLineChart(
                    title: T("服务器状态", "Server Status"),
                    topValue: $"{serverCpu:F1}% / {serverMemGb:F2} GB",
                    summary: T(
                        "60 秒区间，蓝线为 CPU，绿线为内存",
                        "60-second range. Blue is CPU, green is memory."),
                    primary: _serverCpuSamples,
                    secondary: _serverMemPercentSamples,
                    xHint: T("60 秒", "60 seconds"),
                    details:
                    [
                        (T("CPU", "CPU"), $"{serverCpu:F1}%"),
                        (T("内存占用", "Memory"), $"{serverMemGb:F2}/{HostMemoryGb:F1} GB"),
                        (T("本机可用", "Host free"), $"{hostFreeGb:F2} GB"),
                        (T("运行时间", "Uptime"), FormatDuration(DateTime.UtcNow - _serverStartUtc))
                    ]);
                break;

            case HomeMetric.Robot:
                var robotCpu = _robotCpuSamples[^1];
                var robotMemGb = RobotMemoryGb * _robotMemPercentSamples[^1] / 100.0;
                var robotFreeGb = RobotMemoryGb - robotMemGb;
                RenderDualLineChart(
                    title: T("机器人状态", "Robot Status"),
                    topValue: $"{robotCpu:F1}% / {robotMemGb:F2} GB",
                    summary: T(
                        "60 秒区间，蓝线为 CPU，绿线为内存",
                        "60-second range. Blue is CPU, green is memory."),
                    primary: _robotCpuSamples,
                    secondary: _robotMemPercentSamples,
                    xHint: T("60 秒", "60 seconds"),
                    details:
                    [
                        (T("CPU", "CPU"), $"{robotCpu:F1}%"),
                        (T("内存占用", "Memory"), $"{robotMemGb:F2}/{RobotMemoryGb:F1} GB"),
                        (T("本机可用", "Host free"), $"{robotFreeGb:F2} GB"),
                        (T("运行时间", "Uptime"), FormatDuration(DateTime.UtcNow - _robotStartUtc))
                    ]);
                break;

            case HomeMetric.Players:
                var currentPlayers = (int)Math.Round(_playersSamples[^1]);
                var peakPlayers = (int)Math.Round(_playersSamples.Max());
                RenderSingleLineChart(
                    title: T("在线玩家", "Online Players"),
                    topValue: T($"{currentPlayers} 人", $"{currentPlayers} players"),
                    summary: T(
                        "60 秒区间，显示在线玩家数量变化",
                        "60-second range for online player count."),
                    primary: _playersSamples,
                    yMin: 0,
                    yMax: Math.Max(20, _playersSamples.Max() + 3),
                    xHint: T("60 秒", "60 seconds"),
                    showTicker: true,
                    details:
                    [
                        (T("当前人数", "Current"), currentPlayers.ToString(CultureInfo.InvariantCulture)),
                        (T("最高人数", "Peak"), peakPlayers.ToString(CultureInfo.InvariantCulture)),
                        (T("事件数量", "Events"), _playerEvents.Count.ToString(CultureInfo.InvariantCulture)),
                        (T("采样区间", "Range"), T("60 秒", "60 seconds"))
                    ]);
                break;

            case HomeMetric.Network:
                var latency = _networkLatencySamples[^1];
                var packetLoss = Math.Clamp((int)Math.Round(latency / 90.0), 0, 4);
                RenderSingleLineChart(
                    title: T("网络状态", "Network Status"),
                    topValue: $"{latency:F0} ms",
                    summary: T(
                        "12 小时区间，5 分钟测试一次，每次发送 4 包",
                        "12-hour range. Tested every 5 minutes with 4 packets."),
                    primary: _networkLatencySamples,
                    yMin: 0,
                    yMax: Math.Max(180, _networkLatencySamples.Max() + 20),
                    xHint: T("最近 12 小时", "Last 12 hours"),
                    showTicker: false,
                    details:
                    [
                        (T("当前延迟", "Latency"), $"{latency:F0} ms"),
                        (T("丢包", "Packet loss"), $"{packetLoss}/4"),
                        (T("测试频率", "Frequency"), T("5 分钟", "5 min")),
                        (T("采样区间", "Range"), T("12 小时", "12 hours"))
                    ]);
                break;
        }
    }

    private void RenderDualLineChart(
        string title,
        string topValue,
        string summary,
        IReadOnlyList<double> primary,
        IReadOnlyList<double> secondary,
        string xHint,
        IReadOnlyList<(string Label, string Value)> details)
    {
        ChartTitleText.Text = title;
        ChartTopValueText.Text = topValue;
        ChartSummaryText.Text = summary;
        ChartXAxisText.Text = xHint;

        ChartLinePrimary.Points = BuildPolylinePoints(primary, yMin: 0, yMax: 100);
        ChartLineSecondary.Points = BuildPolylinePoints(secondary, yMin: 0, yMax: 100);
        ChartLineSecondary.IsVisible = true;

        SetYAxisLabels(0, 100, value => $"{value:F0}%");
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

        Func<double, string> formatter = _selectedMetric switch
        {
            HomeMetric.Players => value => $"{Math.Round(value):F0}",
            HomeMetric.Network => value => $"{Math.Round(value):F0}ms",
            _ => value => $"{value:F0}"
        };
        SetYAxisLabels(yMin, yMax, formatter);
        SetChartDetails(details);
        EventTickerContainer.IsVisible = showTicker;
    }

    private void SetYAxisLabels(double yMin, double yMax, Func<double, string> formatter)
    {
        var span = Math.Max(0.0001, yMax - yMin);
        var v0 = yMax;
        var v1 = yMin + span * 0.8;
        var v2 = yMin + span * 0.6;
        var v3 = yMin + span * 0.4;
        var v4 = yMin + span * 0.2;
        var v5 = yMin;

        YAxisLabelTop.Text = formatter(v0);
        YAxisLabel2.Text = formatter(v1);
        YAxisLabel3.Text = formatter(v2);
        YAxisLabel4.Text = formatter(v3);
        YAxisLabel5.Text = formatter(v4);
        YAxisLabelBottom.Text = formatter(v5);
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
        ServerStatusThumbLinePrimary.Points = BuildPolylinePoints(_serverCpuSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        ServerStatusThumbLineSecondary.Points = BuildPolylinePoints(_serverMemPercentSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        RobotStatusThumbLinePrimary.Points = BuildPolylinePoints(_robotCpuSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        RobotStatusThumbLineSecondary.Points = BuildPolylinePoints(_robotMemPercentSamples, 0, 100, ThumbnailWidth, ThumbnailHeight);
        OnlinePlayersThumbLinePrimary.Points = BuildPolylinePoints(_playersSamples, 0, Math.Max(20, _playersSamples.Max() + 3), ThumbnailWidth, ThumbnailHeight);
        NetworkStatusThumbLinePrimary.Points = BuildPolylinePoints(_networkLatencySamples, 0, Math.Max(180, _networkLatencySamples.Max() + 20), ThumbnailWidth, ThumbnailHeight);
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

    private void SelectTab(MainTab tab)
    {
        _selectedTab = tab;

        HomePanel.IsVisible = tab == MainTab.Home;
        ConsolePanel.IsVisible = tab == MainTab.Console;
        ProfilePanel.IsVisible = tab == MainTab.ProfileList;
        DownloadPanel.IsVisible = tab == MainTab.Download;
        SettingsPanel.IsVisible = tab == MainTab.Settings;

        SetSelectedClass(HomeNavButton, tab == MainTab.Home);
        SetSelectedClass(ConsoleNavButton, tab == MainTab.Console);
        SetSelectedClass(ProfileNavButton, tab == MainTab.ProfileList);
        SetSelectedClass(DownloadNavButton, tab == MainTab.Download);
        SetSelectedClass(SettingsNavButton, tab == MainTab.Settings);

        if (tab == MainTab.Home)
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

    private static void SetSelectedClass(StyledElement element, bool selected)
    {
        element.Classes.Set("selected", selected);
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
            var psi = new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                ChartSummaryText.Text = T("无法打开链接。", "Unable to open the link.");
            });
        }
    }

    private static string FormatDuration(TimeSpan duration)
    {
        if (duration.TotalDays >= 1)
        {
            return $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}";
        }

        return duration.ToString("hh\\:mm\\:ss");
    }

    private static double NextWithDrift(double current, double maxDelta, double min, double max)
    {
        var next = current + (Random.Shared.NextDouble() * 2 - 1) * maxDelta;
        return Math.Clamp(next, min, max);
    }

    private static void PushNextSample(List<double> samples, double value, int maxCount = RealtimeRangeSeconds)
    {
        if (samples.Count >= maxCount)
        {
            samples.RemoveAt(0);
        }

        samples.Add(value);
    }

    private string PickRandomName()
    {
        string[] names = ["HansJack", "VSCN", "NightFox", "Aster", "Maple", "Sora"];
        return names[_random.Next(names.Length)];
    }

    private string T(string zh, string en) => _isChinese ? zh : en;

    private void OnRepositoryClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com");

    private void OnFeedbackClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/issues");

    private void OnSponsorClick(object? sender, RoutedEventArgs e) => OpenUrl("https://github.com/sponsors");

    private void OnMinimizeClick(object? sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void OnToggleMaximizeClick(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void OnHomeNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Home);

    private void OnConsoleNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Console);

    private void OnProfileNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.ProfileList);

    private void OnDownloadNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Download);

    private void OnSettingsNavClick(object? sender, RoutedEventArgs e) => SelectTab(MainTab.Settings);

    private void OnServerStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Server);

    private void OnRobotStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Robot);

    private void OnOnlinePlayersCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Players);

    private void OnNetworkStatusCardClick(object? sender, RoutedEventArgs e) => SelectMetric(HomeMetric.Network);

    private void OnLaunchHoverAreaPointerEntered(object? sender, PointerEventArgs e)
    {
        LaunchOptionsContainer.Classes.Set("expanded", true);
    }

    private void OnLaunchHoverAreaPointerExited(object? sender, PointerEventArgs e)
    {
        LaunchOptionsContainer.Classes.Set("expanded", false);
    }

    private void OnLaunchServerClick(object? sender, RoutedEventArgs e)
    {
        var instance = InstanceComboBox.SelectedItem?.ToString() ?? T("实例 A", "Instance A");
        var save = SaveComboBox.SelectedItem?.ToString() ?? T("存档 世界-1", "Save World-1");
        _playerEvents.Insert(0, T(
            $"[{DateTime.Now:HH:mm:ss}] 启动请求：{instance} + {save}",
            $"[{DateTime.Now:HH:mm:ss}] Launch requested: {instance} + {save}"));

        if (_playerEvents.Count > 24)
        {
            _playerEvents.RemoveAt(_playerEvents.Count - 1);
        }

        if (_selectedTab != MainTab.Home)
        {
            SelectTab(MainTab.Home);
        }

        SelectMetric(HomeMetric.Players);
    }

    private enum MainTab
    {
        Home,
        Console,
        ProfileList,
        Download,
        Settings
    }

    private enum HomeMetric
    {
        Server,
        Robot,
        Players,
        Network
    }
}
