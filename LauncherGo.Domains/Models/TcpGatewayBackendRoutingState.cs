namespace LauncherGo.Domains.Models;

/// <summary>
///     后端服务器在网关中的路由状态。
/// </summary>
public enum TcpGatewayBackendRoutingState
{
    /// <summary>
    ///     接收新连接、自动负载均衡和管理员重定向。
    /// </summary>
    Online,

    /// <summary>
    ///     不再接收普通新连接，但允许管理员将玩家迁入，已有连接不受影响。
    /// </summary>
    Draining,

    /// <summary>
    ///     拒绝普通新连接和重定向。
    /// </summary>
    Disabled,

    /// <summary>
    ///     仅用于运行时状态，表示健康检查不可达。
    /// </summary>
    Offline
}
