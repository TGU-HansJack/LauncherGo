namespace LauncherGo.Domains.Models;

/// <summary>
///     模组依赖项
/// </summary>
public class ModDependency
{
    public required string ModId { get; init; }

    public string? Version { get; init; }
}

