using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     Creates portable exports of the installed mod list.
/// </summary>
public interface IModListExportService
{
    Task ExportAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        ModListExportFormat format,
        Stream destination,
        CancellationToken cancellationToken = default,
        ModListExportOptions? options = null);

    string GetFileExtension(ModListExportFormat format);
}
