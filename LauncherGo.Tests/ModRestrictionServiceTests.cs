using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModRestrictionServiceTests
{
    [Fact]
    public async Task LoadAsync_DefaultsToInstalledEnabledModsAndForcedWhitelist()
    {
        using var fixture = new RestrictionProfileFixture();
        fixture.AddMod("allowedmod", "1.2.3");
        fixture.AddMod("disabledmod", "2.0.0");
        fixture.SetDisabledMods("disabledmod@2.0.0");

        var settings = await fixture.Service.LoadAsync(fixture.Profile);

        Assert.True(settings.ForceWhitelistEnabled);
        Assert.False(settings.BlacklistEnabled);
        Assert.Equal(["allowedmod"], settings.WhitelistModIds);
        Assert.Empty(settings.BlacklistModIds);
    }

    [Fact]
    public async Task SaveAsync_SynchronizesNativePolicyAndDeploysUniversalMod()
    {
        using var fixture = new RestrictionProfileFixture();
        fixture.SetDisabledMods("launchergorestriction@1.0.0", "anothermod@1.0.0");

        await fixture.Service.SaveAsync(fixture.Profile, new ModRestrictionSettings
        {
            BlacklistEnabled = true,
            ForceWhitelistEnabled = true,
            WhitelistModIds = ["ServerMod", "AllowedClient"],
            BlacklistModIds = ["BlockedClient"]
        });

        var managed = JsonNode.Parse(await File.ReadAllTextAsync(fixture.Service.GetSettingsPath(fixture.Profile)))!.AsObject();
        Assert.True(managed["BlacklistEnabled"]!.GetValue<bool>());
        Assert.True(managed["ForceWhitelistEnabled"]!.GetValue<bool>());
        Assert.Equal("allowedclient", managed["WhitelistModIds"]![0]!.GetValue<string>());
        Assert.Equal("servermod", managed["WhitelistModIds"]![1]!.GetValue<string>());
        Assert.Equal("blockedclient", managed["BlacklistModIds"]![0]!.GetValue<string>());

        var server = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ServerConfigPath))!.AsObject();
        Assert.Equal(
            ["allowedclient", "launchergorestriction", "servermod"],
            server["ModIdWhiteList"]!.AsArray().Select(static item => item!.GetValue<string>()).ToArray());
        Assert.Equal(
            ["blockedclient"],
            server["ModIdBlackList"]!.AsArray().Select(static item => item!.GetValue<string>()).ToArray());
        Assert.Equal(
            ["anothermod@1.0.0"],
            server["WorldConfig"]!["DisabledMods"]!.AsArray()
                .Select(static item => item!.GetValue<string>()).ToArray());

        var deployedRoot = Path.Combine(fixture.RootPath, "Mods", "launchergorestriction");
        Assert.True(File.Exists(Path.Combine(deployedRoot, "launchergorestriction.dll")));
        Assert.True(File.Exists(Path.Combine(deployedRoot, "modinfo.json")));
    }

    [Fact]
    public async Task SaveAsync_RejectsConflictingEnabledLists()
    {
        using var fixture = new RestrictionProfileFixture();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            fixture.Service.SaveAsync(fixture.Profile, new ModRestrictionSettings
            {
                BlacklistEnabled = true,
                ForceWhitelistEnabled = true,
                WhitelistModIds = ["SameMod"],
                BlacklistModIds = ["samemod"]
            }));

        Assert.Contains("samemod", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.Service.GetSettingsPath(fixture.Profile)));
    }

    [Fact]
    public async Task SaveAsync_KeepsRestrictionModInAnEmptyForcedWhitelist()
    {
        using var fixture = new RestrictionProfileFixture();

        await fixture.Service.SaveAsync(fixture.Profile, new ModRestrictionSettings
        {
            ForceWhitelistEnabled = true
        });

        var server = JsonNode.Parse(await File.ReadAllTextAsync(fixture.ServerConfigPath))!.AsObject();
        Assert.Equal(
            ["launchergorestriction"],
            server["ModIdWhiteList"]!.AsArray().Select(static item => item!.GetValue<string>()).ToArray());
    }

    private sealed class RestrictionProfileFixture : IDisposable
    {
        public RestrictionProfileFixture()
        {
            RootPath = Path.Combine(Path.GetTempPath(), "LauncherGo.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(RootPath, "Mods"));
            Directory.CreateDirectory(Path.Combine(RootPath, "ModConfig"));
            ServerConfigPath = Path.Combine(RootPath, "serverconfig.json");
            File.WriteAllText(ServerConfigPath, "{\"WorldConfig\":{\"DisabledMods\":[]}}");

            Profile = new InstanceProfile
            {
                Id = "restriction-test",
                Name = "Restriction Test",
                DirectoryPath = RootPath,
                SaveDirectory = Path.Combine(RootPath, "Saves"),
                ActiveSaveFile = Path.Combine(RootPath, "Saves", "default.vcdbs")
            };

            var serverConfigService = new InstanceServerConfigService();
            var modService = new InstanceModService(serverConfigService);
            Service = new ModRestrictionService(serverConfigService, modService);
        }

        public string RootPath { get; }

        public string ServerConfigPath { get; }

        public InstanceProfile Profile { get; }

        public ModRestrictionService Service { get; }

        public void AddMod(string modId, string version)
        {
            var modPath = Path.Combine(RootPath, "Mods", modId);
            Directory.CreateDirectory(modPath);
            File.WriteAllText(
                Path.Combine(modPath, "modinfo.json"),
                $$"""
                {
                  "type": "code",
                  "modid": "{{modId}}",
                  "name": "{{modId}}",
                  "version": "{{version}}"
                }
                """);
        }

        public void SetDisabledMods(params string[] values)
        {
            var root = JsonNode.Parse(File.ReadAllText(ServerConfigPath))!.AsObject();
            var disabled = new JsonArray();
            foreach (var value in values)
            {
                disabled.Add(value);
            }

            root["WorldConfig"]!["DisabledMods"] = disabled;
            File.WriteAllText(ServerConfigPath, root.ToJsonString());
        }

        public void Dispose()
        {
            if (Directory.Exists(RootPath))
            {
                Directory.Delete(RootPath, true);
            }
        }
    }
}
