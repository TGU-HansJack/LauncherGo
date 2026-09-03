using System.IO.Compression;
using System.Net;
using System.Text;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class InstanceModOfficialDeploymentTests
{
    [Fact]
    public async Task DownloadAndInstallOfficialModAsync_InstallsValidatedPackageAndEnablesIt()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-install-");
        try
        {
            var config = new MutableConfigService();
            using var client = new HttpClient(new BytesResponseHandler(CreateModZip("lithosprobe", "1.0.2")));
            var service = new InstanceModService(config, client);
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };

            var mod = await service.DownloadAndInstallOfficialModAsync(
                profile, "https://mods.example.test/lithosprobe.zip", "lithosprobe", "1.0.2");

            Assert.Equal("lithosprobe", mod.ModId);
            Assert.Equal("1.0.2", mod.Version);
            Assert.False(mod.IsDisabled);
            Assert.True(File.Exists(mod.FilePath));
            Assert.Contains("DisabledMods", config.RawJson, StringComparison.Ordinal);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndInstallOfficialModAsync_RejectsWrongIdWithoutInstalling()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-reject-");
        try
        {
            using var client = new HttpClient(new BytesResponseHandler(CreateModZip("othermod", "1.0.2")));
            var service = new InstanceModService(new MutableConfigService(), client);
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };

            var exception = await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallOfficialModAsync(
                profile, "https://mods.example.test/lithosprobe.zip", "lithosprobe", "1.0.2"));

            Assert.Contains("ID", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(directory.FullName, "Mods"), "*.zip"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task DownloadAndInstallOfficialModAsync_RestoresOldPackageWhenUpdateCannotSaveEnabledState()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-restore-");
        try
        {
            var config = new MutableConfigService();
            var profile = new InstanceProfile { DirectoryPath = directory.FullName };
            using (var firstClient = new HttpClient(new BytesResponseHandler(CreateModZip("lithosprobe", "1.0.0"))))
            {
                var firstService = new InstanceModService(config, firstClient);
                await firstService.DownloadAndInstallOfficialModAsync(
                    profile, "https://mods.example.test/lithosprobe-1.0.0.zip", "lithosprobe", "1.0.0");
            }

            config.FailSaves = true;
            using var updateClient = new HttpClient(new BytesResponseHandler(CreateModZip("lithosprobe", "1.0.2")));
            var service = new InstanceModService(config, updateClient);

            await Assert.ThrowsAsync<InvalidOperationException>(() => service.DownloadAndInstallOfficialModAsync(
                profile, "https://mods.example.test/lithosprobe-1.0.2.zip", "lithosprobe", "1.0.2"));

            config.FailSaves = false;
            var restored = Assert.Single(await service.GetModsAsync(profile));
            Assert.Equal("1.0.0", restored.Version);
            Assert.Empty(Directory.EnumerateFileSystemEntries(Path.Combine(directory.FullName, "Mods"), "*.launchergobak-*"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static byte[] CreateModZip(string modId, string version)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("modinfo.json");
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write($$"""{"modid":"{{modId}}","name":"Probe","version":"{{version}}","side":"server"}""");
        }

        return stream.ToArray();
    }

    private sealed class BytesResponseHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) });
    }

    private sealed class MutableConfigService : IInstanceServerConfigService
    {
        public string RawJson { get; private set; } = "{\"WorldConfig\":{\"DisabledMods\":[]}}";
        public bool FailSaves { get; set; }

        public Task<ServerCommonSettings> LoadServerSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<WorldSettings> LoadWorldSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SaveSettingsAsync(InstanceProfile profile, ServerCommonSettings serverSettings, WorldSettings worldSettings, IReadOnlyList<WorldRuleValue> rules, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<string> LoadRawJsonAsync(InstanceProfile profile, CancellationToken cancellationToken = default) => Task.FromResult(RawJson);
        public Task SaveRawJsonAsync(InstanceProfile profile, string json, CancellationToken cancellationToken = default)
        {
            if (FailSaves) throw new InvalidOperationException("configuration save failed");
            RawJson = json;
            return Task.CompletedTask;
        }

        public Task ImportRawJsonAsync(InstanceProfile profile, string jsonFilePath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
