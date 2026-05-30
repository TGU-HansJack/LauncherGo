using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     服务器日志跟随服务
/// </summary>
public interface ILogTailService : IDisposable
{
    event EventHandler<string>? LogLineReceived;

    Task StartAsync(InstanceProfile profile, bool replayExisting = false, CancellationToken cancellationToken = default);

    Task StopAsync(CancellationToken cancellationToken = default);
}

