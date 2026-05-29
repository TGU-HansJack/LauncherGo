using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     VS2QQ 机器人服务
/// </summary>
public interface IRobotService
{
    Task<RobotSettings> LoadSettingsAsync(CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(RobotSettings settings, CancellationToken cancellationToken = default);

    RobotRuntimeStatus GetCurrentStatus();

    IReadOnlyList<string> GetConsoleLines();

    void ClearConsole();

    Task StartAsync(RobotSettings settings, CancellationToken cancellationToken = default);

    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}
