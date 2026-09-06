using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IDiscordBotService
{
    event EventHandler<DiscordRuntimeStatus>? StatusChanged;
    event EventHandler<string>? OutputReceived;
    Task<DiscordIntegrationSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);
    Task SaveSettingsAsync(DiscordIntegrationSettings settings, CancellationToken cancellationToken = default);
    DiscordRuntimeStatus GetCurrentStatus();
    IReadOnlyList<string> GetConsoleLines();
    void ClearConsole();
    Task StartAsync(DiscordIntegrationSettings settings, CancellationToken cancellationToken = default);
    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
    Task RedeployCommandsAsync(CancellationToken cancellationToken = default);
}
