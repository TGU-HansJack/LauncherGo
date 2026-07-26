using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
/// Deployment and configuration service for the LauncherGo AntiCheat mod.
/// </summary>
public interface IServerAntiCheatService
{
    Task<AntiCheatSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        InstanceProfile profile,
        AntiCheatSettings settings,
        CancellationToken cancellationToken = default);

    Task EnsureAntiCheatModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default,
        bool enableMod = true);

    Task<bool> GetAntiCheatModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SetAntiCheatModEnabledAsync(
        InstanceProfile profile,
        bool enabled,
        CancellationToken cancellationToken = default);
}
