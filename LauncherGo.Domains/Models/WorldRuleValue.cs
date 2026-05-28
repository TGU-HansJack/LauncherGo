namespace LauncherGo.Domains.Models;

/// <summary>
///     世界规则值
/// </summary>
public class WorldRuleValue
{
    public required WorldRuleDefinition Definition { get; init; }

    public string? Value { get; set; }
}
