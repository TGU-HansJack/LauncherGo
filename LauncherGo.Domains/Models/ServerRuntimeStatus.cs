namespace LauncherGo.Domains.Models;

public sealed class ServerRuntimeStatus
{
    public bool IsRunning { get; init; }

    public int? ProcessId { get; init; }

    public DateTimeOffset? StartedAtUtc { get; init; }

    public string? ProfileId { get; init; }

    public double CpuPercent { get; init; }

    public long MemoryBytes { get; init; }

    public int OnlinePlayers { get; init; }

    public int PeakOnlinePlayers { get; init; }

    public bool CanSendCommands { get; init; }

    public string ControlMode { get; init; } = string.Empty;

    public string Message { get; init; } = string.Empty;
}
