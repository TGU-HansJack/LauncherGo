using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     独立 TCP 网关进程的生命周期和状态服务。
/// </summary>
public interface ITcpGatewayService
{
    event EventHandler<TcpGatewayRuntimeStatus>? StatusChanged;

    TcpGatewayRuntimeStatus GetCurrentStatus();

    Task<TcpGatewayRuntimeStatus> RefreshStatusAsync(CancellationToken cancellationToken = default);

    Task StartAsync(TcpGatewaySettings settings, CancellationToken cancellationToken = default);

    Task<TcpGatewayRuntimeStatus> ReloadAsync(TcpGatewaySettings settings, CancellationToken cancellationToken = default);

    Task RecordRoutingHistoryAsync(
        TcpGatewayRoutingHistoryEntry entry,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<TcpGatewayRoutingHistoryEntry>> GetRoutingHistoryAsync(
        int take = 100,
        CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
