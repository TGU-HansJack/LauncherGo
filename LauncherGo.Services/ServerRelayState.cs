namespace LauncherGo.Services;

internal sealed class ServerRelayState
{
    public int SchemaVersion { get; set; } = 1;

    public string PipeName { get; set; } = string.Empty;

    public int RelayProcessId { get; set; }

    public int? ServerProcessId { get; set; }

    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DataPath { get; set; } = string.Empty;

    public string ServerExecutablePath { get; set; } = string.Empty;

    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

