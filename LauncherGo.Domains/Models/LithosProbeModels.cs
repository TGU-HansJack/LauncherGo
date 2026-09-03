namespace LauncherGo.Domains.Models;

/// <summary>
/// A release published by the official Lithos Probe ModDB entry.
/// </summary>
public sealed class LithosProbeRelease
{
    public string Version { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public DateTimeOffset? CreatedAtUtc { get; init; }

    public IReadOnlyList<string> SupportedGameVersions { get; init; } = [];

    public bool SupportsGameVersion(string? gameVersion) =>
        !string.IsNullOrWhiteSpace(gameVersion) &&
        SupportedGameVersions.Contains(gameVersion.Trim(), StringComparer.OrdinalIgnoreCase);

    public override string ToString()
    {
        var tags = SupportedGameVersions.Count == 0
            ? "-"
            : string.Join(", ", SupportedGameVersions);
        return $"{Version} ({tags})";
    }
}

public sealed class LithosProbeProfileSnapshot
{
    public InstanceProfile Profile { get; init; } = new();

    public ModEntry? InstalledMod { get; init; }

    public LithosProbeRelease? ExactCompatibleRelease { get; init; }

    public IReadOnlyList<LithosProbeReportFile> Reports { get; init; } = [];

    public LithosProbeReportFile? LatestReport => Reports.FirstOrDefault(static report => report.Report is not null);
}

public sealed class LithosProbeReportFile
{
    public string FilePath { get; init; } = string.Empty;

    public string FileName => Path.GetFileName(FilePath);

    public DateTimeOffset SortTimeUtc { get; init; }

    public LithosProbeReport? Report { get; init; }

    public string Error { get; init; } = string.Empty;

    public bool IsValid => Report is not null;

    public override string ToString() => Report is null
        ? $"{FileName} ({Error})"
        : Report.GeneratedAtUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
}

public sealed class LithosProbeReport
{
    public const int SupportedSchema = 2;

    public int Schema { get; init; }

    public string Kind { get; init; } = string.Empty;

    public DateTimeOffset GeneratedAtUtc { get; init; }

    public LithosProbeServerInfo Server { get; init; } = new();

    public IReadOnlyList<LithosProbeModInfo> Mods { get; init; } = [];

    public IReadOnlyList<LithosProbeHealthWindow> Windows { get; init; } = [];

    public LithosProbeCensus Census { get; init; } = new();

    public IReadOnlyList<LithosProbeSeries> SeriesTiers { get; init; } = [];

    public LithosProbeSeries? HighestPrecisionSeries { get; init; }

    public LithosProbeProfile? Profile { get; init; }
}

public sealed class LithosProbeServerInfo
{
    public string LithosVersion { get; init; } = string.Empty;
    public string GameVersion { get; init; } = string.Empty;
    public string Runtime { get; init; } = string.Empty;
    public string OperatingSystem { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public int ProcessorCount { get; init; }
    public bool ServerGc { get; init; }
    public long UptimeSeconds { get; init; }
    public long TotalTicks { get; init; }
    public double TickTimeMs { get; init; }
    public int MaxClients { get; init; }
}

public sealed class LithosProbeModInfo
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Side { get; init; } = string.Empty;
}

public sealed class LithosProbeHealthWindow
{
    public string Name { get; init; } = string.Empty;
    public double Seconds { get; init; }
    public double CoveredSeconds { get; init; }
    public int Ticks { get; init; }
    public double Tps { get; init; }
    public double MeanMs { get; init; }
    public double MedianMs { get; init; }
    public double P95Ms { get; init; }
    public double P99Ms { get; init; }
    public double MaxMs { get; init; }
}

public sealed class LithosProbeCensus
{
    public int Players { get; init; }
    public int LoadedChunks { get; init; }
    public int LoadedEntities { get; init; }
}

public sealed class LithosProbeSeries
{
    public int SpanSeconds { get; init; }
    public int Count { get; init; }
    public IReadOnlyList<string> Fields { get; init; } = [];
    public IReadOnlyList<long> Times { get; init; } = [];
    public IReadOnlyDictionary<string, IReadOnlyList<double>> Values { get; init; } =
        new Dictionary<string, IReadOnlyList<double>>(StringComparer.OrdinalIgnoreCase);
}

public sealed class LithosProbeProfile
{
    public double DurationSeconds { get; init; }
    public int TotalSamples { get; init; }
    public int ManagedSamples { get; init; }
    public int IntervalMs { get; init; }
    public IReadOnlyList<LithosProbeThread> Threads { get; init; } = [];
    public IReadOnlyList<LithosProbeHotspot> ModHotspots { get; init; } = [];
    public IReadOnlyList<LithosProbeHotspot> ModuleHotspots { get; init; } = [];
}

public sealed class LithosProbeThread
{
    public string Name { get; init; } = string.Empty;
    public int Samples { get; init; }
    public int ManagedSamples { get; init; }
    public int ParkedSamples { get; init; }
    public IReadOnlyList<LithosProbeCallNode> Children { get; init; } = [];
}

public sealed class LithosProbeCallNode
{
    public string Name { get; init; } = string.Empty;
    public string FullName { get; init; } = string.Empty;
    public string Module { get; init; } = string.Empty;
    public string Mod { get; init; } = string.Empty;
    public int TotalSamples { get; init; }
    public int SelfSamples { get; init; }
    public int SelfManagedSamples { get; init; }
    public IReadOnlyList<LithosProbeCallNode> Children { get; init; } = [];
}

public sealed class LithosProbeHotspot
{
    public string Name { get; init; } = string.Empty;
    public int SelfSamples { get; init; }
}
