using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Creates an archive containing installed mod files.
/// </summary>
public interface IModFileArchiveService
{
    Task CreateModArchiveAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        ModFileArchiveScope scope,
        Stream destination,
        CancellationToken cancellationToken = default);
}
