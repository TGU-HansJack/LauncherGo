namespace LauncherGo.Domains.Enums;

/// <summary>
///     Fields available in a mod list export.
/// </summary>
[Flags]
public enum ModListExportColumn
{
    None = 0,
    Name = 1 << 0,
    ModId = 1 << 1,
    Version = 1 << 2,
    Side = 1 << 3,
    Dependencies = 1 << 4,
    Issues = 1 << 5,
    ConfigPath = 1 << 6,
    FilePath = 1 << 7,
    Enabled = 1 << 8,
    Status = 1 << 9,
    Website = 1 << 10,
    Default = Name | ModId | Version | Side | Dependencies | Issues | Enabled | Status | Website,
    All = Name | ModId | Version | Side | Dependencies | Issues | ConfigPath | FilePath | Enabled | Status | Website
}
