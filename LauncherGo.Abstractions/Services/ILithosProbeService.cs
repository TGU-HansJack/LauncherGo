using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
/// Retrieves official Lithos Probe releases and reads reports exported by the mod.
/// </summary>
public interface ILithosProbeService
{
    Task<IReadOnlyList<LithosProbeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default);

    Task<LithosProbeProfileSnapshot> GetProfileSnapshotAsync(
        InstanceProfile profile,
        IReadOnlyList<LithosProbeRelease> releases,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LithosProbeReportFile>> GetReportsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);
}
