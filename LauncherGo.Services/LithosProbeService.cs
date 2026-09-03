using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
/// Keeps the Probe integration local: releases are read from the official ModDB API and reports are read from the profile log directory.
/// </summary>
public sealed class LithosProbeService : ILithosProbeService
{
    private const string ModDbApiUrl = "https://mods.vintagestory.at/api/mod/lithosprobe";
    private readonly IInstanceModService _instanceModService;
    private readonly HttpClient _httpClient;

    public LithosProbeService(IInstanceModService instanceModService)
        : this(instanceModService, CreateHttpClient())
    {
    }

    internal LithosProbeService(IInstanceModService instanceModService, HttpClient httpClient)
    {
        _instanceModService = instanceModService;
        _httpClient = httpClient;
    }

    public async Task<IReadOnlyList<LithosProbeRelease>> GetReleasesAsync(
        CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ModDbApiUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (!HasSuccessStatus(root) || !root.TryGetProperty("mod", out var mod) || mod.ValueKind != JsonValueKind.Object ||
            !mod.TryGetProperty("releases", out var releaseArray) || releaseArray.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Lithos Probe 官方 ModDB 未返回有效发布数据。");
        }

        return releaseArray.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(ParseRelease)
            .Where(static release => !string.IsNullOrWhiteSpace(release.Version) && !string.IsNullOrWhiteSpace(release.DownloadUrl))
            .OrderByDescending(static release => release.CreatedAtUtc ?? DateTimeOffset.MinValue)
            .ToList();
    }

    public async Task<LithosProbeProfileSnapshot> GetProfileSnapshotAsync(
        InstanceProfile profile,
        IReadOnlyList<LithosProbeRelease> releases,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var installedModsTask = _instanceModService.GetModsAsync(profile, cancellationToken);
        var reportsTask = GetReportsAsync(profile, cancellationToken);
        await Task.WhenAll(installedModsTask, reportsTask);

        var installed = installedModsTask.Result.FirstOrDefault(static mod =>
            mod.ModId.Equals("lithosprobe", StringComparison.OrdinalIgnoreCase));
        var exactRelease = releases.FirstOrDefault(release => release.SupportsGameVersion(profile.Version));
        return new LithosProbeProfileSnapshot
        {
            Profile = profile,
            InstalledMod = installed,
            ExactCompatibleRelease = exactRelease,
            Reports = reportsTask.Result
        };
    }

    public Task<IReadOnlyList<LithosProbeReportFile>> GetReportsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        cancellationToken.ThrowIfCancellationRequested();
        var logsPath = WorkspacePathHelper.GetProfileLogsPath(profile.DirectoryPath);
        if (!Directory.Exists(logsPath))
            return Task.FromResult<IReadOnlyList<LithosProbeReportFile>>([]);

        List<string> paths;
        try
        {
            paths = Directory.EnumerateFiles(logsPath, "lithosprobe-*.json.gz", SearchOption.TopDirectoryOnly).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Task.FromResult<IReadOnlyList<LithosProbeReportFile>>(
            [new LithosProbeReportFile
            {
                FilePath = logsPath,
                Error = "无法扫描 Probe 报告目录：" + ex.Message
            }]);
        }

        var reports = new List<LithosProbeReportFile>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            reports.Add(ReadReportFile(path));
        }

        return Task.FromResult<IReadOnlyList<LithosProbeReportFile>>(reports
            .OrderByDescending(static item => item.Report?.GeneratedAtUtc ?? item.SortTimeUtc)
            .ToList());
    }

    internal static LithosProbeReportFile ReadReportFile(string path)
    {
        DateTimeOffset lastWrite;
        try
        {
            lastWrite = File.GetLastWriteTimeUtc(path);
        }
        catch
        {
            lastWrite = DateTimeOffset.MinValue;
        }

        try
        {
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            using var document = JsonDocument.Parse(gzip);
            return new LithosProbeReportFile
            {
                FilePath = path,
                SortTimeUtc = lastWrite,
                Report = ParseReport(document.RootElement)
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or JsonException or FormatException or InvalidOperationException)
        {
            return new LithosProbeReportFile
            {
                FilePath = path,
                SortTimeUtc = lastWrite,
                Error = DescribeReportError(ex)
            };
        }
    }

    internal static LithosProbeReport ParseReport(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("报告根节点不是对象。");
        var schema = GetInt(root, "schema");
        if (schema != LithosProbeReport.SupportedSchema)
            throw new InvalidDataException($"不支持的 Probe 报告 schema（仅支持 {LithosProbeReport.SupportedSchema}）。");
        var kind = GetString(root, "kind");
        if (!kind.Equals("lithos-probe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("文件不是 Lithos Probe 报告。");

        var generatedAt = ParseRequiredTimestamp(GetString(root, "generatedAt"));
        var seriesTiers = root.TryGetProperty("series", out var series) ? ParseSeriesTiers(series) : [];
        return new LithosProbeReport
        {
            Schema = schema,
            Kind = kind,
            GeneratedAtUtc = generatedAt,
            Server = root.TryGetProperty("server", out var server) && server.ValueKind == JsonValueKind.Object
                ? ParseServer(server)
                : new LithosProbeServerInfo(),
            Mods = root.TryGetProperty("mods", out var mods) ? ParseMods(mods) : [],
            Windows = root.TryGetProperty("windows", out var windows) ? ParseWindows(windows) : [],
            Census = root.TryGetProperty("census", out var census) && census.ValueKind == JsonValueKind.Object
                ? new LithosProbeCensus
                {
                    Players = GetInt(census, "players"),
                    LoadedChunks = GetInt(census, "loadedChunks"),
                    LoadedEntities = GetInt(census, "loadedEntities")
                }
                : new LithosProbeCensus(),
            SeriesTiers = seriesTiers,
            HighestPrecisionSeries = seriesTiers.FirstOrDefault(),
            Profile = root.TryGetProperty("profile", out var profile) && profile.ValueKind == JsonValueKind.Object
                ? ParseProfile(profile)
                : null
        };
    }

    private static LithosProbeRelease ParseRelease(JsonElement release) => new()
    {
        Version = GetString(release, "modversion"),
        DownloadUrl = GetString(release, "mainfile"),
        CreatedAtUtc = ParseOptionalTimestamp(GetString(release, "created")),
        SupportedGameVersions = release.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array
            ? tags.EnumerateArray()
                .Where(static tag => tag.ValueKind == JsonValueKind.String)
                .Select(static tag => tag.GetString()?.Trim() ?? string.Empty)
                .Where(static tag => tag.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : []
    };

    private static LithosProbeServerInfo ParseServer(JsonElement server) => new()
    {
        LithosVersion = GetString(server, "lithosVersion"),
        GameVersion = GetString(server, "gameVersion"),
        Runtime = GetString(server, "runtime"),
        OperatingSystem = GetString(server, "os"),
        Architecture = GetString(server, "architecture"),
        ProcessorCount = GetInt(server, "processorCount"),
        ServerGc = GetBool(server, "serverGc"),
        UptimeSeconds = GetLong(server, "uptimeSeconds"),
        TotalTicks = GetLong(server, "totalTicks"),
        TickTimeMs = GetDouble(server, "tickTimeMs"),
        MaxClients = GetInt(server, "maxClients")
    };

    private static IReadOnlyList<LithosProbeModInfo> ParseMods(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new LithosProbeModInfo
            {
                Id = GetString(item, "id"),
                Name = GetString(item, "name"),
                Version = GetString(item, "version"),
                Side = GetString(item, "side")
            })
            .ToList();
    }

    private static IReadOnlyList<LithosProbeHealthWindow> ParseWindows(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new LithosProbeHealthWindow
            {
                Name = GetString(item, "name"),
                Seconds = GetDouble(item, "seconds"),
                CoveredSeconds = GetDouble(item, "coveredSeconds"),
                Ticks = GetInt(item, "ticks"),
                Tps = GetDouble(item, "tps"),
                MeanMs = GetDouble(item, "meanMs"),
                MedianMs = GetDouble(item, "medianMs"),
                P95Ms = GetDouble(item, "p95Ms"),
                P99Ms = GetDouble(item, "p99Ms"),
                MaxMs = GetDouble(item, "maxMs")
            })
            .ToList();
    }

    private static IReadOnlyList<LithosProbeSeries> ParseSeriesTiers(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("tiers", out var tiers) || tiers.ValueKind != JsonValueKind.Array)
            return [];

        var fields = element.TryGetProperty("fields", out var declaredFields) && declaredFields.ValueKind == JsonValueKind.Array
            ? declaredFields.EnumerateArray()
                .Where(static field => field.ValueKind == JsonValueKind.String)
                .Select(static field => field.GetString() ?? string.Empty)
                .Where(static field => field.Length > 0)
                .ToList()
            : [];

        return tiers.EnumerateArray()
            .Where(static tier => tier.ValueKind == JsonValueKind.Object)
            .Select(tier => ParseSeriesTier(tier, fields))
            .Where(static tier => tier is not null)
            .Cast<LithosProbeSeries>()
            .OrderBy(static tier => tier.SpanSeconds)
            .ToList();
    }

    private static LithosProbeSeries? ParseSeriesTier(JsonElement tier, IReadOnlyList<string> fields)
    {
        if (!tier.TryGetProperty("times", out var times) || times.ValueKind != JsonValueKind.Array ||
            !tier.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var parsedTimes = times.EnumerateArray()
            .Select(static value => value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var time) ? time : 0L)
            .ToList();
        var parsedValues = new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in values.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Array) continue;
            parsedValues[property.Name] = property.Value.EnumerateArray()
                .Select(static value => value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? number : double.NaN)
                .ToList();
        }

        return new LithosProbeSeries
        {
            SpanSeconds = GetInt(tier, "spanSeconds"),
            Count = GetInt(tier, "count"),
            Fields = fields,
            Times = parsedTimes,
            Values = parsedValues
        };
    }

    private static LithosProbeProfile ParseProfile(JsonElement profile)
    {
        return new LithosProbeProfile
        {
            DurationSeconds = GetDouble(profile, "durationSeconds"),
            TotalSamples = GetInt(profile, "totalSamples"),
            ManagedSamples = GetInt(profile, "managedSamples"),
            IntervalMs = GetInt(profile, "intervalMs"),
            Threads = profile.TryGetProperty("threads", out var threads) ? ParseThreads(threads) : [],
            ModHotspots = profile.TryGetProperty("mods", out var mods) ? ParseHotspots(mods, "id") : [],
            ModuleHotspots = profile.TryGetProperty("modules", out var modules) ? ParseHotspots(modules, "name") : []
        };
    }

    private static IReadOnlyList<LithosProbeThread> ParseThreads(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new LithosProbeThread
            {
                Name = GetString(item, "name"),
                Samples = GetInt(item, "samples"),
                ManagedSamples = GetInt(item, "managed"),
                ParkedSamples = GetInt(item, "parked"),
                Children = item.TryGetProperty("children", out var children) ? ParseCallNodes(children) : []
            })
            .OrderByDescending(static thread => thread.Samples)
            .ToList();
    }

    private static IReadOnlyList<LithosProbeCallNode> ParseCallNodes(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(static item => new LithosProbeCallNode
            {
                Name = GetString(item, "name"),
                FullName = GetString(item, "full"),
                Module = GetString(item, "module"),
                Mod = GetString(item, "mod"),
                TotalSamples = GetInt(item, "total"),
                SelfSamples = GetInt(item, "self"),
                SelfManagedSamples = GetInt(item, "selfManaged"),
                Children = item.TryGetProperty("children", out var children) ? ParseCallNodes(children) : []
            })
            .OrderByDescending(static node => node.TotalSamples)
            .ToList();
    }

    private static IReadOnlyList<LithosProbeHotspot> ParseHotspots(JsonElement element, string nameProperty)
    {
        if (element.ValueKind != JsonValueKind.Array) return [];
        return element.EnumerateArray()
            .Where(static item => item.ValueKind == JsonValueKind.Object)
            .Select(item => new LithosProbeHotspot { Name = GetString(item, nameProperty), SelfSamples = GetInt(item, "self") })
            .OrderByDescending(static hotspot => hotspot.SelfSamples)
            .ToList();
    }

    private static bool HasSuccessStatus(JsonElement root) => root.TryGetProperty("statuscode", out var status) &&
        ((status.ValueKind == JsonValueKind.Number && status.TryGetInt32(out var code) && code == 200) ||
         (status.ValueKind == JsonValueKind.String && status.GetString()?.Trim() == "200"));

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim() ?? string.Empty
            : string.Empty;

    private static int GetInt(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var result) ? result : 0;

    private static long GetLong(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt64(out var result) ? result : 0;

    private static double GetDouble(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var result) ? result : 0;

    private static bool GetBool(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static DateTimeOffset ParseRequiredTimestamp(string value) => ParseOptionalTimestamp(value) ??
        throw new InvalidDataException("报告缺少有效生成时间。");

    private static DateTimeOffset? ParseOptionalTimestamp(string value) => DateTimeOffset.TryParse(
        value,
        CultureInfo.InvariantCulture,
        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
        out var result) ? result : null;

    private static string DescribeReportError(Exception exception) => exception switch
    {
        InvalidDataException => exception.Message,
        JsonException => "报告 JSON 无效或尚未写入完成。",
        IOException => "无法读取报告文件，可能仍在写入。",
        _ => "报告无法解析。"
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }
}
