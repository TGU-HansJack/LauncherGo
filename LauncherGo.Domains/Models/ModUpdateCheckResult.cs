namespace LauncherGo.Domains.Models;

/// <summary>
///     Official metadata returned by the Vintage Story mod database for one installed mod.
/// </summary>
public sealed class ModUpdateCheckResult
{
    public required string ModId { get; init; }

    public required string CurrentVersion { get; init; }

    public required string LatestVersion { get; init; }

    public required bool IsUpdateAvailable { get; init; }

    public string ReleaseDate { get; init; } = string.Empty;

    public string Changelog { get; init; } = string.Empty;

    public string HomepageUrl { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;
}
