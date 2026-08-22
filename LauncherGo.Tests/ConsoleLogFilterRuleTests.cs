using LauncherGo.Domains.Models;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ConsoleLogFilterRuleTests
{
    [Theory]
    [InlineData(ConsoleLogFilterMode.Contains, "connection", "Connection accepted")]
    [InlineData(ConsoleLogFilterMode.Exact, "Connection accepted", "Connection accepted")]
    [InlineData(ConsoleLogFilterMode.Regex, @"player\s+\d+", "Player 42 joined")]
    public void MatchesConfiguredMode(ConsoleLogFilterMode mode, string pattern, string line)
    {
        Assert.True(ConsoleLogFilterRuleRules.Matches(
            new ConsoleLogFilterRule { Mode = mode, Pattern = pattern },
            line));
    }

    [Fact]
    public void DisabledAndInvalidRulesDoNotSuppressLines()
    {
        Assert.False(ConsoleLogFilterRuleRules.Matches(
            new ConsoleLogFilterRule { Enabled = false, Pattern = "error" },
            "ERROR: failed"));
        Assert.False(ConsoleLogFilterRuleRules.Matches(
            new ConsoleLogFilterRule { Mode = ConsoleLogFilterMode.Regex, Pattern = "[" },
            "ERROR: failed"));
    }

    [Fact]
    public void NormalizeManyTrimsAndDropsEmptyRules()
    {
        var normalized = ConsoleLogFilterRuleRules.NormalizeMany(
        [
            new ConsoleLogFilterRule { Pattern = "  warning  " },
            new ConsoleLogFilterRule { Pattern = " " }
        ]);

        var rule = Assert.Single(normalized);
        Assert.Equal("warning", rule.Pattern);
    }
}
