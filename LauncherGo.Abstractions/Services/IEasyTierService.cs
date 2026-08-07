using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     EasyTier 房间服务。
/// </summary>
public interface IEasyTierService
{
    event EventHandler<EasyTierRuntimeStatus>? StatusChanged;

    string CoreExecutablePath { get; }

    string CliExecutablePath { get; }

    EasyTierRuntimeStatus GetCurrentStatus();

    Task ImportCoreExecutableAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task ImportCliExecutableAsync(string sourcePath, CancellationToken cancellationToken = default);

    Task StartAsync(CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
