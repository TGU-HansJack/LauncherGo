using System.IO.Compression;
using System.Net;
using System.Text;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class LithosProbeServiceTests
{
    [Fact]
    public async Task GetReleasesAsync_ReadsOfficialReleasesAndMatchesExactGameVersion()
    {
        using var client = new HttpClient(new StaticResponseHandler("""
            {
              "statuscode":"200",
              "mod":{"releases":[
                {"modversion":"1.0.2","mainfile":"https://example.test/probe.zip","created":"2026-09-01 12:00:00","tags":["1.22.7","1.22.6"]},
                {"modversion":"1.0.1","mainfile":"https://example.test/old.zip","created":"2026-08-31 12:00:00","tags":["1.22.5"]}
              ]}
            }
            """));
        var service = new LithosProbeService(new StubModService(), client);

        var releases = await service.GetReleasesAsync();

        var newest = releases[0];
        Assert.Equal("1.0.2", newest.Version);
        Assert.True(newest.SupportsGameVersion("1.22.7"));
        Assert.False(newest.SupportsGameVersion("1.22.4"));
    }

    [Fact]
    public async Task GetReleasesAsync_RejectsMissingReleaseArray()
    {
        using var client = new HttpClient(new StaticResponseHandler("""{"statuscode":200,"mod":{}}"""));
        var service = new LithosProbeService(new StubModService(), client);

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.GetReleasesAsync());
    }

    [Fact]
    public async Task GetReportsAsync_ParsesValidSchemaAndSortsNewestFirst()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-report-");
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(directory.FullName, "Logs"));
            var oldest = Path.Combine(logs.FullName, "lithosprobe-older.json.gz");
            var newest = Path.Combine(logs.FullName, "lithosprobe-newer.json.gz");
            await WriteGzipJsonAsync(oldest, CreateReportJson("2026-09-01T00:00:00Z", includeProfile: false));
            await WriteGzipJsonAsync(newest, CreateReportJson("2026-09-02T00:00:00Z", includeProfile: true));

            var service = new LithosProbeService(new StubModService(), new HttpClient(new StaticResponseHandler("{}")));
            var reports = await service.GetReportsAsync(new InstanceProfile { DirectoryPath = directory.FullName });

            var report = Assert.IsType<LithosProbeReport>(reports[0].Report);
            Assert.Equal("lithosprobe-newer.json.gz", reports[0].FileName);
            Assert.Equal(2, report.Windows.Count);
            Assert.NotNull(report.HighestPrecisionSeries);
            Assert.Equal(2, report.SeriesTiers.Count);
            Assert.Equal(1, report.SeriesTiers[0].SpanSeconds);
            Assert.Equal(3, report.SeriesTiers[0].Count);
            Assert.Contains("managedHeapMb", report.SeriesTiers[0].Fields);
            Assert.Equal(1, report.Profile?.Threads.Count);
            Assert.Equal("testmod", Assert.Single(report.Profile!.ModHotspots).Name);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Theory]
    [InlineData("not gzip")]
    [InlineData("{\"schema\":2,\"kind\":\"other\",\"generatedAt\":\"2026-09-01T00:00:00Z\"}")]
    public async Task GetReportsAsync_ReturnsReadableErrorsWithoutDiscardingFile(string content)
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-invalid-");
        try
        {
            var logs = Directory.CreateDirectory(Path.Combine(directory.FullName, "Logs"));
            var path = Path.Combine(logs.FullName, "lithosprobe-invalid.json.gz");
            if (content == "not gzip")
            {
                await File.WriteAllTextAsync(path, content);
            }
            else
            {
                await WriteGzipJsonAsync(path, content);
            }

            var service = new LithosProbeService(new StubModService(), new HttpClient(new StaticResponseHandler("{}")));
            var report = Assert.Single(await service.GetReportsAsync(new InstanceProfile { DirectoryPath = directory.FullName }));

            Assert.False(report.IsValid);
            Assert.False(string.IsNullOrWhiteSpace(report.Error));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task GetProfileSnapshotAsync_SelectsExactCompatibleRelease()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-probe-config-");
        try
        {
            var profile = new InstanceProfile { DirectoryPath = directory.FullName, Version = "1.22.7" };
            var service = new LithosProbeService(new StubModService(), new HttpClient(new StaticResponseHandler("{}")));

            var snapshot = await service.GetProfileSnapshotAsync(profile,
            [new LithosProbeRelease { Version = "1.0.2", DownloadUrl = "https://example.test/probe.zip", SupportedGameVersions = ["1.22.7"] }]);

            Assert.Equal("1.0.2", snapshot.ExactCompatibleRelease?.Version);
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    private static async Task WriteGzipJsonAsync(string path, string json)
    {
        await using var stream = File.Create(path);
        await using var gzip = new GZipStream(stream, CompressionLevel.Optimal);
        await using var writer = new StreamWriter(gzip, new UTF8Encoding(false));
        await writer.WriteAsync(json);
    }

    private static string CreateReportJson(string generatedAt, bool includeProfile) => $$"""
        {
          "schema": 2,
          "kind": "lithos-probe",
          "generatedAt": "{{generatedAt}}",
          "server": { "gameVersion":"1.22.7", "lithosVersion":"Probe 1.0.2", "runtime":".NET", "os":"Windows", "tickTimeMs":33.333, "processorCount":8 },
          "windows": [
            { "name":"5s", "seconds":5, "coveredSeconds":5, "ticks":150, "tps":30, "meanMs":30, "p95Ms":35, "p99Ms":40, "maxMs":42 },
            { "name":"1m", "seconds":60, "coveredSeconds":60, "ticks":1800, "tps":30, "meanMs":31, "p95Ms":36, "p99Ms":41, "maxMs":44 }
          ],
          "census": { "players":2, "loadedChunks":123, "loadedEntities":456 },
          "series": { "fields":["tps","msptMean","managedHeapMb"], "tiers":[
            { "spanSeconds":10, "count":2, "times":[1,2], "values":{"tps":[30,29],"msptMean":[30,34],"cpuPercent":[12,18],"workingSetMb":[512,520]} },
            { "spanSeconds":1, "count":3, "times":[1,2,3], "values":{"tps":[30,29,28],"msptMean":[30,34,36],"cpuPercent":[12,18,19],"workingSetMb":[512,520,522]} }
          ] }{{(includeProfile ? ",\"profile\":{\"durationSeconds\":10,\"totalSamples\":10,\"managedSamples\":8,\"intervalMs\":1,\"mods\":[{\"id\":\"testmod\",\"self\":3}],\"modules\":[{\"name\":\"test.dll\",\"self\":2}],\"threads\":[{\"name\":\"main\",\"samples\":10,\"managed\":8,\"parked\":0,\"children\":[{\"name\":\"Tick\",\"full\":\"Tick.Full\",\"module\":\"test.dll\",\"mod\":\"testmod\",\"total\":10,\"self\":3,\"selfManaged\":3,\"children\":[]}]}]}" : string.Empty)}}
        }
        """;

    private sealed class StubModService : IInstanceModService
    {
        public Task<IReadOnlyList<ModEntry>> GetModsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<ModEntry>>([]);

        public Task<ModEntry> ImportModZipAsync(InstanceProfile profile, string zipPath, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModEntry> UpdateModAsync(InstanceProfile profile, ModEntry installedMod, string downloadUrl, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<ModEntry> DownloadAndInstallOfficialModAsync(InstanceProfile profile, string downloadUrl, string expectedModId, string expectedVersion, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<ModEntry>> ImportModsAsync(InstanceProfile profile, IReadOnlyCollection<string> sourcePaths, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task SetModEnabledAsync(InstanceProfile profile, string modId, string version, bool enabled, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public Task<int> DeleteModsAsync(InstanceProfile profile, IReadOnlyCollection<ModEntry> mods, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class StaticResponseHandler(string json) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
    }
}
