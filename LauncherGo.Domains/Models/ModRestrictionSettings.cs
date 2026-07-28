namespace LauncherGo.Domains.Models;

/// <summary>
///     Per-profile client mod restriction settings.
/// </summary>
public sealed class ModRestrictionSettings
{
    public bool BlacklistEnabled { get; set; }

    public bool ForceWhitelistEnabled { get; set; } = true;

    public List<string> WhitelistModIds { get; set; } = [];

    public List<string> BlacklistModIds { get; set; } = [];
}
