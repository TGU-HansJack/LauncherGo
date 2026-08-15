namespace LauncherGo.Domains.Models;

/// <summary>
///     LauncherGo Command Bridge settings stored with an instance profile.
/// </summary>
public sealed class CommandBridgeSettings
{
    public bool Enabled { get; init; }

    /// <summary>
    ///     The bridge always binds to 127.0.0.1. A distinct port is required for each concurrently running profile.
    /// </summary>
    public int Port { get; init; }

    /// <summary>
    ///     Random 256-bit token shared only by LauncherGo and the local server mod.
    /// </summary>
    public string AccessToken { get; init; } = string.Empty;

    public int CommandTimeoutMilliseconds { get; init; } = 5000;

    public int MaxCommandLength { get; init; } = 4096;

    /// <summary>
    ///     Retain the legacy ServerHost relay as a compatibility fallback when the bridge is unavailable.
    /// </summary>
    public bool AllowRelayFallback { get; init; } = true;
}

public enum CommandBridgeRuntimeState
{
    Disabled,
    NotDeployed,
    Unavailable,
    Ready
}

public sealed class CommandBridgeRuntimeStatus
{
    public CommandBridgeRuntimeState State { get; init; }

    public string Message { get; init; } = string.Empty;

    public int Port { get; init; }

    public string Version { get; init; } = string.Empty;

    public bool IsReady => State == CommandBridgeRuntimeState.Ready;
}
