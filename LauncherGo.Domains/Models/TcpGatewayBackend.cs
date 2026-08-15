using System.Text.Json.Serialization;

namespace LauncherGo.Domains.Models;

/// <summary>
///     TCP 网关的后端服务器。
/// </summary>
public sealed class TcpGatewayBackend
{
    /// <summary>
    ///     面向路由和重定向模组公开的稳定 ServerId，不会暴露真实主机地址。
    /// </summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; }

    public int Weight { get; set; } = 1;

    /// <summary>
    ///     决定新玩家的路由策略。已有 TCP 会话不会因状态变化中断。
    /// </summary>
    public TcpGatewayBackendRoutingState RoutingState { get; set; } = TcpGatewayBackendRoutingState.Online;

    /// <summary>
    ///     进入维护状态时的默认疏散目标 ServerId。
    /// </summary>
    public string MaintenanceTargetServerId { get; set; } = string.Empty;

    /// <summary>
    ///     可选的 LauncherGo 本地实例关联，用于部署模组和下发重定向命令。
    /// </summary>
    public string ProfileId { get; set; } = string.Empty;

    /// <summary>
    ///     兼容 2.5.7 及更早版本的配置。新配置请使用 <see cref="RoutingState"/>。
    /// </summary>
    public bool Enabled
    {
        get => RoutingState is not TcpGatewayBackendRoutingState.Disabled;
        set
        {
            if (!value)
            {
                RoutingState = TcpGatewayBackendRoutingState.Disabled;
            }
            else if (RoutingState == TcpGatewayBackendRoutingState.Disabled)
            {
                RoutingState = TcpGatewayBackendRoutingState.Online;
            }
        }
    }

    [JsonIgnore]
    public IReadOnlyList<TcpGatewayBackendRoutingState> RoutingStateOptions { get; } =
    [
        TcpGatewayBackendRoutingState.Online,
        TcpGatewayBackendRoutingState.Draining,
        TcpGatewayBackendRoutingState.Disabled
    ];

    [JsonIgnore]
    public IReadOnlyList<InstanceProfile> ProfileOptions { get; set; } = [];

    [JsonIgnore]
    public InstanceProfile? SelectedProfile
    {
        get => ProfileOptions.FirstOrDefault(profile =>
            profile.Id.Equals(ProfileId, StringComparison.OrdinalIgnoreCase));
        set => ProfileId = value?.Id ?? string.Empty;
    }
}
