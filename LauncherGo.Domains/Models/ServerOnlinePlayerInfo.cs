namespace LauncherGo.Domains.Models;

public sealed class ServerOnlinePlayerInfo
{
    public string PlayerUid { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public DateTimeOffset? JoinedAtUtc { get; init; }

    public int? PingMilliseconds { get; init; }

    public string ConnectionState { get; init; } = string.Empty;

    public DateTimeOffset? LastActivityUtc { get; init; }

    public string GameMode { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public int? Dimension { get; init; }

    public double? X { get; init; }

    public double? Y { get; init; }

    public double? Z { get; init; }

    public bool HasExtendedInfo =>
        !string.IsNullOrWhiteSpace(GameMode) ||
        !string.IsNullOrWhiteSpace(Role) ||
        Dimension.HasValue ||
        X.HasValue ||
        Y.HasValue ||
        Z.HasValue;
}
