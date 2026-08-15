using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     部署网关 ServerId 重定向模组，并为关联实例生成路由配置。
/// </summary>
public interface IGatewayRedirectModService
{
    Task<int> DeployAsync(
        TcpGatewaySettings settings,
        IReadOnlyList<InstanceProfile> profiles,
        CancellationToken cancellationToken = default);
}
