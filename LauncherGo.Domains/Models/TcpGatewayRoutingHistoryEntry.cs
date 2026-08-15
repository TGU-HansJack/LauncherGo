namespace LauncherGo.Domains.Models;

/// <summary>
///     网关路由、健康状态和重定向操作的审计记录。
/// </summary>
public sealed class TcpGatewayRoutingHistoryEntry
{
    public DateTimeOffset OccurredAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public string Action { get; set; } = string.Empty;

    public string SourceServerId { get; set; } = string.Empty;

    public string TargetServerId { get; set; } = string.Empty;

    public string Details { get; set; } = string.Empty;
}
