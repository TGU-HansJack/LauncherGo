using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     ServerMap 内置地图模组服务
/// </summary>
public interface IServerMapService
{
    Task EnsureMapModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<bool> GetMapModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);
}
