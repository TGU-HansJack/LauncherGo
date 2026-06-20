namespace LauncherGo.Domains.Models;

public sealed class ServerOnlinePlayerInfo
{
    public string PlayerName { get; init; } = string.Empty;

    public string ProfileId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public DateTimeOffset? JoinedAtUtc { get; init; }
}
