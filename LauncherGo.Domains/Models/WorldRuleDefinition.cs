namespace LauncherGo.Domains.Models;

/// <summary>
///     世界规则定义
/// </summary>
public class WorldRuleDefinition
{
    public required string Key { get; init; }

    public required string LabelZh { get; init; }

    public required string LabelEn { get; init; }

    public required WorldRuleType Type { get; init; }

    public IReadOnlyList<string> Choices { get; init; } = [];

    public IReadOnlyList<string> ChoiceNames { get; init; } = [];

    public string? DefaultValue { get; init; }

    public string? DescriptionZh { get; init; }

    public string? DescriptionEn { get; init; }
}
