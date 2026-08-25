using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Queries the official Vintage Story mod database for release metadata.
/// </summary>
public interface IModUpdateService
{
    Task<ModUpdateCheckResult> CheckAsync(
        ModEntry mod,
        CancellationToken cancellationToken = default);
}
