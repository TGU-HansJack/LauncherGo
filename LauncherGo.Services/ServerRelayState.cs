namespace LauncherGo.Services;

internal sealed class ServerRelayState
{
    public int SchemaVersion { get; set; }

    public string InstanceId { get; set; } = string.Empty;

    public string ControlToken { get; set; } = string.Empty;

    public string PipeName { get; set; } = string.Empty;

    public int RelayProcessId { get; set; }

    public DateTimeOffset RelayStartedAtUtc { get; set; }

    public string HostExecutablePath { get; set; } = string.Empty;

    public int? ServerProcessId { get; set; }

    public DateTimeOffset? ServerProcessStartedAtUtc { get; set; }

    public bool RestartOnCrash { get; set; }

    public bool IsRestarting { get; set; }

    public int RestartCount { get; set; }

    public int? LastExitCode { get; set; }

    public string? LastError { get; set; }

    public string ProfileId { get; set; } = string.Empty;

    public string ProfileName { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DataPath { get; set; } = string.Empty;

    public string ServerExecutablePath { get; set; } = string.Empty;

    /// <summary>
    ///     Null is a legacy relay which did not report command-write state. False means stdin forwarding is still blocked.
    /// </summary>
    public bool? CommandChannelAvailable { get; set; }

    public string? LastCommandForwardError { get; set; }

    public DateTimeOffset StartedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

