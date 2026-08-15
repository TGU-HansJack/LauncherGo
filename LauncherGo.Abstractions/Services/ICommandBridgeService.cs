using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Deploys and talks to the in-server LauncherGo Command Bridge mod.
/// </summary>
public interface ICommandBridgeService
{
    Task<CommandBridgeSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        InstanceProfile profile,
        CommandBridgeSettings settings,
        CancellationToken cancellationToken = default);

    Task EnsureCommandBridgeModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default,
        bool enableMod = true);

    Task<bool> GetCommandBridgeModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SetCommandBridgeModEnabledAsync(
        InstanceProfile profile,
        bool enabled,
        CancellationToken cancellationToken = default);

    Task<CommandBridgeRuntimeStatus> GetRuntimeStatusAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    /// <summary>
    ///     Replaces the access token in a running bridge and persists the replacement for future starts.
    /// </summary>
    Task RotateAccessTokenAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SendCommandAsync(
        InstanceProfile profile,
        string command,
        CancellationToken cancellationToken = default);
}
