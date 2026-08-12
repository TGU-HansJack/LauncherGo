using LauncherGo.Domains.Enums;

namespace LauncherGo.Domains.Models;

public sealed class LauncherUpdateCheckResult
{
    public required LauncherUpdateRelease Release { get; init; }

    public required string CurrentVersion { get; init; }

    public required string LatestVersion { get; init; }

    public required LauncherPackageKind PackageKind { get; init; }

    public LauncherUpdateAsset? SelectedAsset { get; init; }

    public required bool IsUpdateAvailable { get; init; }
}
