using LauncherGo.Domains.Models;
using LauncherGo.Ui.Views;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AntiCheatWhitelistRuleItemTests
{
    [Fact]
    public void RuleItem_RoundTripsAllCompatibilityFields()
    {
        var expiresAt = new DateTimeOffset(2026, 8, 1, 12, 30, 0, TimeSpan.Zero);
        var source = new AntiCheatWhitelistRule
        {
            Enabled = false,
            Bypass = true,
            Id = "airship",
            PlayerUid = "uid-1",
            PlayerName = "Alex",
            Role = "suplayer",
            Groups = ["builders", "42"],
            Detectors = ["movement.speed", "movement.flight"],
            Contexts = ["mounted", "highping"],
            ExpiresAtUtc = expiresAt,
            SpeedMultiplier = 3.5,
            ActionRateMultiplier = 1.8,
            Reason = "approved airship",
            CreatedBy = "admin"
        };

        var result = LauncherMainWindow.AntiCheatWhitelistRuleItem.FromModel(source).ToModel();

        Assert.Equal(source.Enabled, result.Enabled);
        Assert.Equal(source.Bypass, result.Bypass);
        Assert.Equal(source.Id, result.Id);
        Assert.Equal(source.PlayerUid, result.PlayerUid);
        Assert.Equal(source.PlayerName, result.PlayerName);
        Assert.Equal(source.Role, result.Role);
        Assert.Equal(source.Groups, result.Groups);
        Assert.Equal(source.Detectors, result.Detectors);
        Assert.Equal(source.Contexts, result.Contexts);
        Assert.Equal(source.ExpiresAtUtc, result.ExpiresAtUtc);
        Assert.Equal(source.SpeedMultiplier, result.SpeedMultiplier);
        Assert.Equal(source.ActionRateMultiplier, result.ActionRateMultiplier);
        Assert.Equal(source.Reason, result.Reason);
        Assert.Equal(source.CreatedBy, result.CreatedBy);
    }

    [Fact]
    public void RuleItem_InvalidExpirationIsRejected()
    {
        var item = new LauncherMainWindow.AntiCheatWhitelistRuleItem
        {
            Id = "bad-expiry",
            ExpiresAtUtcText = "not-a-date"
        };

        Assert.Throws<FormatException>(item.ToModel);
    }
}
