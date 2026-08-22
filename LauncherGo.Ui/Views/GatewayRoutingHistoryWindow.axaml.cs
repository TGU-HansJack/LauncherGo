using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using LauncherGo.Domains.Models;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui;

namespace LauncherGo.Ui.Views;

public partial class GatewayRoutingHistoryWindow : Window
{
    private readonly bool _isChinese;
    private readonly ObservableCollection<GatewayRoutingHistoryItem> _items = [];

    public GatewayRoutingHistoryWindow()
    {
        InitializeComponent();
    }

    public GatewayRoutingHistoryWindow(
        IReadOnlyList<TcpGatewayRoutingHistoryEntry> history,
        string logPath,
        bool isChinese)
        : this()
    {
        _isChinese = isChinese;
        Title = T("网关路由历史", "Gateway Routing History");
        TitleTextBlock.Text = Title;
        PathTextBlock.Text = string.IsNullOrWhiteSpace(logPath)
            ? T("日志将在网关运行或执行路由操作后创建。", "The log is created after the gateway runs or a routing operation is performed.")
            : logPath;
        ToolTip.SetTip(PathTextBlock, PathTextBlock.Text);
        TimeHeaderTextBlock.Text = T("时间", "Time");
        ActionHeaderTextBlock.Text = T("事件", "Event");
        SourceHeaderTextBlock.Text = T("来源 ServerId", "Source ServerId");
        TargetHeaderTextBlock.Text = T("目标 ServerId", "Target ServerId");
        DetailsHeaderTextBlock.Text = T("详细信息", "Details");
        CloseButton.Content = T("关闭", "Close");
        HistoryItemsControl.ItemsSource = _items;
        foreach (var entry in history)
        {
            _items.Add(new GatewayRoutingHistoryItem(
                entry.OccurredAtUtc == default ? "--" : entry.OccurredAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"),
                TranslateAction(entry.Action),
                string.IsNullOrWhiteSpace(entry.SourceServerId) ? "-" : entry.SourceServerId,
                string.IsNullOrWhiteSpace(entry.TargetServerId) ? "-" : entry.TargetServerId,
                string.IsNullOrWhiteSpace(entry.Details) ? "-" : TranslateDetails(entry.Details)));
        }

        if (_items.Count == 0)
        {
            _items.Add(new GatewayRoutingHistoryItem("--", T("暂无记录", "No records"), "-", "-", T("尚未产生路由或健康检查记录。", "No routing or health-check records yet.")));
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e) => Close();

    private string TranslateAction(string action) => action switch
    {
        "HealthChanged" => T("健康状态变化", "Health changed"),
        "Failover" => T("故障转移", "Failover"),
        "TicketRedirect" => T("凭证重定向", "Ticket redirect"),
        "PlayerRedirect" => T("玩家重定向", "Player redirect"),
        "Evacuate" => T("整服疏散", "Evacuate"),
        "Maintenance" => T("维护模式", "Maintenance"),
        "RoutingStateChanged" => T("路由状态变化", "Routing state changed"),
        _ => string.IsNullOrWhiteSpace(action) ? T("未知", "Unknown") : action
    };

    private string TranslateDetails(string details)
    {
        if (!_isChinese)
        {
            return details;
        }

        const string healthCheckFailedPrefix = "Backend TCP health check failed: ";
        const string routingStateChangedPrefix = "Routing state changed from ";

        if (details.StartsWith(healthCheckFailedPrefix, StringComparison.Ordinal))
        {
            return $"后端 TCP 健康检查失败：{details[healthCheckFailedPrefix.Length..]}";
        }

        if (details.StartsWith(routingStateChangedPrefix, StringComparison.Ordinal) &&
            details.EndsWith(".", StringComparison.Ordinal))
        {
            var states = details[routingStateChangedPrefix.Length..^1].Split(" to ", StringSplitOptions.None);
            if (states.Length == 2)
            {
                return $"路由状态已从 {TranslateRoutingState(states[0])} 变更为 {TranslateRoutingState(states[1])}。";
            }
        }

        return details switch
        {
            "A one-time transfer ticket selected the target backend." => "一次性转移凭证已选择目标后端。",
            "A backend connection failed and the gateway selected another healthy backend." => "后端连接失败，网关已选择另一个健康后端。",
            "Backend is disabled." => "后端已禁用。",
            "Backend TCP health check is reachable." => "后端 TCP 健康检查可达。",
            "Backend entered Draining maintenance mode." => "后端已进入 Draining 维护模式。",
            "Evacuation command sent to the associated local server instance." => "已向关联的本地服务端实例发送疏散命令。",
            _ when details.StartsWith("Redirect command sent for player '", StringComparison.Ordinal) &&
                   details.EndsWith("'.", StringComparison.Ordinal) =>
                $"已向玩家 '{details["Redirect command sent for player '".Length..^2]}' 发送重定向命令。",
            _ => details
        };
    }

    private static string TranslateRoutingState(string state) => state switch
    {
        "Online" => "在线",
        "Draining" => "排空中",
        "Disabled" => "已禁用",
        "Offline" => "离线",
        _ => state
    };

    private string T(string zh, string en)
    {
        try
        {
            return ServiceLocator.GetRequiredService<ILocalizationService>().Resolve(zh, en);
        }
        catch (InvalidOperationException)
        {
            return _isChinese ? zh : en;
        }
    }
}

public sealed record GatewayRoutingHistoryItem(
    string TimeText,
    string ActionText,
    string SourceText,
    string TargetText,
    string DetailsText);
