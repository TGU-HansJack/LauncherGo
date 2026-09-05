using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface ILauncherUpdateService
{
    string CurrentVersion { get; }

    LauncherPackageKind PackageKind { get; }

    Task<LauncherUpdateCheckResult> CheckLatestAsync(
        GitHubProxyKind proxy,
        bool includePrerelease = false,
        CancellationToken cancellationToken = default);

    Task PrepareAndLaunchUpdateAsync(
        LauncherUpdateCheckResult update,
        GitHubProxyKind proxy,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);
}
