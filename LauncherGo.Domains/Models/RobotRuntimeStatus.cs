namespace LauncherGo.Domains.Models;

/// <summary>
///     机器人运行状态
/// </summary>
public class RobotRuntimeStatus
{
    public bool IsRunning { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public string? OneBotWsUrl { get; init; }
}
