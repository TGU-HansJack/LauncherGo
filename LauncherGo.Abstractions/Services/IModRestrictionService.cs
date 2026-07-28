using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Manages per-profile client mod loading restrictions.
/// </summary>
public interface IModRestrictionService
{
    Task<ModRestrictionSettings> LoadAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        InstanceProfile profile,
        ModRestrictionSettings settings,
        CancellationToken cancellationToken = default);

    string GetSettingsPath(InstanceProfile profile);
}
