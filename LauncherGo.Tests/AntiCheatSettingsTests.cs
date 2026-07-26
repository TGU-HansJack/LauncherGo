using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AntiCheatSettingsTests
{
    [Fact]
    public void Defaults_AreMonitorOnlyAndDoNotPunishPlayers()
    {
        var settings = ServerAntiCheatService.NormalizeSettings(new AntiCheatSettings());

        Assert.False(settings.Enabled);
        Assert.True(settings.MonitorOnly);
        Assert.False(settings.Actions.KickEnabled);
        Assert.False(settings.Actions.BanEnabled);
        Assert.False(settings.Actions.PunishStatisticalDetections);
    }

    [Fact]
    public void Normalize_PreservesDisabledWhitelistRuleForLaterReenable()
    {
        var settings = ServerAntiCheatService.NormalizeSettings(new AntiCheatSettings
        {
            Whitelist =
            [
                new AntiCheatWhitelistRule
                {
                    Enabled = false,
                    Id = " carry-on ",
                    PlayerName = "Player",
                    Detectors = [" movement.* "]
                }
            ]
        });

        var rule = Assert.Single(settings.Whitelist);
        Assert.False(rule.Enabled);
        Assert.Equal("carry-on", rule.Id);
        Assert.Equal(["movement.*"], rule.Detectors);
    }

    [Fact]
    public void Normalize_DoesNotTurnEmptyDetectorListIntoWildcard()
    {
        var settings = ServerAntiCheatService.NormalizeSettings(new AntiCheatSettings
        {
            Whitelist =
            [
                new AntiCheatWhitelistRule
                {
                    PlayerName = "Player",
                    Detectors = [" "]
                }
            ]
        });

        Assert.Empty(Assert.Single(settings.Whitelist).Detectors);
    }

    [Fact]
    public async Task LoadSettings_InvalidJsonPreservesWhitelistFile()
    {
        var profileRoot = Path.Combine(
            Path.GetTempPath(),
            "LauncherGo.Tests",
            Guid.NewGuid().ToString("N"));
        var configPath = Path.Combine(profileRoot, "ModConfig", "launchergoanticheat.json");
        const string invalidJson = "{ \"Enabled\": true, \"Whitelist\": [";
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
        await File.WriteAllTextAsync(configPath, invalidJson);

        try
        {
            var service = new ServerAntiCheatService(new InstanceServerConfigService());
            var profile = new InstanceProfile
            {
                Id = "profile-a",
                Name = "Survival",
                DirectoryPath = profileRoot
            };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.LoadSettingsAsync(profile));

            Assert.Contains("原文件已保留", exception.Message);
            Assert.Equal(invalidJson, await File.ReadAllTextAsync(configPath));
        }
        finally
        {
            if (Directory.Exists(profileRoot))
                Directory.Delete(profileRoot, recursive: true);
        }
    }
}
