using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Domains.Models;

namespace LauncherGo.Ui.Views;

public partial class GatewayBackendStatisticsWindow : Window
{
    private bool _isChinese;
    private readonly ObservableCollection<GatewayBackendStatisticItem> _items = [];
    private readonly ObservableCollection<GatewayDisconnectItem> _disconnectItems = [];
    private string _disconnectsFingerprint = "__uninitialized__";

    public GatewayBackendStatisticsWindow()
    {
        InitializeComponent();
    }

    public GatewayBackendStatisticsWindow(TcpGatewayBackendRuntimeStatus backend, bool isChinese)
        : this()
    {
        _isChinese = isChinese;
        MetricItemsControl.ItemsSource = _items;
        DisconnectItemsControl.ItemsSource = _disconnectItems;
        MetricHeaderTextBlock.Text = T("指标", "Metric");
        ValueHeaderTextBlock.Text = T("数值", "Value");
        DisconnectsTitleTextBlock.Text = T("最近断开记录", "Recent disconnects");
        DisconnectTimeHeaderTextBlock.Text = T("时间", "Time");
        DisconnectTypeHeaderTextBlock.Text = T("类型", "Type");
        DisconnectDetailsHeaderTextBlock.Text = T("详细信息", "Details");
        CloseButton.Content = T("关闭", "Close");
        UpdateStatus(backend);
    }

    public string BackendId { get; private set; } = string.Empty;

    public void UpdateStatus(TcpGatewayBackendRuntimeStatus backend)
    {
        BackendId = backend.Id;
        Title = T("后端转发统计", "Backend Relay Statistics");
        TitleTextBlock.Text = string.IsNullOrWhiteSpace(backend.Name) ? backend.Id : backend.Name;
        AddressTextBlock.Text = backend.Address;
        LogPathTextBlock.Text = string.IsNullOrWhiteSpace(backend.StatisticsLogPath)
            ? T("统计日志将在网关运行后创建。", "The statistics log is created while the gateway is running.")
            : backend.StatisticsLogPath;
        ToolTip.SetTip(LogPathTextBlock, LogPathTextBlock.Text);

        var statistics = backend.Statistics ?? new TcpGatewayBackendStatistics();
        _items.Clear();
        Add(T("实时上行", "Current upstream"), FormatMbps(statistics.CurrentClientToBackendMbps));
        Add(T("实时下行", "Current downstream"), FormatMbps(statistics.CurrentBackendToClientMbps));
        Add(T("峰值上行", "Peak upstream"), FormatMbps(statistics.PeakClientToBackendMbps));
        Add(T("峰值下行", "Peak downstream"), FormatMbps(statistics.PeakBackendToClientMbps));
        Add(T("平均上行", "Average upstream"), FormatMbps(statistics.AverageClientToBackendMbps));
        Add(T("平均下行", "Average downstream"), FormatMbps(statistics.AverageBackendToClientMbps));
        Add(T("累计上行", "Total upstream"), FormatDataSize(statistics.ClientToBackendBytes));
        Add(T("累计下行", "Total downstream"), FormatDataSize(statistics.BackendToClientBytes));
        Add(T("连接统计持续时间", "Connection statistics duration"), FormatDuration(statistics.StartedAtUtc));
        Add(T("当前连接数", "Current connections"), statistics.CurrentConnections.ToString());
        Add(T("历史最大连接数", "Peak connections"), statistics.PeakConnections.ToString());
        Add(T("连接建立速率", "Connection establish rate"), $"{statistics.ConnectionEstablishRatePerMinute:F2} {T("次/分钟", "connections/min")}");
        Add(T("连接失败率", "Connection failure rate"), $"{statistics.ConnectionFailureRate:F2}%");
        Add(T("转发延迟（后端 TCP 建连）", "Relay latency (backend TCP connect)"), $"{statistics.AverageBackendConnectLatencyMilliseconds:F2} ms");

        var recentDisconnects = statistics.RecentDisconnects.Take(10).ToList();
        var disconnectsFingerprint = string.Join(
            '\u001F',
            recentDisconnects.Select(disconnect =>
                $"{disconnect.OccurredAtUtc.UtcTicks}\u001E{disconnect.Type}\u001E{disconnect.Details}"));
        if (string.Equals(_disconnectsFingerprint, disconnectsFingerprint, StringComparison.Ordinal))
        {
            return;
        }

        _disconnectsFingerprint = disconnectsFingerprint;
        _disconnectItems.Clear();
        foreach (var disconnect in recentDisconnects)
        {
            _disconnectItems.Add(new GatewayDisconnectItem(
                disconnect.OccurredAtUtc == default
                    ? "--"
                    : disconnect.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                TranslateDisconnectType(disconnect.Type),
                string.IsNullOrWhiteSpace(disconnect.Details)
                    ? T("无详细信息", "No additional details")
                    : disconnect.Details));
        }

        if (_disconnectItems.Count == 0)
        {
            _disconnectItems.Add(new GatewayDisconnectItem(
                "--",
                T("暂无记录", "No records"),
                T("尚未发生断开。", "No disconnect has occurred.")));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private void Add(string label, string value) => _items.Add(new GatewayBackendStatisticItem(label, value));

    private string TranslateDisconnectType(string type)
    {
        if (string.IsNullOrWhiteSpace(type))
        {
            return T("未知", "Unknown");
        }

        return type switch
        {
            "ClientClosed" => T("客户端关闭", "Client closed"),
            "BackendClosed" => T("后端关闭", "Backend closed"),
            "GatewayStopped" => T("网关停止", "Gateway stopped"),
            "RelayError" => T("转发异常", "Relay error"),
            _ => type
        };
    }

    private string T(string zh, string en) => _isChinese ? zh : en;

    private static string FormatMbps(double value) => $"{value:F3} Mbps";

    private static string FormatDataSize(long bytes)
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

    private static string FormatDuration(DateTimeOffset startedAtUtc)
    {
        if (startedAtUtc == default)
        {
            return "--";
        }

        var duration = DateTimeOffset.UtcNow - startedAtUtc;
        return duration.TotalDays >= 1
            ? $"{(int)duration.TotalDays}d {duration:hh\\:mm\\:ss}"
            : duration.ToString("hh\\:mm\\:ss");
    }

}

public sealed record GatewayBackendStatisticItem(string Label, string Value);

public sealed record GatewayDisconnectItem(string OccurredAtText, string TypeText, string DetailsText);
