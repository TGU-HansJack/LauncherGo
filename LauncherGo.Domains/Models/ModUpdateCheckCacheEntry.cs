namespace LauncherGo.Domains.Models;

/// <summary>
///     Persisted result of the last update check for one installed mod.
/// </summary>
public sealed class ModUpdateCheckCacheEntry
{
    public required string ProfileId { get; init; }

    public required string ModId { get; init; }

    public required string CurrentVersion { get; init; }

    /// <summary>
    ///     Available, Latest, or Failed.
    /// </summary>
    public required string Status { get; init; }

    public ModUpdateCheckResult? Result { get; init; }

    public DateTimeOffset CheckedAtUtc { get; init; }
}
