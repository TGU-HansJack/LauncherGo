namespace LauncherGo.Domains.Models;

/// <summary>
///     EasyTier 运行状态。
/// </summary>
public sealed class EasyTierRuntimeStatus
{
    public bool IsRunning { get; set; }

    public bool IsReady { get; set; }

    public int? ProcessId { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public int RpcPort { get; set; }

    public int ControlPort { get; set; }

    public int ConnectedPeerCount { get; set; }

    public int ConnectedPlayerCount { get; set; }

    public string LocalIpV4 { get; set; } = string.Empty;

    public string NetworkName { get; set; } = string.Empty;

    public string RoomCode { get; set; } = string.Empty;

    public string GameAddress { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;
}
