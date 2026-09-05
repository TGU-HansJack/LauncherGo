using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>Local-only v2 server bridge for query, command and event access.</summary>
public interface IServerBridgeService
{
    Task<ServerBridgeSettings> LoadSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(InstanceProfile profile, ServerBridgeSettings settings, CancellationToken cancellationToken = default);
    Task EnsureServerBridgeModDeployedAsync(InstanceProfile profile, CancellationToken cancellationToken = default, bool enableMod = true);
    Task<bool> GetServerBridgeModEnabledAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task SetServerBridgeModEnabledAsync(InstanceProfile profile, bool enabled, CancellationToken cancellationToken = default);
    Task<ServerBridgeRuntimeStatus> GetRuntimeStatusAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task RotateAccessTokenAsync(InstanceProfile profile, CancellationToken cancellationToken = default);
    Task<ServerBridgeQueryResult> QueryAsync(InstanceProfile profile, string method, JsonObject? arguments = null, CancellationToken cancellationToken = default);
    Task<ServerBridgeSubscription> SubscribeAsync(InstanceProfile profile, ServerBridgeSubscriptionOptions options, Func<ServerBridgeEvent, Task> handler, CancellationToken cancellationToken = default);
    Task ExecuteCommandAsync(InstanceProfile profile, string command, CancellationToken cancellationToken = default);
}
