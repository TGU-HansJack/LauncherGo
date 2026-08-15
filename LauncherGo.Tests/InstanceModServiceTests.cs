using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class InstanceModServiceTests
{
    [Fact]
    public async Task GetModsAsync_ReadsPascalCaseModMetadataFields()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-metadata-");
        try
        {
            var modDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "Mods", "clayworks"));
            await File.WriteAllTextAsync(
                Path.Combine(modDirectory.FullName, "modinfo.json"),
                """
                {
                  "Type": "content",
                  "TextureSize": 32,
                  "Name": "Clayworks",
                  "Version": "0.6.0",
                  "NetworkVersion": null,
                  "ModID": "clayworks",
                  "Dependencies": [
                    { "ModID": "game", "Version": "1.20.0" }
                  ]
                }
                """);

            var service = new InstanceModService(new StubServerConfigService());
            var mods = await service.GetModsAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            var mod = Assert.Single(mods);
            Assert.Equal("clayworks", mod.ModId);
            Assert.Equal("0.6.0", mod.Version);
            var dependency = Assert.Single(mod.Dependencies);
            Assert.Equal("game", dependency.ModId);
            Assert.Equal("1.20.0", dependency.Version);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private sealed class StubServerConfigService : IInstanceServerConfigService
    {
        public Task<ServerCommonSettings> LoadServerSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldSettings> LoadWorldSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            InstanceProfile profile,
            ServerCommonSettings serverSettings,
            WorldSettings worldSettings,
            IReadOnlyList<WorldRuleValue> rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadRawJsonAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult("{}");

        public Task SaveRawJsonAsync(InstanceProfile profile, string json, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportRawJsonAsync(InstanceProfile profile, string jsonFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
