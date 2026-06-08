using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IServerProcessService
{
    event EventHandler<string>? OutputReceived;

    event EventHandler<ServerRuntimeStatus>? StatusChanged;

    ServerRuntimeStatus GetCurrentStatus();

    ServerRuntimeStatus GetCachedStatus();

    IReadOnlyList<string> GetOnlinePlayerNames();

    Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);

    Task SendCommandAsync(string command, CancellationToken cancellationToken = default);
}
