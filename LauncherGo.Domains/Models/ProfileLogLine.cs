namespace LauncherGo.Domains.Models;

public sealed class ProfileLogLine
{
    public string ProfileId { get; init; } = string.Empty;

    public string ProfileName { get; init; } = string.Empty;

    public string Line { get; init; } = string.Empty;

    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
}
