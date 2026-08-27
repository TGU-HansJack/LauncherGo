using LauncherGo.Domains.Enums;

namespace LauncherGo.Domains.Models;

/// <summary>
///     User-selected fields and metadata behavior for a mod list export.
/// </summary>
public sealed class ModListExportOptions
{
    public ModListExportColumn Columns { get; init; } = ModListExportColumn.Default;

    public bool ResolveWebsiteUrls { get; init; } = true;
}
