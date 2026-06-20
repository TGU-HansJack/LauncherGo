using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IServerProcessService
{
    event EventHandler<string>? OutputReceived;

    event EventHandler<ServerOutputLine>? ProfileOutputReceived;

    event EventHandler<ServerRuntimeStatus>? StatusChanged;

    ServerRuntimeStatus GetCurrentStatus();

    ServerRuntimeStatus GetCurrentStatus(string profileId);

    IReadOnlyList<ServerRuntimeStatus> GetCurrentStatuses();

    ServerRuntimeStatus GetCachedStatus();

    IReadOnlyList<ServerRuntimeStatus> GetCachedStatuses();

    IReadOnlyList<string> GetOnlinePlayerNames();

    IReadOnlyList<string> GetOnlinePlayerNames(string profileId);

    IReadOnlyList<ServerOnlinePlayerInfo> GetOnlinePlayers();

    Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default);

    Task StopAsync(string profileId, TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string profileId, string command, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}
