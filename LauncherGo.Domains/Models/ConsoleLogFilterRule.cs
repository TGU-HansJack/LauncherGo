using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LauncherGo.Domains.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ConsoleLogFilterMode
{
    Contains,
    Exact,
    Regex
}

public sealed class ConsoleLogFilterRule
{
    public bool Enabled { get; set; } = true;

    public ConsoleLogFilterMode Mode { get; set; } = ConsoleLogFilterMode.Contains;

    public string Pattern { get; set; } = string.Empty;
}

public static class ConsoleLogFilterRuleRules
{
    public static List<ConsoleLogFilterRule> NormalizeMany(IEnumerable<ConsoleLogFilterRule>? rules)
    {
        var result = new List<ConsoleLogFilterRule>();
        foreach (var rule in rules ?? [])
        {
            if (rule is null)
            {
                continue;
            }

            var pattern = rule.Pattern?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(pattern))
            {
                continue;
            }

            result.Add(new ConsoleLogFilterRule
            {
                Enabled = rule.Enabled,
                Mode = Enum.IsDefined(rule.Mode) ? rule.Mode : ConsoleLogFilterMode.Contains,
                Pattern = pattern
            });
        }

        return result;
    }

    public static bool Matches(ConsoleLogFilterRule? rule, string? line)
    {
        if (rule is null ||
            !rule.Enabled ||
            string.IsNullOrEmpty(line) ||
            string.IsNullOrWhiteSpace(rule.Pattern))
        {
            return false;
        }

        var pattern = rule.Pattern.Trim();
        return rule.Mode switch
        {
            ConsoleLogFilterMode.Contains => line.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            ConsoleLogFilterMode.Exact => line.Equals(pattern, StringComparison.OrdinalIgnoreCase),
            ConsoleLogFilterMode.Regex => IsRegexMatch(pattern, line),
            _ => false
        };
    }

    public static bool MatchesAny(IEnumerable<ConsoleLogFilterRule>? rules, string? line)
    {
        return rules?.Any(rule => Matches(rule, line)) == true;
    }

    private static bool IsRegexMatch(string pattern, string line)
    {
        try
        {
            return Regex.IsMatch(
                line,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                TimeSpan.FromMilliseconds(100));
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
