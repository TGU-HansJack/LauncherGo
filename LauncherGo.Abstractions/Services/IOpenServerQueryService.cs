using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     开放信息（OSQ）服务
/// </summary>
public interface IOpenServerQueryService
{
    event EventHandler<string>? OutputReceived;

    Task<OpenServerQueryRuntimeSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(OpenServerQueryRuntimeSettings settings, CancellationToken cancellationToken = default);

    OpenServerQueryRuntimeStatus GetRuntimeStatus();

    Task StartAsync(OpenServerQueryRuntimeSettings settings, CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
