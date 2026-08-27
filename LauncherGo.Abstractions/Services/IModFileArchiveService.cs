using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Creates an archive containing the server-compatible installed mods.
/// </summary>
public interface IModFileArchiveService
{
    Task CreateServerModArchiveAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        Stream destination,
        CancellationToken cancellationToken = default);
}
