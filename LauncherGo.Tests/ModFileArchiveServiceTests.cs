using System.IO.Compression;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModFileArchiveServiceTests
{
    [Fact]
    public async Task Archive_ExcludesClientMods_AndIncludesServerAndUniversalMods()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-mod-archive-");
        try
        {
            var serverZip = Path.Combine(directory.FullName, "server.zip");
            await File.WriteAllTextAsync(serverZip, "server");
            var clientZip = Path.Combine(directory.FullName, "client.zip");
            await File.WriteAllTextAsync(clientZip, "client");
            var universalDirectory = Directory.CreateDirectory(Path.Combine(directory.FullName, "universal"));
            await File.WriteAllTextAsync(Path.Combine(universalDirectory.FullName, "modinfo.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(universalDirectory.FullName, "data.txt"), "universal");

            var mods = new ModEntry[]
            {
                CreateMod("server", "Server", serverZip),
                CreateMod("client", "Client", clientZip),
                CreateMod("universal", "Universal", universalDirectory.FullName)
            };
            await using var output = new MemoryStream();
            await new ModFileArchiveService().CreateServerModArchiveAsync(
                new InstanceProfile { DirectoryPath = directory.FullName },
                mods,
                output);

            output.Position = 0;
            using var archive = new ZipArchive(output, ZipArchiveMode.Read);
            var names = archive.Entries.Select(static entry => entry.FullName).ToList();
            Assert.Contains("Mods/server.zip", names);
            Assert.Contains("Mods/universal/modinfo.json", names);
            Assert.Contains("Mods/universal/data.txt", names);
            Assert.DoesNotContain(names, name => name.Contains("client", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static ModEntry CreateMod(string id, string side, string filePath) => new()
    {
        Name = id,
        ModId = id,
        Version = "1.0.0",
        Side = side,
        FilePath = filePath,
        Status = "OK"
    };
}
