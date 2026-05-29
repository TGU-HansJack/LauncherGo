using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     ServerAuth 服务
/// </summary>
public interface IServerAuthService
{
    Task<ServerAuthSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerAuthSettings settings,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ServerAuthPlayerSummary>> GetPlayersAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<bool> ClearPasswordAsync(
        InstanceProfile profile,
        string playerUidOrName,
        CancellationToken cancellationToken = default);

    Task EnsureAuthModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<bool> GetAuthModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);
}

