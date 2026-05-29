namespace LauncherGo.Abstractions.Services;

/// <summary>
///     自动化调度服务
/// </summary>
public interface IAutomationService
{
    event EventHandler<string>? RuntimeLogReceived;

    IReadOnlyList<string> GetRuntimeLogs();

    Task ReloadAsync(CancellationToken cancellationToken = default);
}

