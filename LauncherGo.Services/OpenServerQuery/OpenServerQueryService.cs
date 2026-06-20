using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using ProtoBuf;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     开放信息（OSQ）服务
/// </summary>
public sealed class OpenServerQueryService : IOpenServerQueryService
{
    private const string DefaultListenPrefix = "http://127.0.0.1:18089/";
    private const string ReportPath = "/api/osq/report";
    private const int PushIntervalSec = 2;
    private const int MaxMarkerLayersInPayload = 32;
    private const int MaxMapMarkersPerLayerInPayload = 2048;
    private const int MaxInlineMapTilesInPayload = 48;
    private const int MaxMapTileBytesPerFile = 1 * 1024 * 1024;
    private const int MaxMapZoomOut = 8;
    private const int ChunkMask = 4_194_303;
    private const int MapTileSize = 512;
    private const int MapExportSignatureVersion = 2;
    private const int MaxRecentOsqChats = 48;
    private const int MaxRecentOsqPlayerEvents = 48;
    private const int MaxRecentOsqNotifications = 48;
    private const int MaxTailReadBytesPerLog = 512 * 1024;
    private const int MaxTailReadLinesPerLog = 600;
    private const int MaxOsqRequestBodyBytes = 8 * 1024 * 1024;
    private const int EndpointGzipThresholdBytes = 32 * 1024;

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions OutboundJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    private static readonly Regex NonceRegex = new("^[a-z0-9]{8,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TokenRegex = new("^[A-Za-z0-9_-]{16,256}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex NamespacedTypeLikeRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*){2,}:?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly string[] MapDataDirectoryNames = ["ServerMap", "LiveMap"];
    private static readonly TimeSpan NonceTtl = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan MaxClockDrift = TimeSpan.FromMinutes(10);

    private static readonly string[] KnownLogTimeFormats =
    [
        "yyyy-MM-dd HH:mm:ss",
        "yyyy/MM/dd HH:mm:ss",
        "yyyy-MM-ddTHH:mm:ss",
        "d.M.yyyy HH:mm:ss",
        "M/d/yyyy HH:mm:ss"
    ];

    private static readonly Regex[] ChatLinePatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Talk|Chat)\]\s*(?:\d+\s*\|\s*)?(?<sender>[^:]{1,64}):\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2}).*?\[(?:Talk|Chat)\]\s*(?:\d+\s*\|\s*)?(?<sender>[^:]{1,64}):\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2}).*?Message to all in group \d+:\s*(?<sender>[^:]{1,64}):\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2}).*?<(?<sender>[^>]{1,64})>\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^\[(?<time>[^\]]+)\]\s*\[(?:Talk|Chat)\]\s*(?:\d+\s*\|\s*)?(?<sender>[^:]{1,64}):\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}).*?\[(?:Talk|Chat)\]\s*(?:\d+\s*\|\s*)?(?<sender>[^:]{1,64}):\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2}).*?<(?<sender>[^>]{1,64})>\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^\[(?<time>[^\]]+)\]\s*<(?<sender>[^>]{1,64})>\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] JoinEventPatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*(?<player>[^\[\]:]{1,64})\s+\[[^\]]+\](?::\d+)?\s+joins\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*(?<player>[^:]{1,64})\s+加入了服务器\.?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Audit\]\s*(?<player>[^\.]{1,64})\s+joined\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] LeaveEventPatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*Player\s+(?<player>[^\.]{1,64})\s+left\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*Player\s+(?<player>[^\.:\r\n]{1,64})\s+got removed(?:\.|:|：).*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*(?<player>[^\[\]:]{1,64})\s+\[[^\]]+\](?::\d+)?\s+(?:left|leaves|got removed)(?:\.|:|：).*$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*(?<player>[^:]{1,64})\s+离开了服务器\.?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Event\]\s*(?<player>[^。\.\[\]:]{1,64})(?:离开了游戏|离开了服务器|已被移除)[。\.]?$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Audit\]\s*(?<player>[^\.]{1,64})\s+left\.$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] NotificationLinePatterns =
    [
        new(@"^(?:\[log\]\s*)?(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+Notification|Notification|服务器通知)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?:\[log\]\s*)?(?<time>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+Notification|Notification|服务器通知)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?:\[log\]\s*)?\[(?<time>[^\]]+)\]\s*\[(?:Server\s+Notification|Notification|服务器通知)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] DeathEventPatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Audit\]\s*(?<player>[^\.]{1,64})\s+died(?:\.\s*(?:Death\s+message|Death\s+reason|Reason)[:：]\s*(?<reason>.+))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+)?(?:Notification|Event)\]\s*(?:Player\s+)?(?<player>[^\.:\r\n]{1,64})\s+(?:died|has died)\b(?:\.\s*(?:(?:Death\s+message|Death\s+reason|Reason)[:：]\s*)?(?<reason>.+))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] ChineseDeathEventPatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[Audit\]\s*(?<player>[^。]{1,64})已死亡(?:。(?:死亡消息|死因|原因)[:：](?<reason>.+))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+)?(?:Notification|Event)\]\s*(?:玩家\s*)?(?<player>[^。:\r\n]{1,64}?)(?:已死亡|死亡)(?:。?(?:死亡消息|死因|原因)[:：]\s*(?<reason>.+))?\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex[] FallDeathEventPatterns =
    [
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+)?(?:Notification|Event|Audit)\]\s*(?:Player\s+)?(?<player>[^\.:\r\n]{1,64})\s+(?<reason>(?:fell from a high place|fell to (?:his|her|their) death|fell off .+|plummeted .+).*)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+)?(?:Notification|Event|Audit)\]\s*(?:玩家\s*)?(?<player>[^。:\r\n]{1,64}?)(?<reason>(?:摔死了|摔死|从高处坠落而亡|坠落身亡).*)\s*$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
    ];

    private static readonly Regex GenericRuntimeNotificationPattern =
        new(@"^(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Notification|Event)\]\s*(?<content>.+)$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly HashSet<string> SupportedMapTileExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png",
        ".jpg",
        ".jpeg",
        ".webp"
    };

    private static readonly uint[] PngCrcTable = BuildPngCrcTable();

    private readonly IServerProcessService? _serverProcessService;
    private readonly IInstanceProfileService? _profileService;
    private readonly IInstanceServerConfigService? _serverConfigService;
    private readonly IOsqSnapshotCacheService? _osqSnapshotCacheService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _stateSync = new();
    private readonly object _nonceSync = new();
    private readonly ConcurrentDictionary<string, NonceState> _nonceCache = new(StringComparer.Ordinal);

    private RuntimeState? _runtime;
    private CancellationTokenSource? _runtimeCts;
    private Task? _runtimeTask;

    public OpenServerQueryService()
        : this(null, null, null, null)
    {
    }

    public OpenServerQueryService(
        IServerProcessService? serverProcessService,
        IInstanceProfileService? profileService,
        IInstanceServerConfigService? serverConfigService,
        IOsqSnapshotCacheService? osqSnapshotCacheService)
    {
        _serverProcessService = serverProcessService;
        _profileService = profileService;
        _serverConfigService = serverConfigService;
        _osqSnapshotCacheService = osqSnapshotCacheService;
    }

    public event EventHandler<string>? OutputReceived;

    private static string SettingsPath => Path.Combine(WorkspacePathHelper.RobotRoot, "openserverquery-settings.json");

    /// <inheritdoc />
    public async Task<OpenServerQueryRuntimeSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.RobotRoot);

        if (!File.Exists(SettingsPath))
        {
            var defaults = BuildDefaultSettings();
            await SaveSettingsAsync(defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(SettingsPath, cancellationToken);
            var parsed = JsonSerializer.Deserialize<OpenServerQueryRuntimeSettings>(json, JsonReadOptions) ?? BuildDefaultSettings();
            return Normalize(parsed);
        }
        catch
        {
            return BuildDefaultSettings();
        }
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(OpenServerQueryRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.RobotRoot);
        var normalized = Normalize(settings);
        var json = JsonSerializer.Serialize(normalized, JsonWriteOptions);
        await File.WriteAllTextAsync(SettingsPath, json, cancellationToken);
    }

    /// <inheritdoc />
    public OpenServerQueryRuntimeStatus GetRuntimeStatus()
    {
        lock (_stateSync)
        {
            var runtime = _runtime;
            if (runtime is null)
            {
                return new OpenServerQueryRuntimeStatus
                {
                    IsListening = false
                };
            }

            var endpoints = runtime.EndpointsByHost
                .OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase)
                .Select(x => new OpenServerQueryEndpointRuntime
                {
                    ServerHost = x.Key,
                    Enabled = x.Value.Settings.Enabled,
                    LastServerName = x.Value.LastServerName,
                    LastServerStatus = x.Value.LastServerStatus,
                    LastOnlinePlayers = x.Value.LastOnlinePlayers,
                    LastMaxPlayers = x.Value.LastMaxPlayers,
                    LastPayloadTimeUtc = x.Value.LastPayloadTimeUtc,
                    LastReceivedUtc = FormatIso(x.Value.LastReceivedUtc),
                    LastError = x.Value.LastError
                })
                .ToList();

            return new OpenServerQueryRuntimeStatus
            {
                IsListening = runtime.Listener?.IsListening == true,
                ListenPrefix = runtime.Settings.ListenPrefix,
                StartedAtUtc = FormatIso(runtime.StartedAtUtc),
                LastReceivedUtc = FormatIso(runtime.LastReceivedUtc),
                LastError = runtime.LastError,
                TotalRequests = runtime.TotalRequests,
                AcceptedRequests = runtime.AcceptedRequests,
                RejectedRequests = runtime.RejectedRequests,
                Endpoints = endpoints
            };
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(OpenServerQueryRuntimeSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is not null && _runtimeTask is not null)
            {
                throw new InvalidOperationException("联结服务已在运行。");
            }

            var normalized = Normalize(settings);
            await SaveSettingsAsync(normalized, cancellationToken);

            var hostByToken = new Dictionary<string, string>(StringComparer.Ordinal);
            var endpoints = new Dictionary<string, EndpointState>(StringComparer.OrdinalIgnoreCase);
            foreach (var endpoint in normalized.Endpoints)
            {
                endpoints[endpoint.ServerHost] = new EndpointState
                {
                    Settings = endpoint
                };

                if (!endpoint.Enabled)
                {
                    continue;
                }

                var token = endpoint.Token.Trim();
                hostByToken.TryAdd(token, endpoint.ServerHost);
            }

            var runtime = new RuntimeState
            {
                Settings = normalized,
                EndpointsByHost = endpoints,
                HostByToken = hostByToken
            };

            _runtimeCts = new CancellationTokenSource();
            _runtime = runtime;
            _runtimeTask = Task.Run(() => RunRuntimeAsync(runtime, _runtimeCts.Token), CancellationToken.None);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Task? runTask = null;
        RuntimeState? runtime = null;
        CancellationTokenSource? cts = null;

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_runtimeTask is null || _runtime is null)
            {
                return;
            }

            runTask = _runtimeTask;
            runtime = _runtime;
            cts = _runtimeCts;
            _runtimeTask = null;
            _runtime = null;
            _runtimeCts = null;
            cts?.Cancel();
        }
        finally
        {
            _gate.Release();
        }

        try
        {
            runtime?.Listener?.Close();
        }
        catch
        {
            // ignore
        }

        if (runTask is null)
        {
            return;
        }

        var timeoutTask = Task.Delay(gracefulTimeout, cancellationToken);
        var completed = await Task.WhenAny(runTask, timeoutTask);
        if (!ReferenceEquals(completed, runTask))
        {
            throw new TimeoutException("停止联结服务超时。");
        }

        await runTask;
        cts?.Dispose();
    }

    private async Task RunRuntimeAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        var listenerTask = Task.Run(() => ListenLoopAsync(runtime, cancellationToken), CancellationToken.None);
        var pushTask = Task.Run(() => PushLoopAsync(runtime, cancellationToken), CancellationToken.None);

        try
        {
            await Task.WhenAll(listenerTask, pushTask);
        }
        finally
        {
            await _gate.WaitAsync();
            try
            {
                if (ReferenceEquals(_runtime, runtime))
                {
                    _runtime = null;
                    _runtimeTask = null;
                    _runtimeCts?.Dispose();
                    _runtimeCts = null;
                }
            }
            finally
            {
                _gate.Release();
            }
        }
    }

    private async Task ListenLoopAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add(runtime.Settings.ListenPrefix);

        try
        {
            listener.Start();
            runtime.Listener = listener;
            Emit($"[osq] listening: {runtime.Settings.ListenPrefix}");
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    listener.Close();
                }
                catch
                {
                    // ignore
                }
            });

            while (!cancellationToken.IsCancellationRequested)
            {
                HttpListenerContext? context = null;
                try
                {
                    context = await listener.GetContextAsync();
                    _ = Task.Run(() => HandleRequestAsync(runtime, context, cancellationToken), cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    lock (_stateSync)
                    {
                        runtime.LastError = ex.Message;
                    }
                    Emit($"[osq] listener warning: {ex.Message}");
                    await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            lock (_stateSync)
            {
                runtime.LastError = ex.Message;
            }
            Emit($"[osq] listener start failed: {ex.Message}");
        }
        finally
        {
            try
            {
                listener.Close();
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task PushLoopAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        Emit("[osq] local push enabled");

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (ShouldPushLocalSnapshot(runtime))
                {
                    await PushLocalSnapshotAsync(runtime, cancellationToken);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                lock (_stateSync)
                {
                    runtime.LastError = ex.Message;
                }
                Emit($"[osq] push warning: {ex.Message}");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(PushIntervalSec), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private static bool ShouldPushLocalSnapshot(RuntimeState runtime)
    {
        return runtime.Settings.Enabled || HasConfiguredRobotGroups();
    }

    private async Task PushLocalSnapshotAsync(RuntimeState runtime, CancellationToken cancellationToken)
    {
        var contexts = await TryBuildLocalServerContextsAsync(cancellationToken);
        if (contexts.Count == 0)
        {
            lock (_stateSync)
            {
                foreach (var endpoint in runtime.EndpointsByHost.Values)
                {
                    if (!endpoint.Settings.Enabled)
                    {
                        continue;
                    }

                    endpoint.LastError = "local-server-not-running";
                }
            }
            return;
        }

        for (var index = 0; index < contexts.Count; index++)
        {
            var context = contexts[index];
            var now = DateTimeOffset.UtcNow;
            var nonce = GenerateNonce();
            var needsMapPayload = runtime.Settings.IncludeMapData &&
                runtime.EndpointsByHost.Values.Any(endpoint => endpoint.Settings.Enabled);
            var payload = await BuildLocalServerSnapshotAsync(
                context,
                runtime.Settings,
                now,
                nonce,
                needsMapPayload,
                cancellationToken);
            var qqPayload = CreateSnapshotForOutputTarget(payload, runtime.Settings, includeMapData: false);
            var qqJson = JsonSerializer.Serialize(qqPayload, OutboundJsonOptions);
            var mapJson = needsMapPayload
                ? JsonSerializer.Serialize(payload, OutboundJsonOptions)
                : qqJson;

            var cachedNode = JsonNode.Parse(qqJson)?.AsObject();
            if (cachedNode is not null)
            {
                _osqSnapshotCacheService?.AddSnapshot("local", cachedNode, now);
                _osqSnapshotCacheService?.AddSnapshot($"local:{context.Profile.Id}", cachedNode.DeepClone().AsObject(), now);
            }

            foreach (var pair in runtime.EndpointsByHost)
            {
                var endpoint = pair.Value;
                if (!endpoint.Settings.Enabled)
                {
                    continue;
                }

                if (!IsValidToken(endpoint.Settings.Token))
                {
                    lock (_stateSync)
                    {
                        endpoint.LastError = "invalid-token";
                    }
                    continue;
                }

                var reportUri = BuildEndpointReportUri(endpoint.Settings.ServerHost, runtime.Settings.AllowInsecureHttp);
                if (reportUri is null)
                {
                    lock (_stateSync)
                    {
                        endpoint.LastError = "invalid-endpoint";
                    }
                    continue;
                }

                try
                {
                    var includeEndpointMap = runtime.Settings.IncludeMapData;
                    var endpointPayload = includeEndpointMap ? payload : qqPayload;
                    var json = includeEndpointMap ? mapJson : qqJson;

                    await SendEndpointAsync(
                        reportUri,
                        endpoint.Settings.Token,
                        json,
                        now,
                        nonce,
                        runtime.Settings.RequestTimeoutSec <= 0 ? 8 : runtime.Settings.RequestTimeoutSec,
                        cancellationToken);

                    lock (_stateSync)
                    {
                        endpoint.LastPayloadTimeUtc = endpointPayload.TimestampUtc;
                        endpoint.LastServerName = endpointPayload.Server.Name;
                        endpoint.LastServerStatus = endpointPayload.Server.Status;
                        endpoint.LastOnlinePlayers = endpointPayload.Server.OnlinePlayerCount;
                        endpoint.LastMaxPlayers = endpointPayload.Server.MaxPlayers;
                        endpoint.LastReceivedUtc = DateTimeOffset.UtcNow;
                        endpoint.LastError = string.Empty;

                        runtime.LastReceivedUtc = endpoint.LastReceivedUtc;
                    }
                }
                catch (Exception ex)
                {
                    lock (_stateSync)
                    {
                        endpoint.LastError = ex.Message;
                        runtime.LastError = ex.Message;
                    }
                }

                if (index + 1 < contexts.Count)
                {
                    await Task.Delay(100, cancellationToken);
                }
            }
        }
    }

    private async Task<IReadOnlyList<LocalServerContext>> TryBuildLocalServerContextsAsync(CancellationToken cancellationToken)
    {
        if (_serverProcessService is null || _profileService is null || _serverConfigService is null)
        {
            return [];
        }

        var contexts = new List<LocalServerContext>();
        var runtimeStatuses = _serverProcessService.GetCurrentStatuses()
            .Where(status => status.IsRunning && !string.IsNullOrWhiteSpace(status.ProfileId))
            .ToList();

        foreach (var runtimeStatus in runtimeStatuses)
        {
            var context = await TryBuildLocalServerContextAsync(runtimeStatus, cancellationToken);
            if (context is not null)
            {
                contexts.Add(context);
            }
        }

        return contexts;
    }

    private async Task<LocalServerContext?> TryBuildLocalServerContextAsync(CancellationToken cancellationToken)
    {
        if (_serverProcessService is null || _profileService is null || _serverConfigService is null)
        {
            return null;
        }

        return await TryBuildLocalServerContextAsync(_serverProcessService.GetCurrentStatus(), cancellationToken);
    }

    private async Task<LocalServerContext?> TryBuildLocalServerContextAsync(
        ServerRuntimeStatus runtimeStatus,
        CancellationToken cancellationToken)
    {
        if (_profileService is null || _serverConfigService is null ||
            !runtimeStatus.IsRunning || string.IsNullOrWhiteSpace(runtimeStatus.ProfileId))
        {
            return null;
        }

        var profile = _profileService.GetProfileById(runtimeStatus.ProfileId);
        if (profile is null)
        {
            return null;
        }

        var serverSettings = await _serverConfigService.LoadServerSettingsAsync(profile, cancellationToken);
        var worldSettings = await _serverConfigService.LoadWorldSettingsAsync(profile, cancellationToken);
        var configRoot = TryReadProfileConfigRoot(profile.DirectoryPath);

        var mapSizes = ResolveMapSizes(configRoot, worldSettings);
        var savePath = ResolveSaveDatabasePath(profile, worldSettings);
        var description = ResolveServerDescription(configRoot);

        return new LocalServerContext
        {
            Profile = profile,
            RuntimeStatus = runtimeStatus,
            ServerSettings = serverSettings,
            WorldSettings = worldSettings,
            RawConfigRoot = configRoot,
            MapSizeX = mapSizes.MapSizeX,
            MapSizeY = mapSizes.MapSizeY,
            MapSizeZ = mapSizes.MapSizeZ,
            SaveDatabasePath = savePath,
            Description = description
        };
    }

    private async Task<OsqSnapshotEnvelope> BuildLocalServerSnapshotAsync(
        LocalServerContext context,
        OpenServerQueryRuntimeSettings settings,
        DateTimeOffset now,
        string nonce,
        bool includeMapData,
        CancellationToken cancellationToken)
    {
        var serverVersion = ResolveDisplayServerVersion(context.Profile.Version);
        var whitelistMode = ResolveWhitelistModeText(context.ServerSettings.WhitelistMode);
        var onlinePlayerNames = (context.RuntimeStatus.OnlinePlayerNames ?? [])
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlinePlayers = onlinePlayerNames.Count > 0
            ? onlinePlayerNames.Count
            : Math.Max(0, context.RuntimeStatus.OnlinePlayers);
        var maxPlayers = Math.Max(0, context.ServerSettings.MaxClients);

        var snapshot = new OsqSnapshotEnvelope
        {
            ModId = "launchergo-osq",
            SchemaVersion = 2,
            TimestampUtc = now.ToString("O", CultureInfo.InvariantCulture),
            UnixTime = now.ToUnixTimeSeconds(),
            Nonce = nonce,
            Capabilities = BuildSnapshotCapabilities(settings, includeMapData),
            Server = new OsqServerInfo
            {
                Name = context.ServerSettings.ServerName ?? string.Empty,
                Version = serverVersion,
                NetworkVersion = serverVersion,
                ApiVersion = string.Empty,
                Status = context.RuntimeStatus.IsRunning ? "RunGame" : "Stopped",
                WhitelistMode = whitelistMode,
                WhitelistEnforced = !string.Equals(whitelistMode, "Off", StringComparison.OrdinalIgnoreCase),
                HasPassword = !string.IsNullOrWhiteSpace(context.ServerSettings.Password),
                PlayerCount = onlinePlayers,
                OnlinePlayerCount = onlinePlayers,
                MaxPlayers = maxPlayers,
                Description = context.Description,
                WelcomeMessage = context.ServerSettings.WelcomeMessage ?? string.Empty,
                Dedicated = true,
                ServerIp = context.ServerSettings.Ip ?? string.Empty,
                ServerPort = context.ServerSettings.Port,
                WorldName = context.WorldSettings.WorldName ?? string.Empty,
                UptimeSeconds = ResolveUptimeSeconds(context.RuntimeStatus.StartedAtUtc, now)
            }
        };

        if (settings.IncludePlayers && onlinePlayerNames.Count > 0)
        {
            snapshot.Players = onlinePlayerNames
                .Select(name => new OsqPlayerInfo
                {
                    PlayerUid = name,
                    PlayerName = name,
                    IsOnline = true,
                    IsPlaying = true,
                    ConnectionState = context.RuntimeStatus.IsRunning ? "Playing" : "Disconnected",
                    DelayLevel = "unknown",
                    LastSeenUtc = now.ToString("O", CultureInfo.InvariantCulture)
                })
                .ToList();
        }

        var activity = BuildRecentServerActivitySnapshot(context, now, cancellationToken);

        if (settings.IncludeNotifications)
        {
            snapshot.ServerNotifications = activity.Notifications;
        }

        if (settings.IncludeChats)
        {
            snapshot.RecentChats = activity.Chats;
        }

        if (settings.IncludePlayerEvents)
        {
            snapshot.PlayerEvents = activity.PlayerEvents;
        }

        snapshot.ServerMap = includeMapData
            ? await BuildServerMapSnapshotAsync(context, now, cancellationToken)
            : null;

        return snapshot;
    }

    private static OsqSnapshotEnvelope CreateSnapshotForOutputTarget(
        OsqSnapshotEnvelope source,
        OpenServerQueryRuntimeSettings settings,
        bool includeMapData)
    {
        return new OsqSnapshotEnvelope
        {
            ModId = source.ModId,
            SchemaVersion = source.SchemaVersion,
            TimestampUtc = source.TimestampUtc,
            UnixTime = source.UnixTime,
            Nonce = source.Nonce,
            Capabilities = BuildSnapshotCapabilities(settings, includeMapData && source.ServerMap is not null),
            Server = source.Server,
            Players = source.Players,
            PlayerEvents = source.PlayerEvents,
            RecentChats = source.RecentChats,
            ServerNotifications = source.ServerNotifications,
            ServerMap = includeMapData ? source.ServerMap : null
        };
    }

    private static LocalServerActivitySnapshot BuildRecentServerActivitySnapshot(
        LocalServerContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var signals = new List<ParsedServerSignal>();
        var dedup = new HashSet<string>(StringComparer.Ordinal);

        foreach (var logPath in EnumerateServerLogCandidates(context.Profile.DirectoryPath))
        {
            foreach (var line in ReadTailLines(logPath, MaxTailReadBytesPerLog, MaxTailReadLinesPerLog, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!TryParseServerSignal(line, now, out var signal))
                {
                    continue;
                }

                var signature = BuildSignalSignature(signal);
                if (!dedup.Add(signature))
                {
                    continue;
                }

                signals.Add(signal);
            }
        }

        if (signals.Count == 0)
        {
            return new LocalServerActivitySnapshot();
        }

        signals.Sort(static (a, b) => a.SortTimeUtc.CompareTo(b.SortTimeUtc));

        var chats = signals
            .Where(x => x.Kind == ServerSignalKind.Chat)
            .TakeLast(MaxRecentOsqChats)
            .Select(x => new OsqChatInfo
            {
                TimestampUtc = x.TimestampUtc,
                ChannelId = 0,
                SenderName = x.Sender,
                SenderUid = string.Empty,
                Message = x.Content,
                Data = string.Empty
            })
            .ToList();

        var playerEvents = signals
            .Where(x => x.Kind == ServerSignalKind.PlayerEvent)
            .TakeLast(MaxRecentOsqPlayerEvents)
            .Select(x => new OsqPlayerEventInfo
            {
                TimestampUtc = x.TimestampUtc,
                EventType = x.EventType,
                PlayerName = x.PlayerName,
                PlayerUid = string.Empty,
                ConnectionState = x.ConnectionState
            })
            .ToList();

        var notifications = signals
            .Where(x => x.Kind == ServerSignalKind.Notification)
            .TakeLast(MaxRecentOsqNotifications)
            .Select(x => new OsqServerNotificationInfo
            {
                TimestampUtc = x.TimestampUtc,
                Message = x.Content
            })
            .ToList();

        return new LocalServerActivitySnapshot
        {
            Chats = chats,
            PlayerEvents = playerEvents,
            Notifications = notifications
        };
    }

    private static List<string> BuildSnapshotCapabilities(OpenServerQueryRuntimeSettings settings, bool includeMapData)
    {
        var capabilities = new List<string> { "serverInfo" };
        if (settings.IncludePlayers)
        {
            capabilities.Add("players");
        }

        if (settings.IncludeChats)
        {
            capabilities.Add("chats");
        }

        if (settings.IncludePlayerEvents)
        {
            capabilities.Add("playerEvents");
        }

        if (includeMapData)
        {
            capabilities.Add("map");
        }

        return capabilities;
    }

    private static IEnumerable<string> EnumerateServerLogCandidates(string profileDirectoryPath)
    {
        var logsPath = WorkspacePathHelper.GetProfileLogsPath(profileDirectoryPath);
        if (string.IsNullOrWhiteSpace(logsPath))
        {
            yield break;
        }

        yield return Path.Combine(logsPath, "server-main.log");
        yield return Path.Combine(logsPath, "server-chat.log");
        yield return Path.Combine(logsPath, "server-audit.log");
    }

    private static IReadOnlyList<string> ReadTailLines(
        string logPath,
        int maxBytes,
        int maxLines,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(logPath))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length <= 0)
            {
                return [];
            }

            long start = Math.Max(0, stream.Length - Math.Max(1, maxBytes));
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, true, 8192, leaveOpen: true);
            if (start > 0)
            {
                // Drop potential partial line created by tail seek.
                _ = reader.ReadLine();
            }

            var lines = new Queue<string>(Math.Max(1, maxLines));
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                var normalized = NormalizeLogText(line);
                if (normalized.Length == 0)
                {
                    continue;
                }

                if (lines.Count >= maxLines)
                {
                    _ = lines.Dequeue();
                }

                lines.Enqueue(normalized);
            }

            return lines.ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool TryParseServerSignal(string line, DateTimeOffset fallbackUtcNow, out ParsedServerSignal signal)
    {
        signal = null!;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var trimmed = line.Trim();
        var unwrapped = StripLauncherLogPrefix(trimmed);

        if (TryParseNotificationSignal(unwrapped, fallbackUtcNow, out signal))
        {
            return true;
        }

        if (TryParsePlayerEventSignal(unwrapped, fallbackUtcNow, out signal))
        {
            return true;
        }

        if (TryParseChatSignal(unwrapped, fallbackUtcNow, out signal))
        {
            return true;
        }

        return false;
    }

    private static bool TryParseChatSignal(string line, DateTimeOffset fallbackUtcNow, out ParsedServerSignal signal)
    {
        signal = null!;
        if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(line))
        {
            return false;
        }

        foreach (var pattern in ChatLinePatterns)
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var sender = NormalizeNotificationContent(match.Groups["sender"].Value);
            var content = NormalizeNotificationContent(match.Groups["content"].Value);
            if (sender.Length == 0 || content.Length == 0)
            {
                continue;
            }

            if (IsLikelyGroupRelayEcho(sender)
                || IsLikelyGroupRelayEcho(content)
                || ServerLogPrivacyFilter.ShouldSuppressRelayParts(line, sender, content))
            {
                continue;
            }

            var sortTime = ParseLogTime(match.Groups["time"].Value, fallbackUtcNow);
            signal = new ParsedServerSignal
            {
                Kind = ServerSignalKind.Chat,
                SortTimeUtc = sortTime,
                TimestampUtc = sortTime.ToString("O", CultureInfo.InvariantCulture),
                Sender = sender,
                Content = content
            };
            return true;
        }

        return false;
    }

    private static bool TryParsePlayerEventSignal(string line, DateTimeOffset fallbackUtcNow, out ParsedServerSignal signal)
    {
        signal = null!;

        if (TryParsePlayerEventByPatterns(line, JoinEventPatterns, "join", "Playing", fallbackUtcNow, out signal))
        {
            return true;
        }

        if (TryParsePlayerEventByPatterns(line, LeaveEventPatterns, "leave", "Disconnected", fallbackUtcNow, out signal))
        {
            return true;
        }

        if (TryParseDeathEventByPatterns(line, DeathEventPatterns, fallbackUtcNow, out signal)
            || TryParseDeathEventByPatterns(line, ChineseDeathEventPatterns, fallbackUtcNow, out signal)
            || TryParseDeathEventByPatterns(line, FallDeathEventPatterns, fallbackUtcNow, out signal))
        {
            return true;
        }

        return false;
    }

    private static bool TryParsePlayerEventByPatterns(
        string line,
        IEnumerable<Regex> patterns,
        string eventType,
        string connectionState,
        DateTimeOffset fallbackUtcNow,
        out ParsedServerSignal signal)
    {
        signal = null!;
        foreach (var pattern in patterns)
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var player = NormalizeLogText(match.Groups["player"].Value);
            if (player.Length == 0)
            {
                continue;
            }

            var sortTime = ParseLogTime(match.Groups["time"].Value, fallbackUtcNow);
            signal = new ParsedServerSignal
            {
                Kind = ServerSignalKind.PlayerEvent,
                SortTimeUtc = sortTime,
                TimestampUtc = sortTime.ToString("O", CultureInfo.InvariantCulture),
                EventType = eventType,
                PlayerName = player,
                ConnectionState = connectionState
            };
            return true;
        }

        return false;
    }

    private static bool TryParseDeathEventByPatterns(
        string line,
        IEnumerable<Regex> patterns,
        DateTimeOffset fallbackUtcNow,
        out ParsedServerSignal signal)
    {
        signal = null!;
        foreach (var pattern in patterns)
        {
            var deathMatch = pattern.Match(line);
            if (!deathMatch.Success)
            {
                continue;
            }

            var player = NormalizeLogText(deathMatch.Groups["player"].Value);
            if (player.Length == 0)
            {
                continue;
            }

            var reason = NormalizeLogText(deathMatch.Groups["reason"].Value);
            var message = reason.Length == 0 ? $"玩家 {player} 死亡" : $"玩家 {player} 死亡：{reason}";
            var sortTime = ParseLogTime(deathMatch.Groups["time"].Value, fallbackUtcNow);
            signal = new ParsedServerSignal
            {
                Kind = ServerSignalKind.Notification,
                SortTimeUtc = sortTime,
                TimestampUtc = sortTime.ToString("O", CultureInfo.InvariantCulture),
                Content = message
            };
            return true;
        }

        return false;
    }

    private static bool TryParseNotificationSignal(string line, DateTimeOffset fallbackUtcNow, out ParsedServerSignal signal)
    {
        signal = null!;
        foreach (var pattern in NotificationLinePatterns)
        {
            var match = pattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var content = NormalizeNotificationContent(match.Groups["content"].Value);
            if (content.Length == 0)
            {
                continue;
            }

            if (IsLikelyGroupRelayEcho(content) || ServerLogPrivacyFilter.ShouldSuppressRelayParts(content))
            {
                continue;
            }

            var sortTime = ParseLogTime(match.Groups["time"].Value, fallbackUtcNow);
            signal = new ParsedServerSignal
            {
                Kind = ServerSignalKind.Notification,
                SortTimeUtc = sortTime,
                TimestampUtc = sortTime.ToString("O", CultureInfo.InvariantCulture),
                Content = content
            };
            return true;
        }

        var genericMatch = GenericRuntimeNotificationPattern.Match(line);
        if (!genericMatch.Success)
        {
            return false;
        }

        var genericContent = NormalizeLogText(genericMatch.Groups["content"].Value);
        if (!ShouldIncludeGenericRuntimeNotification(genericContent))
        {
            return false;
        }

        var genericSortTime = ParseLogTime(genericMatch.Groups["time"].Value, fallbackUtcNow);
        signal = new ParsedServerSignal
        {
            Kind = ServerSignalKind.Notification,
            SortTimeUtc = genericSortTime,
            TimestampUtc = genericSortTime.ToString("O", CultureInfo.InvariantCulture),
            Content = genericContent
        };
        return true;
    }

    private static bool ShouldIncludeGenericRuntimeNotification(string content)
    {
        if (content.Length == 0)
        {
            return false;
        }

        if (IsLikelyGroupRelayEcho(content))
        {
            return false;
        }

        if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(content))
        {
            return false;
        }

        if (NamespacedTypeLikeRegex.IsMatch(content))
        {
            return false;
        }

        if (content.StartsWith("Mod '", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = content.ToLowerInvariant();
        if (normalized.StartsWith("handling console command", StringComparison.Ordinal))
        {
            return false;
        }

        if (normalized.StartsWith("message to all in group", StringComparison.Ordinal))
        {
            return false;
        }

        return normalized.Contains("temporal", StringComparison.Ordinal)
               || normalized.Contains("stability", StringComparison.Ordinal)
               || normalized.Contains("storm", StringComparison.Ordinal)
               || normalized.Contains("rift", StringComparison.Ordinal)
               || normalized.Contains("时空", StringComparison.Ordinal)
               || normalized.Contains("稳态", StringComparison.Ordinal)
               || normalized.Contains("风暴", StringComparison.Ordinal)
               || normalized.Contains("裂隙", StringComparison.Ordinal);
    }

    private static string NormalizeNotificationContent(string content)
    {
        var decoded = WebUtility.HtmlDecode(content ?? string.Empty);
        var withoutHtml = HtmlTagRegex.Replace(decoded, string.Empty);
        return NormalizeLogText(withoutHtml);
    }

    private static bool IsLikelyGroupRelayEcho(string content)
    {
        var normalized = NormalizeLogText(content);
        if (normalized.Length == 0)
        {
            return false;
        }

        return normalized.StartsWith("[群聊 ", StringComparison.Ordinal)
               || normalized.StartsWith("[group ", StringComparison.OrdinalIgnoreCase)
               || normalized.Contains("群聊 ", StringComparison.Ordinal);
    }

    private static DateTimeOffset ParseLogTime(string raw, DateTimeOffset fallbackUtcNow)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
        {
            return fallbackUtcNow;
        }

        foreach (var format in KnownLogTimeFormats)
        {
            if (DateTime.TryParseExact(
                    value,
                    format,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                return new DateTimeOffset(DateTime.SpecifyKind(parsed, DateTimeKind.Local)).ToUniversalTime();
            }
        }

        if (DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsedOffset))
        {
            return parsedOffset.ToUniversalTime();
        }

        if (DateTime.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                out var parsedLocal))
        {
            return new DateTimeOffset(DateTime.SpecifyKind(parsedLocal, DateTimeKind.Local)).ToUniversalTime();
        }

        return fallbackUtcNow;
    }

    private static string BuildSignalSignature(ParsedServerSignal signal)
    {
        return $"{signal.Kind}|{signal.TimestampUtc}|{signal.Sender}|{signal.Content}|{signal.EventType}|{signal.PlayerName}|{signal.ConnectionState}";
    }

    private static string NormalizeLogText(string text)
    {
        var normalized = (text ?? string.Empty)
            .Replace('\r', ' ')
            .Replace('\n', ' ')
            .Trim();
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        return MultiWhitespaceRegex.Replace(normalized, " ");
    }

    private static string StripLauncherLogPrefix(string line)
    {
        const string prefix = "[log]";
        var trimmed = (line ?? string.Empty).Trim();
        return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? trimmed[prefix.Length..].TrimStart()
            : trimmed;
    }

    private async Task<OsqServerMapInfo> BuildServerMapSnapshotAsync(
        LocalServerContext context,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var snapshot = new OsqServerMapInfo
        {
            Standard = "osq-server-map-v1",
            Enabled = true,
            FullSnapshot = true,
            Source = "osq-native-vcdb",
            IncludeTilePayload = false,
            SavegameIdentifier = string.Empty,
            ServerMapRoot = string.Empty,
            ServerMapUrl = string.Empty,
            GeneratedAtUtc = now.ToString("O", CultureInfo.InvariantCulture),
            DataDigest = string.Empty,
            SkippedReason = string.Empty,
            PlayersCount = 0,
            MarkersCount = 0,
            MarkerLayers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase),
            Tiles = []
        };

        string? selectedMapRoot = null;
        string savegameIdentifier = string.Empty;
        var serverMapCandidate = TryFindServerMapRoot(context.Profile.DirectoryPath);
        if (serverMapCandidate is not null)
        {
            selectedMapRoot = serverMapCandidate.RootPath;
            savegameIdentifier = serverMapCandidate.SavegameIdentifier;
            FillServerMapSnapshotFromRoot(snapshot, selectedMapRoot, "osq-servermap-disk", now);
            snapshot.SavegameIdentifier = savegameIdentifier;
            if (snapshot.Settings.HasValue)
            {
                snapshot.DataDigest = ComputeMapPayloadDigest(snapshot);
                return snapshot;
            }

            snapshot.SkippedReason = "servermap-settings-json-missing";
        }
        else
        {
            snapshot.SkippedReason = "servermap-disk-not-found";
        }

        snapshot.Enabled = false;
        snapshot.FullSnapshot = false;
        snapshot.IncludeTilePayload = false;
        snapshot.Source = "map-source-not-found";
        snapshot.SavegameIdentifier = savegameIdentifier;
        snapshot.ServerMapRoot = selectedMapRoot ?? string.Empty;
        snapshot.ServerMapUrl = string.Empty;
        snapshot.OverviewData = null;
        snapshot.Tiles = [];
        snapshot.MarkerLayers = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        return snapshot;
    }

    private static string ResolveImageMimeType(string extension)
    {
        var ext = (extension ?? string.Empty).Trim().ToLowerInvariant();
        return ext switch
        {
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".webp" => "image/webp",
            ".gif" => "image/gif",
            _ => "application/octet-stream"
        };
    }

    private bool TryEnsureVsslMapExport(
        LocalServerContext context,
        DateTimeOffset now,
        out string reason,
        out string osqMapRoot,
        out string savegameIdentifier)
    {
        reason = string.Empty;
        osqMapRoot = Path.Combine(
            WorkspacePathHelper.WorkspaceRoot,
            "map-cache",
            WorkspacePathHelper.SanitizeFileName(Path.GetFileName(Path.GetFullPath(context.Profile.DirectoryPath))),
            "map");
        savegameIdentifier = TryResolveSavegameIdentifier(context.Profile, context.SaveDatabasePath);
        var saveDbPath = context.SaveDatabasePath;

        if (string.IsNullOrWhiteSpace(saveDbPath) || !File.Exists(saveDbPath))
        {
            reason = "save-db-not-found";
            return false;
        }

        Directory.CreateDirectory(osqMapRoot);

        string markerPath = Path.Combine(osqMapRoot, "web", "data", "osq-map-meta.json");
        string lastSourceStamp = ReadLastExportSourceStamp(markerPath);
        int lastExportVersion = ReadLastExportVersion(markerPath);
        string currentSourceStamp = BuildSaveDbSourceStamp(saveDbPath);
        bool hasOldExport = IsUsableVsslMapExport(osqMapRoot);
        bool unchanged =
            hasOldExport &&
            lastExportVersion == MapExportSignatureVersion &&
            !string.IsNullOrWhiteSpace(lastSourceStamp) &&
            lastSourceStamp.Equals(currentSourceStamp, StringComparison.Ordinal);
        if (unchanged)
        {
            return true;
        }

        if (!TryExportMapFromSave(
                saveDbPath,
                osqMapRoot,
                context,
                now,
                out var chunkCount,
                out var exportError))
        {
            reason = string.IsNullOrWhiteSpace(exportError) ? "map-export-failed" : exportError;
            return false;
        }

        WriteOsqMapMeta(markerPath, currentSourceStamp, now, chunkCount, saveDbPath);
        return true;
    }

    private static bool IsUsableVsslMapExport(string osqMapRoot)
    {
        string webRoot = Path.Combine(osqMapRoot, "web");
        string dataDir = Path.Combine(webRoot, "data");
        string settingsPath = Path.Combine(dataDir, "settings.json");
        string overviewPath = Path.Combine(dataDir, "osq-overview.json");
        if (!File.Exists(settingsPath) || !File.Exists(overviewPath))
        {
            return false;
        }

        try
        {
            using FileStream overviewStream = File.OpenRead(overviewPath);
            using JsonDocument overviewDoc = JsonDocument.Parse(overviewStream);
            JsonElement root = overviewDoc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            if (!root.TryGetProperty("dataBase64", out JsonElement payloadNode) ||
                payloadNode.ValueKind != JsonValueKind.String)
            {
                return false;
            }

            string payload = payloadNode.GetString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(payload))
            {
                return false;
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private void FillServerMapSnapshotFromRoot(
        OsqServerMapInfo snapshot,
        string rootPath,
        string source,
        DateTimeOffset now)
    {
        snapshot.Source = source;
        snapshot.ServerMapRoot = rootPath;

        string dataDir = Path.Combine(rootPath, "web", "data");

        snapshot.ServerMapConfig = TryReadServerMapConfigForRoot(rootPath);
        snapshot.Settings = TryReadJsonFile(dataDir, "settings.json");
        snapshot.Players = TryReadJsonFile(dataDir, "players.json");
        snapshot.MarkersIndex = TryReadJsonFile(dataDir, "markers.json");
        snapshot.MarkerLayers = LoadMarkerLayerFiles(dataDir, MaxMapMarkersPerLayerInPayload);
        snapshot.OverviewData = TryReadJsonFile(dataDir, "osq-overview.json");
        snapshot.PlayersCount = EstimatePlayersCount(snapshot.Players);
        snapshot.MarkersCount = EstimateMarkersCount(snapshot.MarkerLayers);
        snapshot.ServerMapUrl = ResolveMapWebUrl(snapshot.ServerMapConfig, snapshot.Settings);
        snapshot.Tiles = ShouldIncludeServerMapTiles(source)
            ? LoadInlineMapTiles(Path.Combine(rootPath, "web", "tiles"), now)
            : [];
        snapshot.IncludeTilePayload = snapshot.Tiles.Count > 0;
        snapshot.DataDigest = ComputeMapPayloadDigest(snapshot);
    }

    private static bool ShouldIncludeServerMapTiles(string source)
    {
        return source.Contains("servermap-disk", StringComparison.OrdinalIgnoreCase);
    }

    private static void MergeServerMapDynamicData(
        OsqServerMapInfo snapshot,
        string serverMapRoot,
        DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(serverMapRoot))
        {
            return;
        }

        string dataDir = Path.Combine(serverMapRoot, "web", "data");
        if (!Directory.Exists(dataDir))
        {
            return;
        }

        JsonElement? serverMapConfig = TryReadServerMapConfigForRoot(serverMapRoot);
        if (serverMapConfig.HasValue)
        {
            snapshot.ServerMapConfig = serverMapConfig;
        }

        JsonElement? liveSettings = TryReadJsonFile(dataDir, "settings.json");
        if (liveSettings.HasValue)
        {
            snapshot.Settings = liveSettings;
        }

        JsonElement? livePlayers = TryReadJsonFile(dataDir, "players.json");
        if (livePlayers.HasValue &&
            (EstimatePlayersCount(livePlayers) > 0 ||
             !snapshot.Players.HasValue ||
             EstimatePlayersCount(snapshot.Players) == 0))
        {
            snapshot.Players = livePlayers;
        }

        JsonElement? liveMarkersIndex = TryReadJsonFile(dataDir, "markers.json");
        Dictionary<string, JsonElement> liveMarkerLayers = LoadMarkerLayerFiles(dataDir, MaxMapMarkersPerLayerInPayload);
        if (liveMarkerLayers.Count > 0)
        {
            snapshot.MarkersIndex = liveMarkersIndex ?? snapshot.MarkersIndex;
            Dictionary<string, JsonElement> merged = new(snapshot.MarkerLayers, StringComparer.OrdinalIgnoreCase);
            foreach (KeyValuePair<string, JsonElement> pair in liveMarkerLayers)
            {
                merged[pair.Key] = pair.Value;
            }

            snapshot.MarkerLayers = merged;
        }

        if (string.IsNullOrWhiteSpace(snapshot.ServerMapUrl))
        {
            snapshot.ServerMapUrl = ResolveMapWebUrl(snapshot.ServerMapConfig, snapshot.Settings);
        }

        if (!string.IsNullOrWhiteSpace(snapshot.Source) &&
            !snapshot.Source.Contains("servermap-disk", StringComparison.OrdinalIgnoreCase))
        {
            snapshot.Source += "+servermap-disk";
        }

        snapshot.DynamicDataRoot = serverMapRoot;
        List<MapTileEntry> liveTiles = LoadInlineMapTiles(Path.Combine(serverMapRoot, "web", "tiles"), now);
        if (liveTiles.Count > 0)
        {
            snapshot.IncludeTilePayload = true;
            snapshot.Tiles = liveTiles;
        }

        snapshot.GeneratedAtUtc = string.IsNullOrWhiteSpace(snapshot.GeneratedAtUtc)
            ? now.ToString("O", CultureInfo.InvariantCulture)
            : snapshot.GeneratedAtUtc;
        snapshot.PlayersCount = EstimatePlayersCount(snapshot.Players);
        snapshot.MarkersCount = EstimateMarkersCount(snapshot.MarkerLayers);
        snapshot.DataDigest = ComputeMapPayloadDigest(snapshot);
    }

    private bool TryExportMapFromSave(
        string dbPath,
        string osqMapRoot,
        LocalServerContext context,
        DateTimeOffset now,
        out int chunkCount,
        out string error)
    {
        chunkCount = 0;
        error = string.Empty;
        string stage = "init";
        string tempSnapshotDbPath = string.Empty;
        try
        {
            stage = "prepare-paths";
            string webRoot = Path.Combine(osqMapRoot, "web");
            string dataDir = Path.Combine(webRoot, "data");
            string tilesDir = Path.Combine(webRoot, "tiles", "basic");
            string markerDir = Path.Combine(dataDir, "markers");

            stage = "prepare-directories";
            Directory.CreateDirectory(dataDir);
            if (Directory.Exists(tilesDir))
            {
                Directory.Delete(tilesDir, recursive: true);
            }
            Directory.CreateDirectory(markerDir);

            stage = "snapshot-copy";
            string readableDbPath = dbPath;
            if (TryCreateSaveDbSnapshotCopy(dbPath, osqMapRoot, out string copiedDbPath, out string copyError))
            {
                readableDbPath = copiedDbPath;
                tempSnapshotDbPath = copiedDbPath;
            }
            else if (!string.IsNullOrWhiteSpace(copyError))
            {
            Emit($"[osq] map export: snapshot copy failed, fallback to live db ({copyError})");
            }

            stage = "load-mapchunks";
            int seaLevel = ResolveSeaLevel(context);
            if (!TryLoadMapChunksFromSqlite(readableDbPath, seaLevel, out List<MapChunkLightInfo> chunks, out string loadError))
            {
                error = "save-db-read-failed:" + loadError;
                return false;
            }

            stage = "no-mapchunks";
            if (chunks.Count == 0)
            {
                error = "no-mapchunks";
                WriteMapSettingsJson(dataDir, now, context, 0, 0, 0, 0, 0, 0);
                WriteMapPlayersJson(dataDir);
                WriteMapMarkersJson(dataDir, 0, 0);
                return false;
            }

            stage = "sort-chunks";
            chunkCount = chunks.Count;
            chunks.Sort((a, b) =>
            {
                int cmp = a.ChunkX.CompareTo(b.ChunkX);
                return cmp != 0 ? cmp : a.ChunkZ.CompareTo(b.ChunkZ);
            });

            stage = "calc-bounds";
            int minChunkX = chunks[0].ChunkX;
            int maxChunkX = chunks[0].ChunkX;
            int minChunkZ = chunks[0].ChunkZ;
            int maxChunkZ = chunks[0].ChunkZ;
            for (int i = 1; i < chunks.Count; i++)
            {
                MapChunkLightInfo info = chunks[i];
                if (info.ChunkX < minChunkX) minChunkX = info.ChunkX;
                if (info.ChunkX > maxChunkX) maxChunkX = info.ChunkX;
                if (info.ChunkZ < minChunkZ) minChunkZ = info.ChunkZ;
                if (info.ChunkZ > maxChunkZ) maxChunkZ = info.ChunkZ;
            }

            int centerX = (minChunkX + maxChunkX) / 2;
            int centerZ = (minChunkZ + maxChunkZ) / 2;
            int minTileX = Math.Min(FloorDiv(minChunkX, 16), FloorDiv(maxChunkX, 16));
            int maxTileX = Math.Max(FloorDiv(minChunkX, 16), FloorDiv(maxChunkX, 16));
            int minTileZ = Math.Min(FloorDiv(minChunkZ, 16), FloorDiv(maxChunkZ, 16));
            int maxTileZ = Math.Max(FloorDiv(minChunkZ, 16), FloorDiv(maxChunkZ, 16));

            stage = "write-overview";
            WriteMapOverviewJson(
                dataDir,
                chunks,
                now,
                minChunkX,
                maxChunkX,
                minChunkZ,
                maxChunkZ);

            stage = "write-settings";
            WriteMapSettingsJson(
                dataDir,
                now,
                context,
                centerX * 32,
                centerZ * 32,
                minTileX,
                maxTileX,
                minTileZ,
                maxTileZ);

            stage = "write-players";
            WriteMapPlayersJson(dataDir);

            stage = "write-markers";
            WriteMapMarkersJson(dataDir, centerX * 32, centerZ * 32);
            return true;
        }
        catch (Exception ex)
        {
            error = "map-export-stage-" + stage + ":" + ex.GetType().Name + ":" + ex.Message;
            return false;
        }
        finally
        {
            CleanupSaveDbSnapshotCopy(tempSnapshotDbPath);
        }
    }

    private static bool TryLoadMapChunksFromSqlite(
        string dbPath,
        int seaLevel,
        out List<MapChunkLightInfo> chunks,
        out string error)
    {
        chunks = [];
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(dbPath) || !File.Exists(dbPath))
        {
            error = "db-not-found";
            return false;
        }

        try
        {
            var csb = new SqliteConnectionStringBuilder
            {
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            };

            using var conn = new SqliteConnection(csb.ToString());
            conn.Open();

            var table = ResolveMapChunkTableName(conn);
            if (string.IsNullOrWhiteSpace(table))
            {
                error = "mapchunk-table-not-found";
                return false;
            }

            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"SELECT position, data FROM {table}";
            cmd.CommandTimeout = 30;

            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                long rawPos = reader.GetInt64(0);
                if (reader.IsDBNull(1))
                {
                    continue;
                }

                byte[] data = (byte[])reader.GetValue(1);
                if (data.Length == 0)
                {
                    continue;
                }

                try
                {
                    var mapChunk = Serializer.Deserialize<SavegameMapChunk>(new MemoryStream(data));
                    if (mapChunk?.RainHeightMap is null || mapChunk.RainHeightMap.Length < 1024)
                    {
                        continue;
                    }

                    (int chunkX, int chunkZ) = DecodeChunkPosition(rawPos);
                    var info = BuildMapChunkLightInfo(mapChunk, chunkX, chunkZ, seaLevel);
                    if (info is not null)
                    {
                        chunks.Add(info);
                    }
                }
                catch
                {
                    // ignore malformed chunk entries
                }
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.GetType().Name + ":" + ex.Message;
            return false;
        }
    }

    private static string ResolveMapChunkTableName(SqliteConnection conn)
    {
        var candidates = new[] { "mapchunk", "mapchunks" };
        foreach (var name in candidates)
        {
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name = $name LIMIT 1;";
            cmd.Parameters.AddWithValue("$name", name);
            var value = cmd.ExecuteScalar();
            if (value is not null && value != DBNull.Value)
            {
                return name;
            }
        }

        return string.Empty;
    }

    private static (int ChunkX, int ChunkZ) DecodeChunkPosition(long rawPosition)
    {
        ulong raw = unchecked((ulong)rawPosition);
        int chunkX = (int)raw & ChunkMask;
        int chunkZ = (int)(raw >> 27) & ChunkMask;
        return (chunkX, chunkZ);
    }

    private static MapChunkLightInfo? BuildMapChunkLightInfo(
        SavegameMapChunk mapChunk,
        int chunkX,
        int chunkZ,
        int seaLevel)
    {
        ushort[] rain = mapChunk.RainHeightMap ?? [];
        if (rain.Length < 1024)
        {
            return null;
        }

        ushort[] terrain = mapChunk.TerrainHeightMap is { Length: >= 1024 }
            ? mapChunk.TerrainHeightMap
            : rain;
        int[] topRock = mapChunk.TopRockIdMap is { Length: >= 1024 }
            ? mapChunk.TopRockIdMap
            : [];

        int[] pixels = new int[32 * 32];
        for (int z = 0; z < 32; z++)
        {
            for (int x = 0; x < 32; x++)
            {
                int index = z * 32 + x;
                int terrainY = terrain[index];
                int rainY = rain[index];
                bool isWater = terrainY <= seaLevel + 1 && rainY <= seaLevel + 2;

                int color = HeightToColor(terrainY, isWater, seaLevel);
                if (!isWater && topRock.Length >= 1024)
                {
                    color = ApplyRockTint(color, topRock[index]);
                }
                float shade = ShadeFromHeights(terrain, x, z, terrainY);
                if (rainY - terrainY > 2)
                {
                    shade *= 0.95f;
                }
                pixels[index] = ApplyShade(color, shade);
            }
        }

        return new MapChunkLightInfo(chunkX, chunkZ, pixels);
    }

    private static void BlendChunkIntoCanvas(TileCanvas canvas, int offsetX, int offsetY, MapChunkLightInfo chunk)
    {
        const int chunkSize = 32;
        for (int z = 0; z < chunkSize; z++)
        {
            int targetY = offsetY + z;
            if (targetY < 0 || targetY >= canvas.Height)
            {
                continue;
            }

            int sourceRow = z * chunkSize;
            int targetRow = targetY * canvas.Width;
            for (int x = 0; x < chunkSize; x++)
            {
                int targetX = offsetX + x;
                if (targetX < 0 || targetX >= canvas.Width)
                {
                    continue;
                }

                canvas.Pixels[targetRow + targetX] = chunk.Pixels[sourceRow + x];
            }
        }
    }

    private static void WriteZoomOutTiles(
        string rendererTilesDir,
        Dictionary<(int TileX, int TileZ), TileCanvas> baseCanvases)
    {
        if (baseCanvases.Count == 0)
        {
            return;
        }

        for (int zoom = 1; zoom <= MaxMapZoomOut; zoom++)
        {
            int step = 1 << zoom;
            var zoomCanvases = new Dictionary<(int TileX, int TileZ), TileCanvas>();

            foreach (KeyValuePair<(int TileX, int TileZ), TileCanvas> pair in baseCanvases)
            {
                int targetTileX = FloorDiv(pair.Key.TileX, step);
                int targetTileZ = FloorDiv(pair.Key.TileZ, step);
                var targetKey = (targetTileX, targetTileZ);
                if (!zoomCanvases.TryGetValue(targetKey, out TileCanvas? targetCanvas))
                {
                    targetCanvas = new TileCanvas(MapTileSize, MapTileSize);
                    zoomCanvases[targetKey] = targetCanvas;
                }

                int basePixelX = PositiveModulo(pair.Key.TileX * MapTileSize / step, MapTileSize);
                int basePixelZ = PositiveModulo(pair.Key.TileZ * MapTileSize / step, MapTileSize);
                DownsampleBaseTileIntoZoomCanvas(pair.Value, targetCanvas, basePixelX, basePixelZ, step);
            }

            string zoomDir = Path.Combine(rendererTilesDir, zoom.ToString(CultureInfo.InvariantCulture));
            Directory.CreateDirectory(zoomDir);
            foreach (KeyValuePair<(int TileX, int TileZ), TileCanvas> pair in zoomCanvases)
            {
                string filePath = Path.Combine(zoomDir, $"{pair.Key.TileX}_{pair.Key.TileZ}.png");
                byte[] png = EncodeRgbaToPng(pair.Value.Pixels, pair.Value.Width, pair.Value.Height);
                File.WriteAllBytes(filePath, png);
            }
        }
    }

    private static void DownsampleBaseTileIntoZoomCanvas(
        TileCanvas source,
        TileCanvas target,
        int basePixelX,
        int basePixelZ,
        int step)
    {
        for (int sourceZ = 0; sourceZ < source.Height; sourceZ += step)
        {
            int targetZ = basePixelZ + sourceZ / step;
            if ((uint)targetZ >= (uint)target.Height)
            {
                continue;
            }

            for (int sourceX = 0; sourceX < source.Width; sourceX += step)
            {
                int targetX = basePixelX + sourceX / step;
                if ((uint)targetX >= (uint)target.Width)
                {
                    continue;
                }

                int color = AverageTileBlock(source, sourceX, sourceZ, step);
                if (ColorA(color) == 0)
                {
                    continue;
                }

                target.Pixels[targetZ * target.Width + targetX] = color;
            }
        }
    }

    private static int AverageTileBlock(TileCanvas source, int startX, int startZ, int step)
    {
        long a = 0;
        long r = 0;
        long g = 0;
        long b = 0;
        int count = 0;

        int endX = Math.Min(source.Width, startX + step);
        int endZ = Math.Min(source.Height, startZ + step);
        for (int z = startZ; z < endZ; z++)
        {
            int row = z * source.Width;
            for (int x = startX; x < endX; x++)
            {
                int color = source.Pixels[row + x];
                int alpha = ColorA(color);
                if (alpha == 0)
                {
                    continue;
                }

                a += alpha;
                r += ColorR(color);
                g += ColorG(color);
                b += ColorB(color);
                count++;
            }
        }

        if (count == 0)
        {
            return 0;
        }

        return ToRgba((int)(a / count), (int)(r / count), (int)(g / count), (int)(b / count));
    }

    private static int PositiveModulo(int value, int divisor)
    {
        int remainder = value % divisor;
        return remainder < 0 ? remainder + divisor : remainder;
    }

    private static int HeightToColor(int y, bool isWater, int seaLevel)
    {
        if (isWater)
        {
            int depth = Math.Clamp(seaLevel - y, 0, 48);
            float alpha = depth / 48f;
            int shallow = ToRgba(255, 96, 150, 198);
            int deep = ToRgba(255, 36, 84, 156);
            return MixArgb(shallow, deep, alpha);
        }

        int rel = y - seaLevel;
        int baseColor = rel switch
        {
            < 2 => ToRgba(255, 198, 186, 130),
            < 18 => ToRgba(255, 108, 157, 96),
            < 44 => ToRgba(255, 92, 144, 86),
            < 80 => ToRgba(255, 124, 130, 106),
            < 122 => ToRgba(255, 138, 138, 138),
            _ => ToRgba(255, 236, 236, 236)
        };

        int jitter = (y * 13) & 15;
        int sat = Math.Clamp(170 - rel / 3, 35, 180);
        int val = Math.Clamp(132 + jitter * 4, 120, 220);
        int tint = HsvToRgba((92 + rel / 4) & 255, sat, val);
        float tintAlpha = rel > 80 ? 0.12f : 0.2f;
        return MixArgb(baseColor, tint, tintAlpha);
    }

    private static float ShadeFromHeights(ushort[] rain, int x, int z, int current)
    {
        int west = GetHeight(rain, x - 1, z, current);
        int north = GetHeight(rain, x, z - 1, current);
        int northwest = GetHeight(rain, x - 1, z - 1, current);
        int direction = Math.Sign(current - west) + Math.Sign(current - north) + Math.Sign(current - northwest);
        int steepness = Math.Max(Math.Max(Math.Abs(current - west), Math.Abs(current - north)), Math.Abs(current - northwest));
        float slopeFactor = Math.Min(0.5f, steepness / 10f) / 1.25f;
        return direction switch
        {
            > 0 => 1.08f + slopeFactor,
            < 0 => 0.92f - slopeFactor,
            _ => 1f
        };
    }

    private static int GetHeight(ushort[] rain, int x, int z, int fallback)
    {
        if ((uint)x >= 32u || (uint)z >= 32u)
        {
            return fallback;
        }

        return rain[z * 32 + x];
    }

    private static int ApplyShade(int color, float shade)
    {
        shade = Math.Clamp(shade, 0.5f, 1.6f);
        int r = Math.Clamp((int)(ColorR(color) * shade), 0, 255);
        int g = Math.Clamp((int)(ColorG(color) * shade), 0, 255);
        int b = Math.Clamp((int)(ColorB(color) * shade), 0, 255);
        return ToRgba(255, r, g, b);
    }

    private static int ApplyRockTint(int baseColor, int topRockId)
    {
        if (topRockId <= 0)
        {
            return baseColor;
        }

        int hash = unchecked(topRockId * 1103515245 + 12345);
        int hue = PositiveModulo(hash >> 4, 256);
        int sat = 58 + PositiveModulo(hash >> 9, 50);
        int val = 92 + PositiveModulo(hash >> 15, 52);
        int tint = HsvToRgba(hue, sat, val);
        return MixArgb(baseColor, tint, 0.2f);
    }

    private static int MixArgb(int baseColor, int overlayColor, float alpha)
    {
        alpha = Math.Clamp(alpha, 0f, 1f);
        float inv = 1f - alpha;
        int r = (int)(ColorR(baseColor) * inv + ColorR(overlayColor) * alpha);
        int g = (int)(ColorG(baseColor) * inv + ColorG(overlayColor) * alpha);
        int b = (int)(ColorB(baseColor) * inv + ColorB(overlayColor) * alpha);
        return ToRgba(255, r, g, b);
    }

    private static int ToRgba(int a, int r, int g, int b)
    {
        return ((a & 255) << 24) | ((r & 255) << 16) | ((g & 255) << 8) | (b & 255);
    }

    private static byte ColorA(int color) => (byte)((color >> 24) & 255);
    private static byte ColorR(int color) => (byte)((color >> 16) & 255);
    private static byte ColorG(int color) => (byte)((color >> 8) & 255);
    private static byte ColorB(int color) => (byte)(color & 255);

    private static int HsvToRgba(int h, int s, int v)
    {
        float hf = (Math.Clamp(h, 0, 255) / 255f) * 360f;
        float sf = Math.Clamp(s, 0, 255) / 255f;
        float vf = Math.Clamp(v, 0, 255) / 255f;

        float c = vf * sf;
        float x = c * (1f - Math.Abs((hf / 60f) % 2f - 1f));
        float m = vf - c;

        float r1;
        float g1;
        float b1;
        if (hf < 60f)
        {
            r1 = c;
            g1 = x;
            b1 = 0;
        }
        else if (hf < 120f)
        {
            r1 = x;
            g1 = c;
            b1 = 0;
        }
        else if (hf < 180f)
        {
            r1 = 0;
            g1 = c;
            b1 = x;
        }
        else if (hf < 240f)
        {
            r1 = 0;
            g1 = x;
            b1 = c;
        }
        else if (hf < 300f)
        {
            r1 = x;
            g1 = 0;
            b1 = c;
        }
        else
        {
            r1 = c;
            g1 = 0;
            b1 = x;
        }

        int r = Math.Clamp((int)Math.Round((r1 + m) * 255f), 0, 255);
        int g = Math.Clamp((int)Math.Round((g1 + m) * 255f), 0, 255);
        int b = Math.Clamp((int)Math.Round((b1 + m) * 255f), 0, 255);
        return ToRgba(255, r, g, b);
    }

    private static bool TryCreateSaveDbSnapshotCopy(string sourceDbPath, string osqMapRoot, out string snapshotDbPath, out string error)
    {
        snapshotDbPath = string.Empty;
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(sourceDbPath) || !File.Exists(sourceDbPath))
        {
            error = "source-db-not-found";
            return false;
        }

        if (string.IsNullOrWhiteSpace(osqMapRoot))
        {
            error = "osq-map-root-unavailable";
            return false;
        }

        try
        {
            string tmpRoot = Path.Combine(osqMapRoot, "tmp");
            Directory.CreateDirectory(tmpRoot);
            string fileName = $"vssl-map-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.vcdbs";
            snapshotDbPath = Path.Combine(tmpRoot, fileName);

            File.Copy(sourceDbPath, snapshotDbPath, overwrite: true);
            CopyFileIfExists(sourceDbPath + "-wal", snapshotDbPath + "-wal");
            CopyFileIfExists(sourceDbPath + "-shm", snapshotDbPath + "-shm");
            return true;
        }
        catch (Exception ex)
        {
            snapshotDbPath = string.Empty;
            error = ex.Message;
            return false;
        }
    }

    private static void CleanupSaveDbSnapshotCopy(string snapshotDbPath)
    {
        if (string.IsNullOrWhiteSpace(snapshotDbPath))
        {
            return;
        }

        TryDeleteFileQuietly(snapshotDbPath);
        TryDeleteFileQuietly(snapshotDbPath + "-wal");
        TryDeleteFileQuietly(snapshotDbPath + "-shm");
    }

    private static void CopyFileIfExists(string sourcePath, string targetPath)
    {
        if (!File.Exists(sourcePath))
        {
            return;
        }

        File.Copy(sourcePath, targetPath, overwrite: true);
    }

    private static void TryDeleteFileQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignore
        }
    }

    private static string BuildSaveDbSourceStamp(string saveDbPath)
    {
        StringBuilder sb = new();
        AppendFileStamp(sb, "db", saveDbPath);
        AppendFileStamp(sb, "wal", saveDbPath + "-wal");
        AppendFileStamp(sb, "shm", saveDbPath + "-shm");
        return sb.ToString();
    }

    private static void AppendFileStamp(StringBuilder sb, string tag, string filePath)
    {
        sb.Append(tag).Append('=');
        if (!File.Exists(filePath))
        {
            sb.Append("missing;");
            return;
        }

        try
        {
            FileInfo info = new(filePath);
            long ticks = info.LastWriteTimeUtc.Ticks;
            long length = info.Length;
            sb.Append(ticks.ToString(CultureInfo.InvariantCulture))
                .Append(':')
                .Append(length.ToString(CultureInfo.InvariantCulture))
                .Append(';');
        }
        catch
        {
            sb.Append("error;");
        }
    }

    private static string ReadLastExportSourceStamp(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return string.Empty;
            }

            string text = File.ReadAllText(metaPath);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("sourceStamp", out JsonElement node) || node.ValueKind != JsonValueKind.String)
            {
                return string.Empty;
            }

            return (node.GetString() ?? string.Empty).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static int ReadLastExportVersion(string metaPath)
    {
        try
        {
            if (!File.Exists(metaPath))
            {
                return 0;
            }

            string text = File.ReadAllText(metaPath);
            using JsonDocument doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("exportVersion", out JsonElement node))
            {
                return 0;
            }

            return node.ValueKind switch
            {
                JsonValueKind.Number when node.TryGetInt32(out int n) => n,
                JsonValueKind.String when int.TryParse(node.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int n) => n,
                _ => 0
            };
        }
        catch
        {
            return 0;
        }
    }

    private static void WriteOsqMapMeta(string metaPath, string sourceStamp, DateTimeOffset now, int chunkCount, string dbPath)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(metaPath) ?? string.Empty);
            using FileStream stream = File.Create(metaPath);
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });
            DateTime dbWriteUtc = File.Exists(dbPath) ? File.GetLastWriteTimeUtc(dbPath) : DateTime.MinValue;
            writer.WriteStartObject();
            writer.WriteNumber("exportVersion", MapExportSignatureVersion);
            writer.WriteString("sourceStamp", sourceStamp ?? string.Empty);
            writer.WriteString("dbWriteUtc", dbWriteUtc == DateTime.MinValue ? string.Empty : dbWriteUtc.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("generatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteNumber("chunkCount", chunkCount);
            writer.WriteString("dbPath", dbPath ?? string.Empty);
            writer.WriteEndObject();
        }
        catch
        {
            // ignore
        }
    }

    private static void WriteMapSettingsJson(
        string dataDir,
        DateTimeOffset now,
        LocalServerContext context,
        int spawnX,
        int spawnZ,
        int minTileX,
        int maxTileX,
        int minTileZ,
        int maxTileZ)
    {
        string path = Path.Combine(dataDir, "settings.json");
        using FileStream stream = File.Create(path);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteBoolean("friendlyUrls", false);
        writer.WriteBoolean("playerList", true);
        writer.WriteBoolean("playerMarkers", true);
        writer.WriteNumber("maxPlayers", Math.Max(0, context.ServerSettings.MaxClients));
        writer.WriteNumber("interval", 10);
        writer.WritePropertyName("size");
        writer.WriteStartArray();
        writer.WriteNumberValue(Math.Max(1, context.MapSizeX));
        writer.WriteNumberValue(Math.Max(1, context.MapSizeZ));
        writer.WriteEndArray();
        writer.WritePropertyName("spawn");
        writer.WriteStartArray();
        writer.WriteNumberValue(spawnX);
        writer.WriteNumberValue(spawnZ);
        writer.WriteEndArray();
        writer.WritePropertyName("web");
        writer.WriteStartObject();
        writer.WriteString("tiletype", "png");
        writer.WriteEndObject();
        writer.WritePropertyName("zoom");
        writer.WriteStartObject();
        writer.WriteNumber("def", ChooseDefaultMapZoom(minTileX, maxTileX, minTileZ, maxTileZ));
        writer.WriteNumber("maxin", -3);
        writer.WriteNumber("maxout", MaxMapZoomOut);
        writer.WriteEndObject();
        writer.WritePropertyName("renderers");
        writer.WriteStartArray();
        writer.WriteStartObject();
        writer.WriteString("id", "basic");
        writer.WriteString("icon", "basic");
        writer.WriteEndObject();
        writer.WriteEndArray();
        writer.WritePropertyName("osq");
        writer.WriteStartObject();
        writer.WriteString("standard", "osq-server-map-v1");
        writer.WriteString("generatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
        writer.WriteString("source", "vssl-native-vcdb");
        writer.WritePropertyName("tileBounds");
        writer.WriteStartObject();
        writer.WriteNumber("minX", minTileX);
        writer.WriteNumber("maxX", maxTileX);
        writer.WriteNumber("minZ", minTileZ);
        writer.WriteNumber("maxZ", maxTileZ);
        writer.WriteEndObject();
        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static int ChooseDefaultMapZoom(int minTileX, int maxTileX, int minTileZ, int maxTileZ)
    {
        int tileWidth = Math.Max(1, maxTileX - minTileX + 1);
        int tileHeight = Math.Max(1, maxTileZ - minTileZ + 1);
        double requiredScale = Math.Max(tileWidth / 3.0, tileHeight / 2.0);
        if (requiredScale <= 1)
        {
            return 0;
        }

        int zoom = (int)Math.Ceiling(Math.Log(requiredScale, 2));
        return Math.Clamp(zoom, 0, MaxMapZoomOut);
    }

    private static void WriteMapPlayersJson(string dataDir)
    {
        using FileStream stream = File.Create(Path.Combine(dataDir, "players.json"));
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WriteNumber("interval", 10);
        writer.WriteBoolean("hidden", false);
        writer.WritePropertyName("players");
        writer.WriteStartArray();
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteMapMarkersJson(string dataDir, int spawnX, int spawnZ)
    {
        string markersRoot = Path.Combine(dataDir, "markers");
        Directory.CreateDirectory(markersRoot);

        string indexPath = Path.Combine(dataDir, "markers.json");
        using FileStream stream = File.Create(indexPath);
        using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });
        writer.WriteStartObject();
        writer.WritePropertyName("markers");
        writer.WriteStartArray();
        writer.WriteStringValue("spawn");
        writer.WriteEndArray();
        writer.WriteEndObject();

        string spawnPath = Path.Combine(markersRoot, "spawn.json");
        using FileStream spawnStream = File.Create(spawnPath);
        using Utf8JsonWriter spawnWriter = new(spawnStream, new JsonWriterOptions { Indented = false });
        spawnWriter.WriteStartObject();
        spawnWriter.WriteString("id", "spawn");
        spawnWriter.WriteString("label", "Spawn");
        spawnWriter.WriteNumber("interval", 30);
        spawnWriter.WriteBoolean("hidden", false);
        spawnWriter.WritePropertyName("markers");
        spawnWriter.WriteStartArray();
        spawnWriter.WriteStartObject();
        spawnWriter.WriteString("type", "icon");
        spawnWriter.WriteString("id", "servermap:spawn");
        spawnWriter.WritePropertyName("point");
        spawnWriter.WriteStartArray();
        spawnWriter.WriteNumberValue(spawnX);
        spawnWriter.WriteNumberValue(spawnZ);
        spawnWriter.WriteEndArray();
        spawnWriter.WritePropertyName("options");
        spawnWriter.WriteStartObject();
        spawnWriter.WriteString("title", "Spawn");
        spawnWriter.WriteString("alt", "Spawn");
        spawnWriter.WriteString("iconUrl", "#svg-house");
        spawnWriter.WritePropertyName("iconSize");
        spawnWriter.WriteStartArray();
        spawnWriter.WriteNumberValue(16);
        spawnWriter.WriteNumberValue(16);
        spawnWriter.WriteEndArray();
        spawnWriter.WritePropertyName("iconAnchor");
        spawnWriter.WriteStartArray();
        spawnWriter.WriteNumberValue(8);
        spawnWriter.WriteNumberValue(16);
        spawnWriter.WriteEndArray();
        spawnWriter.WriteEndObject();
        spawnWriter.WritePropertyName("tooltip");
        spawnWriter.WriteStartObject();
        spawnWriter.WriteString("direction", "top");
        spawnWriter.WriteString("content", "Spawn");
        spawnWriter.WriteEndObject();
        spawnWriter.WriteEndObject();
        spawnWriter.WriteEndArray();
        spawnWriter.WriteEndObject();
    }

    private static void WriteMapOverviewJson(
        string dataDir,
        List<MapChunkLightInfo> chunks,
        DateTimeOffset now,
        int minChunkX,
        int maxChunkX,
        int minChunkZ,
        int maxChunkZ)
    {
        string path = Path.Combine(dataDir, "osq-overview.json");
        try
        {
            const int chunkPixelSize = 32;
            const int pixelBytesPerChunk = chunkPixelSize * chunkPixelSize * 4;
            const int recordSize = 8 + pixelBytesPerChunk;

            long rawLengthLong = (long)Math.Max(0, chunks.Count) * recordSize;
            if (rawLengthLong <= 0 || rawLengthLong > int.MaxValue)
            {
                return;
            }

            byte[] raw = new byte[(int)rawLengthLong];
            int offset = 0;
            foreach (MapChunkLightInfo chunk in chunks)
            {
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset, 4), chunk.ChunkX);
                offset += 4;
                BinaryPrimitives.WriteInt32LittleEndian(raw.AsSpan(offset, 4), chunk.ChunkZ);
                offset += 4;
                for (int i = 0; i < chunkPixelSize * chunkPixelSize; i++)
                {
                    int color = i < chunk.Pixels.Length ? chunk.Pixels[i] : 0;
                    raw[offset++] = ColorR(color);
                    raw[offset++] = ColorG(color);
                    raw[offset++] = ColorB(color);
                    raw[offset++] = ColorA(color);
                }
            }

            byte[] compressed;
            using (MemoryStream ms = new())
            {
                using (ZLibStream zlib = new(ms, CompressionLevel.SmallestSize, leaveOpen: true))
                {
                    zlib.Write(raw, 0, raw.Length);
                }

                compressed = ms.ToArray();
            }

            string dataBase64 = Convert.ToBase64String(compressed);
            using FileStream stream = File.Create(path);
            using Utf8JsonWriter writer = new(stream, new JsonWriterOptions { Indented = false });
            writer.WriteStartObject();
            writer.WriteString("standard", "osq-map-overview-v1");
            writer.WriteString("format", "chunk-rgba32-binary-v1");
            writer.WriteString("encoding", "base64");
            writer.WriteString("compression", "zlib");
            writer.WriteNumber("chunkSize", chunkPixelSize);
            writer.WriteNumber("chunkCount", chunks.Count);
            writer.WriteNumber("minChunkX", minChunkX);
            writer.WriteNumber("maxChunkX", maxChunkX);
            writer.WriteNumber("minChunkZ", minChunkZ);
            writer.WriteNumber("maxChunkZ", maxChunkZ);
            writer.WriteString("generatedAtUtc", now.ToString("O", CultureInfo.InvariantCulture));
            writer.WriteString("dataBase64", dataBase64);
            writer.WriteEndObject();
        }
        catch
        {
            // ignore overview write failures
        }
    }

    private static int FloorDiv(int value, int divisor)
    {
        int q = value / divisor;
        int r = value % divisor;
        if (r != 0 && ((value ^ divisor) < 0))
        {
            q--;
        }

        return q;
    }

    private static byte[] EncodeRgbaToPng(int[] argbPixels, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            return [];
        }

        int rawStride = width * 4 + 1;
        byte[] raw = new byte[rawStride * height];
        int dst = 0;
        for (int y = 0; y < height; y++)
        {
            raw[dst++] = 0;
            int rowStart = y * width;
            for (int x = 0; x < width; x++)
            {
                int color = argbPixels[rowStart + x];
                raw[dst++] = ColorR(color);
                raw[dst++] = ColorG(color);
                raw[dst++] = ColorB(color);
                raw[dst++] = ColorA(color);
            }
        }

        byte[] compressed;
        using (MemoryStream ms = new())
        {
            using (ZLibStream zlib = new(ms, CompressionLevel.SmallestSize, leaveOpen: true))
            {
                zlib.Write(raw, 0, raw.Length);
            }

            compressed = ms.ToArray();
        }

        using MemoryStream png = new();
        png.Write([137, 80, 78, 71, 13, 10, 26, 10]);
        WritePngChunk(png, "IHDR", BuildIhdrData(width, height));
        WritePngChunk(png, "IDAT", compressed);
        WritePngChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static byte[] BuildIhdrData(int width, int height)
    {
        byte[] ihdr = new byte[13];
        WriteBigEndianInt32(ihdr, 0, width);
        WriteBigEndianInt32(ihdr, 4, height);
        ihdr[8] = 8;
        ihdr[9] = 6;
        ihdr[10] = 0;
        ihdr[11] = 0;
        ihdr[12] = 0;
        return ihdr;
    }

    private static void WritePngChunk(Stream stream, string type, byte[] data)
    {
        byte[] chunkType = Encoding.ASCII.GetBytes(type);
        WriteBigEndianUInt32(stream, (uint)(data?.Length ?? 0));
        stream.Write(chunkType, 0, 4);
        if (data is { Length: > 0 })
        {
            stream.Write(data, 0, data.Length);
        }

        uint crc = ComputePngCrc(chunkType, data ?? []);
        WriteBigEndianUInt32(stream, crc);
    }

    private static uint ComputePngCrc(byte[] type, byte[] data)
    {
        uint crc = 0xFFFFFFFFu;
        for (int i = 0; i < type.Length; i++)
        {
            crc = PngCrcTable[(crc ^ type[i]) & 0xFF] ^ (crc >> 8);
        }
        for (int i = 0; i < data.Length; i++)
        {
            crc = PngCrcTable[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
        }

        return crc ^ 0xFFFFFFFFu;
    }

    private static uint[] BuildPngCrcTable()
    {
        uint[] table = new uint[256];
        for (uint n = 0; n < 256; n++)
        {
            uint c = n;
            for (int k = 0; k < 8; k++)
            {
                c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            }
            table[n] = c;
        }

        return table;
    }

    private static void WriteBigEndianInt32(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)((value >> 24) & 0xFF);
        buffer[offset + 1] = (byte)((value >> 16) & 0xFF);
        buffer[offset + 2] = (byte)((value >> 8) & 0xFF);
        buffer[offset + 3] = (byte)(value & 0xFF);
    }

    private static void WriteBigEndianUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)((value >> 24) & 0xFF));
        stream.WriteByte((byte)((value >> 16) & 0xFF));
        stream.WriteByte((byte)((value >> 8) & 0xFF));
        stream.WriteByte((byte)(value & 0xFF));
    }

    private static JsonElement? TryReadJsonFile(string dataDir, string fileName)
    {
        try
        {
            string fullPath = Path.Combine(dataDir, fileName);
            if (!File.Exists(fullPath))
            {
                return null;
            }

            string text = File.ReadAllText(fullPath);
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryReadJsonFileWithPath(string fullPath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
            {
                return null;
            }

            string text = File.ReadAllText(fullPath);
            using JsonDocument doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryReadServerMapConfigForRoot(string serverMapRoot)
    {
        foreach (string candidate in EnumerateServerMapConfigCandidates(serverMapRoot))
        {
            JsonElement? config = TryReadJsonFileWithPath(candidate);
            if (config.HasValue)
            {
                return config;
            }
        }

        return null;
    }

    private static IEnumerable<string> EnumerateServerMapConfigCandidates(string serverMapRoot)
    {
        var roots = new List<string>();
        try
        {
            var rootInfo = new DirectoryInfo(Path.GetFullPath(serverMapRoot));
            var profileRoot = rootInfo.Parent?.Parent?.Parent?.FullName;
            if (!string.IsNullOrWhiteSpace(profileRoot))
            {
                roots.Add(Path.Combine(profileRoot, "ModConfig"));
            }

            var legacyRoot = rootInfo.Parent?.FullName;
            if (!string.IsNullOrWhiteSpace(legacyRoot))
            {
                roots.Add(Path.Combine(legacyRoot, "ModConfig"));
            }
        }
        catch
        {
            // ignore malformed roots
        }

        foreach (string root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            yield return Path.Combine(root, "servermap.json");
            yield return Path.Combine(root, "livemap.json");
        }
    }

    private static Dictionary<string, JsonElement> LoadMarkerLayerFiles(string dataDir, int maxMarkersInPayload)
    {
        Dictionary<string, JsonElement> map = new(StringComparer.OrdinalIgnoreCase);

        string markerDir = Path.Combine(dataDir, "markers");
        if (!Directory.Exists(markerDir))
        {
            return map;
        }

        int safeMax = Math.Max(16, maxMarkersInPayload);
        string[] markerFiles = Directory.GetFiles(markerDir, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
            .Take(MaxMarkerLayersInPayload)
            .ToArray();

        foreach (string file in markerFiles)
        {
            try
            {
                string text = File.ReadAllText(file);
                using JsonDocument doc = JsonDocument.Parse(text);
                JsonElement root = doc.RootElement.Clone();

                if (root.ValueKind == JsonValueKind.Object &&
                    root.TryGetProperty("markers", out JsonElement markersProp) &&
                    markersProp.ValueKind == JsonValueKind.Array &&
                    markersProp.GetArrayLength() > safeMax)
                {
                    map[Path.GetFileNameWithoutExtension(file) ?? file] = TrimLayerMarkers(root, safeMax);
                }
                else
                {
                    map[Path.GetFileNameWithoutExtension(file) ?? file] = root;
                }
            }
            catch
            {
                // ignore malformed marker file
            }
        }

        return map;
    }

    private static JsonElement TrimLayerMarkers(JsonElement root, int maxMarkers)
    {
        using MemoryStream ms = new();
        using (Utf8JsonWriter writer = new(ms))
        {
            writer.WriteStartObject();
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (property.NameEquals("markers") && property.Value.ValueKind == JsonValueKind.Array)
                {
                    writer.WritePropertyName("markers");
                    writer.WriteStartArray();
                    int emitted = 0;
                    foreach (JsonElement item in property.Value.EnumerateArray())
                    {
                        if (emitted >= maxMarkers)
                        {
                            break;
                        }

                        item.WriteTo(writer);
                        emitted++;
                    }

                    writer.WriteEndArray();
                }
                else
                {
                    property.WriteTo(writer);
                }
            }

            writer.WriteEndObject();
        }

        ms.Position = 0;
        using JsonDocument doc = JsonDocument.Parse(ms);
        return doc.RootElement.Clone();
    }

    private static int EstimatePlayersCount(JsonElement? playersRoot)
    {
        if (!playersRoot.HasValue || playersRoot.Value.ValueKind != JsonValueKind.Object)
        {
            return 0;
        }

        if (!playersRoot.Value.TryGetProperty("players", out JsonElement arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        return arr.GetArrayLength();
    }

    private static int EstimateMarkersCount(Dictionary<string, JsonElement> markerLayers)
    {
        int total = 0;
        foreach (KeyValuePair<string, JsonElement> pair in markerLayers)
        {
            JsonElement layer = pair.Value;
            if (layer.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            if (!layer.TryGetProperty("markers", out JsonElement markers) || markers.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            total += markers.GetArrayLength();
        }

        return total;
    }

    private static List<MapTileEntry> LoadInlineMapTiles(string tilesRoot, DateTimeOffset now)
    {
        List<MapTileEntry> result = [];
        if (!Directory.Exists(tilesRoot))
        {
            return result;
        }

        var orderedFiles = Directory
            .EnumerateFiles(tilesRoot, "*.*", SearchOption.AllDirectories)
            .Where(path => SupportedMapTileExtensions.Contains(Path.GetExtension(path) ?? string.Empty))
            .Select(path =>
            {
                string rel = BuildRelativePath(tilesRoot, path);
                ParseTilePathMetadata(rel, out _, out int? zoom, out _);
                DateTime writeTime;
                try
                {
                    writeTime = File.GetLastWriteTimeUtc(path);
                }
                catch
                {
                    writeTime = DateTime.MinValue;
                }
                return new
                {
                    Path = path,
                    RelativePath = rel,
                    Zoom = zoom ?? -1,
                    LastWriteUtc = writeTime
                };
            })
            .OrderByDescending(entry => entry.Zoom)
            .ThenBy(entry => entry.RelativePath, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(entry => entry.LastWriteUtc)
            .ToList();

        long rotationSeed = now.ToUnixTimeSeconds() / Math.Max(1, PushIntervalSec);
        var detailPriorityFiles = orderedFiles
            .Where(entry => entry.Zoom >= 0 && entry.Zoom <= 1)
            .ToList();
        var otherFiles = orderedFiles
            .Where(entry => entry.Zoom < 0 || entry.Zoom > 1)
            .ToList();

        var files = detailPriorityFiles;
        if (detailPriorityFiles.Count >= MaxInlineMapTilesInPayload)
        {
            files = SelectRotatingMapTileWindow(
                detailPriorityFiles,
                MaxInlineMapTilesInPayload,
                rotationSeed);
        }
        else
        {
            int remainingSlots = MaxInlineMapTilesInPayload - detailPriorityFiles.Count;
            var rotatedOthers = SelectRotatingMapTileWindow(otherFiles, remainingSlots, rotationSeed);
            files.AddRange(rotatedOthers);
        }

        foreach (var fileEntry in files)
        {
            string file = fileEntry.Path;
            FileInfo info;
            try
            {
                info = new FileInfo(file);
                if (!info.Exists)
                {
                    continue;
                }
            }
            catch
            {
                continue;
            }

            string ext = Path.GetExtension(info.Name) ?? string.Empty;
            string mimeType = ResolveImageMimeType(ext);
            string rel = fileEntry.RelativePath;
            ParseTilePathMetadata(rel, out string renderer, out int? zoom, out string tileKey);

            bool contentIncluded = false;
            string skippedReason = string.Empty;
            string base64 = string.Empty;
            if (info.Length > MaxMapTileBytesPerFile)
            {
                skippedReason = "file-too-large";
            }
            else
            {
                try
                {
                    byte[] bytes = File.ReadAllBytes(info.FullName);
                    base64 = Convert.ToBase64String(bytes);
                    contentIncluded = true;
                }
                catch
                {
                    skippedReason = "read-failed";
                }
            }

            result.Add(new MapTileEntry
            {
                RelativePath = rel,
                FileName = info.Name,
                Renderer = renderer,
                Zoom = zoom,
                Tile = tileKey,
                MimeType = mimeType,
                SizeBytes = info.Length,
                LastWriteUtc = info.LastWriteTimeUtc == DateTime.MinValue
                    ? now.ToString("O", CultureInfo.InvariantCulture)
                    : info.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture),
                Sha256 = ComputeFileSha256(info.FullName),
                ContentIncluded = contentIncluded,
                ContentEncoding = contentIncluded ? "base64" : string.Empty,
                DataBase64 = base64,
                SkippedReason = skippedReason
            });
        }

        return result;
    }

    private static List<T> SelectRotatingMapTileWindow<T>(List<T> orderedEntries, int maxCount, long seed)
    {
        if (orderedEntries.Count <= maxCount)
        {
            return orderedEntries;
        }

        int windowCount = (int)Math.Ceiling(orderedEntries.Count / (double)maxCount);
        int windowIndex = (int)((seed < 0 ? -seed : seed) % windowCount);
        int offset = windowIndex * maxCount;
        List<T> selected = orderedEntries
            .Skip(offset)
            .Take(maxCount)
            .ToList();
        if (selected.Count < maxCount)
        {
            selected.AddRange(orderedEntries.Take(maxCount - selected.Count));
        }

        return selected;
    }

    private static void ParseTilePathMetadata(string relativePath, out string renderer, out int? zoom, out string tile)
    {
        renderer = string.Empty;
        zoom = null;
        tile = string.Empty;

        if (string.IsNullOrWhiteSpace(relativePath))
        {
            return;
        }

        string[] parts = relativePath.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 1)
        {
            renderer = parts[0];
        }
        if (parts.Length >= 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsedZoom))
        {
            zoom = parsedZoom;
        }
        if (parts.Length >= 3)
        {
            string fileName = parts[2];
            tile = Path.GetFileNameWithoutExtension(fileName) ?? string.Empty;
        }
    }

    private static string BuildRelativePath(string rootPath, string fullPath)
    {
        try
        {
            return Path.GetRelativePath(rootPath, fullPath).Replace('\\', '/');
        }
        catch
        {
            return fullPath.Replace('\\', '/');
        }
    }

    private static string ResolveMapWebUrl(JsonElement? serverMapConfig, JsonElement? settingsRoot)
    {
        string fromConfig = TryReadServerMapUrl(serverMapConfig);
        if (!string.IsNullOrWhiteSpace(fromConfig))
        {
            return fromConfig;
        }

        if (settingsRoot.HasValue &&
            settingsRoot.Value.ValueKind == JsonValueKind.Object &&
            settingsRoot.Value.TryGetProperty("web", out JsonElement web) &&
            web.ValueKind == JsonValueKind.Object &&
            web.TryGetProperty("url", out JsonElement urlNode) &&
            urlNode.ValueKind == JsonValueKind.String)
        {
            string value = (urlNode.GetString() ?? string.Empty).Trim();
            if (value.Length > 0)
            {
                return value.TrimEnd('/');
            }
        }

        return string.Empty;
    }

    private static string TryReadServerMapUrl(JsonElement? serverMapConfigRoot)
    {
        if (!serverMapConfigRoot.HasValue || serverMapConfigRoot.Value.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        JsonElement root = serverMapConfigRoot.Value;
        if (!root.TryGetProperty("web", out JsonElement webNode) || webNode.ValueKind != JsonValueKind.Object)
        {
            return string.Empty;
        }

        if (!webNode.TryGetProperty("url", out JsonElement urlNode) || urlNode.ValueKind != JsonValueKind.String)
        {
            return string.Empty;
        }

        string url = (urlNode.GetString() ?? string.Empty).Trim();
        return url.Length == 0 ? string.Empty : url.TrimEnd('/');
    }

    private static string ComputeMapPayloadDigest(OsqServerMapInfo snapshot)
    {
        using SHA256 sha = SHA256.Create();
        StringBuilder sb = new();
        sb.Append(snapshot.SavegameIdentifier ?? string.Empty).Append('|');
        sb.Append(snapshot.GeneratedAtUtc ?? string.Empty).Append('|');
        sb.Append(snapshot.ServerMapUrl ?? string.Empty).Append('|');
        sb.Append(snapshot.PlayersCount.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.MarkersCount.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.MarkerLayers.Count.ToString(CultureInfo.InvariantCulture)).Append('|');
        sb.Append(snapshot.Tiles.Count.ToString(CultureInfo.InvariantCulture));
        if (snapshot.OverviewData.HasValue && snapshot.OverviewData.Value.ValueKind != JsonValueKind.Undefined)
        {
            sb.Append('|');
            sb.Append(snapshot.OverviewData.Value.GetRawText());
        }

        foreach (MapTileEntry tile in snapshot.Tiles)
        {
            sb.Append('|');
            sb.Append(tile.RelativePath ?? string.Empty);
            sb.Append(':');
            sb.Append(tile.Sha256 ?? string.Empty);
        }

        byte[] bytes = Encoding.UTF8.GetBytes(sb.ToString());
        byte[] hash = sha.ComputeHash(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeFileSha256(string fullPath)
    {
        try
        {
            using var stream = File.OpenRead(fullPath);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(stream);
            return Convert.ToHexString(hash).ToLowerInvariant();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static ServerMapRootCandidate? TryFindServerMapRoot(string profileDataPath)
    {
        string modDataRoot = Path.Combine(Path.GetFullPath(profileDataPath), "ModData");
        if (!Directory.Exists(modDataRoot))
        {
            return null;
        }

        DateTime latestUtc = DateTime.MinValue;
        ServerMapRootCandidate? candidate = null;

        foreach (var savegameDir in Directory.EnumerateDirectories(modDataRoot))
        {
            var savegameIdentifier = Path.GetFileName(savegameDir) ?? string.Empty;
            if (string.IsNullOrWhiteSpace(savegameIdentifier))
            {
                continue;
            }

            foreach (string mapDataName in MapDataDirectoryNames)
            {
                var serverMapDir = Path.Combine(savegameDir, mapDataName);
                var settingsPath = Path.Combine(serverMapDir, "web", "data", "settings.json");
                if (!File.Exists(settingsPath))
                {
                    continue;
                }

                DateTime writeUtc;
                try
                {
                    writeUtc = File.GetLastWriteTimeUtc(settingsPath);
                }
                catch
                {
                    writeUtc = DateTime.MinValue;
                }

                if (writeUtc >= latestUtc)
                {
                    latestUtc = writeUtc;
                    candidate = new ServerMapRootCandidate
                    {
                        RootPath = serverMapDir,
                        SavegameIdentifier = savegameIdentifier
                    };
                }
            }
        }

        return candidate;
    }

    private static (int MapSizeX, int MapSizeY, int MapSizeZ) ResolveMapSizes(JsonObject? configRoot, WorldSettings worldSettings)
    {
        int mapSizeX = ReadIntNode(configRoot?["MapSizeX"], 1024000);
        int mapSizeZ = ReadIntNode(configRoot?["MapSizeZ"], 1024000);
        int mapSizeY = worldSettings.WorldHeight ?? ReadIntNode(configRoot?["MapSizeY"], 256);

        var worldConfig = configRoot?["WorldConfig"] as JsonObject;
        var worldRules = worldConfig?["WorldConfiguration"] as JsonObject;
        mapSizeX = ReadIntNode(worldRules?["worldWidth"], mapSizeX);
        mapSizeZ = ReadIntNode(worldRules?["worldLength"], mapSizeZ);
        mapSizeY = ReadIntNode(worldRules?["worldHeight"], mapSizeY);
        mapSizeY = ReadIntNode(worldConfig?["MapSizeY"], mapSizeY);

        mapSizeX = mapSizeX <= 0 ? 1024000 : mapSizeX;
        mapSizeY = mapSizeY <= 0 ? 256 : mapSizeY;
        mapSizeZ = mapSizeZ <= 0 ? 1024000 : mapSizeZ;
        return (mapSizeX, mapSizeY, mapSizeZ);
    }

    private static int ResolveSeaLevel(LocalServerContext context)
    {
        const int fallback = 110;
        JsonObject? configRoot = context.RawConfigRoot;

        int seaLevel = ReadIntNode(configRoot?["SeaLevel"], fallback);

        JsonObject? worldConfig = configRoot?["WorldConfig"] as JsonObject;
        JsonObject? worldRules = worldConfig?["WorldConfiguration"] as JsonObject;
        seaLevel = ReadIntNode(worldRules?["globalSeaLevel"], seaLevel);
        seaLevel = ReadIntNode(worldRules?["seaLevel"], seaLevel);
        seaLevel = ReadIntNode(worldConfig?["SeaLevel"], seaLevel);

        return Math.Clamp(seaLevel, 1, Math.Max(1, context.MapSizeY - 1));
    }

    private static string ResolveServerDescription(JsonObject? configRoot)
    {
        var fromServerDescription = ReadStringNode(configRoot?["ServerDescription"]);
        if (!string.IsNullOrWhiteSpace(fromServerDescription))
        {
            return fromServerDescription;
        }

        var worldConfig = configRoot?["WorldConfig"] as JsonObject;
        var worldRules = worldConfig?["WorldConfiguration"] as JsonObject;
        var fromWorldRule = ReadStringNode(worldRules?["serverDescription"]);
        return fromWorldRule ?? string.Empty;
    }

    private static JsonObject? TryReadProfileConfigRoot(string profileDataPath)
    {
        try
        {
            var configPath = WorkspacePathHelper.GetProfileConfigPath(profileDataPath);
            if (!File.Exists(configPath))
            {
                return null;
            }

            var json = File.ReadAllText(configPath);
            var node = JsonNode.Parse(json);
            return node as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSaveDatabasePath(InstanceProfile profile, WorldSettings worldSettings)
    {
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(worldSettings.SaveFileLocation))
        {
            candidates.Add(worldSettings.SaveFileLocation.Trim());
        }
        if (!string.IsNullOrWhiteSpace(profile.ActiveSaveFile))
        {
            candidates.Add(profile.ActiveSaveFile.Trim());
        }

        foreach (var raw in candidates)
        {
            var resolved = ResolvePotentialPath(profile.DirectoryPath, raw);
            if (!string.IsNullOrWhiteSpace(resolved) && File.Exists(resolved))
            {
                return resolved;
            }
        }

        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory) && Directory.Exists(profile.SaveDirectory))
        {
            var file = Directory.EnumerateFiles(profile.SaveDirectory, "*.vcdbs", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path =>
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(file))
            {
                return file;
            }
        }

        var savesDir = Path.Combine(profile.DirectoryPath, "Saves");
        if (Directory.Exists(savesDir))
        {
            var file = Directory.EnumerateFiles(savesDir, "*.vcdbs", SearchOption.TopDirectoryOnly)
                .OrderByDescending(path =>
                {
                    try
                    {
                        return File.GetLastWriteTimeUtc(path);
                    }
                    catch
                    {
                        return DateTime.MinValue;
                    }
                })
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(file))
            {
                return file;
            }
        }

        return string.Empty;
    }

    private static string ResolvePotentialPath(string baseDirectory, string inputPath)
    {
        if (string.IsNullOrWhiteSpace(inputPath))
        {
            return string.Empty;
        }

        try
        {
            if (Path.IsPathRooted(inputPath))
            {
                return Path.GetFullPath(inputPath);
            }

            return Path.GetFullPath(Path.Combine(baseDirectory, inputPath));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string TryResolveSavegameIdentifier(InstanceProfile profile, string saveDbPath)
    {
        string modDataRoot = Path.Combine(Path.GetFullPath(profile.DirectoryPath), "ModData");
        if (Directory.Exists(modDataRoot))
        {
            var serverMapCandidates = Directory.EnumerateDirectories(modDataRoot)
                .SelectMany(saveDir => MapDataDirectoryNames.Select(mapDataName => new
                {
                    SaveDir = saveDir,
                    SettingsPath = Path.Combine(saveDir, mapDataName, "web", "data", "settings.json")
                }))
                .Where(candidate => File.Exists(candidate.SettingsPath))
                .ToList();
            if (serverMapCandidates.Count > 0)
            {
                var best = serverMapCandidates
                    .OrderByDescending(candidate =>
                    {
                        try
                        {
                            return File.GetLastWriteTimeUtc(candidate.SettingsPath);
                        }
                        catch
                        {
                            return DateTime.MinValue;
                        }
                    })
                    .First();
                return Path.GetFileName(best.SaveDir) ?? string.Empty;
            }
        }

        if (!string.IsNullOrWhiteSpace(saveDbPath))
        {
            return Path.GetFileNameWithoutExtension(saveDbPath) ?? string.Empty;
        }

        return string.Empty;
    }

    private static int ResolveUptimeSeconds(DateTimeOffset? startedAtUtc, DateTimeOffset now)
    {
        if (!startedAtUtc.HasValue)
        {
            return 0;
        }

        var seconds = (int)(now - startedAtUtc.Value).TotalSeconds;
        return Math.Max(0, seconds);
    }

    private static string ResolveDisplayServerVersion(string version)
    {
        var raw = (version ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        return raw.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? raw : $"v{raw}";
    }

    private static string ResolveWhitelistModeText(int whitelistMode)
    {
        return whitelistMode switch
        {
            1 => "On",
            2 => "Default",
            _ => "Off"
        };
    }

    private static int ReadIntNode(JsonNode? node, int fallback)
    {
        if (node is null)
        {
            return fallback;
        }

        if (node is JsonValue valueNode && valueNode.TryGetValue<int>(out var iv))
        {
            return iv;
        }

        if (node is JsonValue valueNode2 && valueNode2.TryGetValue<long>(out var lv))
        {
            return (int)Math.Clamp(lv, int.MinValue, int.MaxValue);
        }

        if (node is JsonValue valueNode3 &&
            valueNode3.TryGetValue<string>(out var text) &&
            int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return fallback;
    }

    private static string? ReadStringNode(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node is JsonValue valueNode && valueNode.TryGetValue<string>(out var sv))
        {
            return sv;
        }

        return node.ToJsonString();
    }

    private static Uri? BuildEndpointReportUri(string hostOrUrl, bool allowInsecureHttp)
    {
        string raw = (hostOrUrl ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            raw = (allowInsecureHttp ? "http://" : "https://") + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var parsed))
        {
            return null;
        }

        bool isHttp = string.Equals(parsed.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase);
        if (isHttp && !allowInsecureHttp && !IsLoopbackHost(parsed.Host))
        {
            return null;
        }

        var path = parsed.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            path = ReportPath;
        }
        else if (!path.EndsWith(ReportPath, StringComparison.OrdinalIgnoreCase))
        {
            path = path.TrimEnd('/') + ReportPath;
        }

        var builder = new UriBuilder(parsed)
        {
            Path = path,
            Query = string.Empty,
            Fragment = string.Empty
        };

        return builder.Uri;
    }

    private static bool IsLoopbackHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return false;
        }

        if (string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            return IPAddress.IsLoopback(ip);
        }

        return false;
    }

    private static async Task SendEndpointAsync(
        Uri endpoint,
        string tokenValue,
        string payloadJson,
        DateTimeOffset now,
        string nonce,
        int timeoutSeconds,
        CancellationToken outerToken)
    {
        byte[] payloadBytes = Encoding.UTF8.GetBytes(payloadJson ?? string.Empty);
        bool useGzip = payloadBytes.Length >= EndpointGzipThresholdBytes;
        byte[] bodyBytes = useGzip ? CompressGzipPayload(payloadBytes) : payloadBytes;
        var signature = ComputeSignature(tokenValue, bodyBytes);

        using HttpRequestMessage req = new(HttpMethod.Post, endpoint);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", tokenValue);
        req.Headers.TryAddWithoutValidation("X-OSQ-Timestamp", now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture));
        req.Headers.TryAddWithoutValidation("X-OSQ-Nonce", nonce);
        req.Headers.TryAddWithoutValidation("X-OSQ-Signature", signature);
        req.Headers.TryAddWithoutValidation("X-OSQ-Mod", "launchergo-osq");
        req.Headers.TryAddWithoutValidation("X-OSQ-Version", "1");
        req.Headers.TryAddWithoutValidation("User-Agent", "LauncherGo-OpenServerQuery/1.0");
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Content = new ByteArrayContent(bodyBytes);
        req.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json")
        {
            CharSet = "utf-8"
        };
        if (useGzip)
        {
            req.Content.Headers.ContentEncoding.Add("gzip");
        }

        int timeout = timeoutSeconds <= 0 ? 8 : timeoutSeconds;
        using CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(TimeSpan.FromSeconds(timeout));

        using HttpResponseMessage response = await SharedHttpClient.SendAsync(req, cts.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string body = string.Empty;
            try
            {
                body = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }

            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {response.ReasonPhrase} {(body ?? string.Empty).Trim()}".Trim());
        }
    }

    private static byte[] CompressGzipPayload(byte[] payload)
    {
        using MemoryStream output = new();
        using (GZipStream gzip = new(output, CompressionLevel.Fastest, leaveOpen: true))
        {
            gzip.Write(payload, 0, payload.Length);
        }

        return output.ToArray();
    }

    private static string ComputeSignature(string tokenValue, byte[] payloadBytes)
    {
        byte[] key = Encoding.UTF8.GetBytes(tokenValue ?? string.Empty);
        byte[] digest = HMACSHA256.HashData(key, payloadBytes ?? Array.Empty<byte>());
        return Convert.ToBase64String(digest).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private async Task HandleRequestAsync(RuntimeState runtime, HttpListenerContext context, CancellationToken cancellationToken)
    {
        lock (_stateSync)
        {
            runtime.TotalRequests++;
        }

        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(runtime, context, 405, "method not allowed");
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path.TrimEnd('/'), ReportPath, StringComparison.OrdinalIgnoreCase))
            {
                await RejectAsync(runtime, context, 404, "not found");
                return;
            }

            if (context.Request.ContentLength64 > MaxOsqRequestBodyBytes)
            {
                await RejectAsync(runtime, context, 413, "payload too large");
                return;
            }

            string body;
            using var limited = new LimitedReadStream(context.Request.InputStream, MaxOsqRequestBodyBytes + 1);
            using (var reader = new StreamReader(limited, Encoding.UTF8, true, 8192, leaveOpen: false))
            {
                body = await reader.ReadToEndAsync(cancellationToken);
            }

            if (limited.LimitExceeded)
            {
                await RejectAsync(runtime, context, 413, "payload too large");
                return;
            }

            if (Encoding.UTF8.GetByteCount(body) > MaxOsqRequestBodyBytes)
            {
                await RejectAsync(runtime, context, 413, "payload too large");
                return;
            }

            var token = ParseAuthorizationToken(context.Request.Headers["Authorization"]);
            if (string.IsNullOrWhiteSpace(token))
            {
                await RejectAsync(runtime, context, 401, "missing bearer token");
                return;
            }

            if (!TryResolveServerHostByToken(runtime, token, out var serverHost))
            {
                await RejectAsync(runtime, context, 403, "unknown token");
                return;
            }

            var timestampRaw = context.Request.Headers["X-OSQ-Timestamp"] ?? string.Empty;
            var nonce = context.Request.Headers["X-OSQ-Nonce"] ?? string.Empty;
            var signature = context.Request.Headers["X-OSQ-Signature"] ?? string.Empty;

            if (!long.TryParse(timestampRaw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var timestamp))
            {
                await RejectAsync(runtime, context, 401, "invalid timestamp");
                return;
            }

            if (!VerifySignature(token, body, signature))
            {
                await RejectAsync(runtime, context, 401, "invalid signature");
                return;
            }

            var requestTimeUtc = DateTimeOffset.FromUnixTimeSeconds(timestamp);
            var drift = (DateTimeOffset.UtcNow - requestTimeUtc).Duration();
            if (drift > MaxClockDrift)
            {
                await RejectAsync(runtime, context, 401, "timestamp drift too large");
                return;
            }

            if (!NonceRegex.IsMatch(nonce))
            {
                await RejectAsync(runtime, context, 401, "invalid nonce");
                return;
            }

            if (!TryUseNonce(serverHost, nonce, requestTimeUtc.Add(NonceTtl)))
            {
                await RejectAsync(runtime, context, 409, "replay detected");
                return;
            }

            OsqSnapshotEnvelope? payload;
            try
            {
                payload = JsonSerializer.Deserialize<OsqSnapshotEnvelope>(body, JsonReadOptions);
            }
            catch
            {
                await RejectAsync(runtime, context, 400, "invalid json");
                return;
            }

            if (payload?.Server is null)
            {
                await RejectAsync(runtime, context, 400, "missing server payload");
                return;
            }

            var cachePayload = CreateSnapshotForOutputTarget(payload, runtime.Settings, includeMapData: false);
            var cachePayloadNode = JsonSerializer.SerializeToNode(cachePayload, OutboundJsonOptions)?.AsObject();
            if (cachePayloadNode is not null)
            {
                _osqSnapshotCacheService?.AddSnapshot(serverHost, cachePayloadNode, DateTimeOffset.UtcNow);
            }

            lock (_stateSync)
            {
                if (runtime.EndpointsByHost.TryGetValue(serverHost, out var endpoint))
                {
                    endpoint.LastPayloadTimeUtc = payload.TimestampUtc ?? string.Empty;
                    endpoint.LastServerName = payload.Server.Name ?? string.Empty;
                    endpoint.LastServerStatus = payload.Server.Status ?? string.Empty;
                    endpoint.LastOnlinePlayers = payload.Server.OnlinePlayerCount;
                    endpoint.LastMaxPlayers = payload.Server.MaxPlayers;
                    endpoint.LastReceivedUtc = DateTimeOffset.UtcNow;
                    endpoint.LastError = string.Empty;
                }

                runtime.LastReceivedUtc = DateTimeOffset.UtcNow;
                runtime.AcceptedRequests++;
            }

            await WriteResponseAsync(context, 200, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }
        catch (Exception ex)
        {
            lock (_stateSync)
            {
                runtime.LastError = ex.Message;
                runtime.RejectedRequests++;
            }
            try
            {
                await WriteResponseAsync(context, 500, "internal error");
            }
            catch
            {
                // ignore
            }
            Emit($"[osq] request error: {ex.Message}");
        }
    }

    private async Task RejectAsync(RuntimeState runtime, HttpListenerContext context, int statusCode, string message)
    {
        lock (_stateSync)
        {
            runtime.RejectedRequests++;
            runtime.LastError = message;
        }
        await WriteResponseAsync(context, statusCode, message);
    }

    private bool TryUseNonce(string serverHost, string nonce, DateTimeOffset expiresAtUtc)
    {
        CleanupExpiredNonces();
        var key = $"{serverHost}|{nonce}";
        var nonceState = new NonceState
        {
            ExpiresAtUtc = expiresAtUtc
        };
        lock (_nonceSync)
        {
            return _nonceCache.TryAdd(key, nonceState);
        }
    }

    private void CleanupExpiredNonces()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var pair in _nonceCache)
        {
            if (pair.Value.ExpiresAtUtc <= now)
            {
                _nonceCache.TryRemove(pair.Key, out _);
            }
        }
    }

    private static bool VerifySignature(string token, string rawBody, string givenSignature)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(givenSignature))
        {
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(rawBody ?? string.Empty));
            return TryDecodeSignature(givenSignature.Trim(), out var given)
                && given.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(given, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeSignature(string signature, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        try
        {
            bytes = Convert.FromBase64String(signature);
            return true;
        }
        catch
        {
            // Fall through and try base64url below.
        }

        string normalized = signature.Replace('-', '+').Replace('_', '/');
        switch (normalized.Length % 4)
        {
            case 0:
                break;
            case 2:
                normalized += "==";
                break;
            case 3:
                normalized += "=";
                break;
            default:
                return false;
        }

        try
        {
            bytes = Convert.FromBase64String(normalized);
            return true;
        }
        catch
        {
            bytes = Array.Empty<byte>();
            return false;
        }
    }

    private static string ParseAuthorizationToken(string? authorizationHeader)
    {
        var header = (authorizationHeader ?? string.Empty).Trim();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return header[7..].Trim();
    }

    private static async Task WriteResponseAsync(HttpListenerContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var ok = statusCode is >= 200 and < 300 ? "true" : "false";
        var json = $"{{\"ok\":{ok},\"message\":\"{EscapeJson(message)}\"}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private static bool TryResolveServerHostByToken(RuntimeState runtime, string token, out string serverHost)
    {
        if (runtime.HostByToken.TryGetValue(token, out serverHost!))
        {
            return true;
        }

        serverHost = string.Empty;
        return false;
    }

    private static bool HasConfiguredRobotGroups()
    {
        try
        {
            if (!File.Exists(WorkspacePathHelper.RobotSettingsPath))
            {
                return false;
            }

            var json = File.ReadAllText(WorkspacePathHelper.RobotSettingsPath);
            var settings = JsonSerializer.Deserialize<RobotSettings>(json, JsonReadOptions);
            return settings?.BoundGroupIds?.Any(id => id > 0) == true;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeJson(string value)
    {
        return (value ?? string.Empty)
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
    }

    private static OpenServerQueryRuntimeSettings BuildDefaultSettings()
    {
        return new OpenServerQueryRuntimeSettings();
    }

    private static OpenServerQueryRuntimeSettings Normalize(OpenServerQueryRuntimeSettings settings)
    {
        var endpoints = (settings.Endpoints ?? [])
            .Select(x => new OpenServerQueryEndpointSettings
            {
                ServerHost = NormalizeServerHost(x.ServerHost),
                Token = x.Token?.Trim() ?? string.Empty,
                Enabled = x.Enabled
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerHost) && IsValidToken(x.Token))
            .GroupBy(x => x.ServerHost, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.ServerHost, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new OpenServerQueryRuntimeSettings
        {
            Enabled = settings.Enabled,
            ListenPrefix = NormalizeListenPrefix(settings.ListenPrefix),
            AllowInsecureHttp = settings.AllowInsecureHttp,
            RequestTimeoutSec = settings.RequestTimeoutSec <= 0 ? 8 : settings.RequestTimeoutSec,
            IncludeServerInfo = settings.IncludeServerInfo,
            IncludePlayers = settings.IncludePlayers,
            IncludePlayerEvents = settings.IncludePlayerEvents,
            IncludeChats = settings.IncludeChats,
            IncludeNotifications = settings.IncludeNotifications,
            IncludeMapData = settings.IncludeMapData,
            Endpoints = endpoints
        };
    }

    private static bool IsValidToken(string token)
    {
        var value = token?.Trim() ?? string.Empty;
        return TokenRegex.IsMatch(value);
    }

    private static string NormalizeServerHost(string input)
    {
        var raw = (input ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return string.Empty;
        }

        if (!raw.Contains("://", StringComparison.Ordinal))
        {
            return raw.TrimEnd('/').ToLowerInvariant();
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return raw.ToLowerInvariant();
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return raw.ToLowerInvariant();
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme.ToLowerInvariant(),
            Host = uri.Host.ToLowerInvariant(),
            Query = string.Empty,
            Fragment = string.Empty
        };

        if (uri.IsDefaultPort)
        {
            builder.Port = -1;
        }

        var path = uri.AbsolutePath;
        if (string.IsNullOrWhiteSpace(path) || path == "/")
        {
            builder.Path = string.Empty;
            return builder.Uri.GetLeftPart(UriPartial.Authority);
        }

        builder.Path = path.TrimEnd('/');
        return builder.Uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? DefaultListenPrefix
            : value.Trim();

        if (IsWildcardListenPrefix(raw))
        {
            return NormalizeWildcardListenPrefix(raw);
        }

        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return DefaultListenPrefix;
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return DefaultListenPrefix;
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return DefaultListenPrefix;
        }

        var prefix = uri.GetLeftPart(UriPartial.Path);
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        return prefix;
    }

    private static bool IsWildcardListenPrefix(string value)
    {
        return value.StartsWith("http://+:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("http://*:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://+:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://*:", StringComparison.OrdinalIgnoreCase)
               || value.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeWildcardListenPrefix(string value)
    {
        string prefix = value.Trim();
        if (!prefix.EndsWith('/'))
        {
            prefix += "/";
        }

        if (prefix.StartsWith("http://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "http://+:" + prefix["http://0.0.0.0:".Length..];
        }

        if (prefix.StartsWith("https://0.0.0.0:", StringComparison.OrdinalIgnoreCase))
        {
            return "https://+:" + prefix["https://0.0.0.0:".Length..];
        }

        return prefix;
    }

    private static string FormatIso(DateTimeOffset? value)
    {
        if (!value.HasValue)
        {
            return string.Empty;
        }

        return value.Value.ToString("yyyy-MM-ddTHH:mm:sszzz", CultureInfo.InvariantCulture);
    }

    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[16];
        RandomNumberGenerator.Fill(bytes);
        StringBuilder sb = new(bytes.Length * 2);
        for (int i = 0; i < bytes.Length; i++)
        {
            sb.Append(bytes[i].ToString("x2", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private void Emit(string message)
    {
        OutputReceived?.Invoke(this, message);
    }

    private sealed class RuntimeState
    {
        public required OpenServerQueryRuntimeSettings Settings { get; init; }
        public required Dictionary<string, EndpointState> EndpointsByHost { get; init; }
        public required Dictionary<string, string> HostByToken { get; init; }
        public HttpListener? Listener { get; set; }
        public DateTimeOffset StartedAtUtc { get; init; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? LastReceivedUtc { get; set; }
        public string LastError { get; set; } = string.Empty;
        public long TotalRequests { get; set; }
        public long AcceptedRequests { get; set; }
        public long RejectedRequests { get; set; }
    }

    private sealed class EndpointState
    {
        public required OpenServerQueryEndpointSettings Settings { get; init; }
        public string LastServerName { get; set; } = string.Empty;
        public string LastServerStatus { get; set; } = string.Empty;
        public int LastOnlinePlayers { get; set; }
        public int LastMaxPlayers { get; set; }
        public string LastPayloadTimeUtc { get; set; } = string.Empty;
        public DateTimeOffset? LastReceivedUtc { get; set; }
        public string LastError { get; set; } = string.Empty;
    }

    private readonly record struct QqRobotRemoteServerBinding(string ServerHost, string Token);

    private sealed class NonceState
    {
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class LimitedReadStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _maxBytes;
        private long _bytesRead;

        public LimitedReadStream(Stream inner, long maxBytes)
        {
            _inner = inner;
            _maxBytes = maxBytes;
        }

        public bool LimitExceeded { get; private set; }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => _bytesRead;
            set => throw new NotSupportedException();
        }

        public override void Flush()
        {
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (LimitExceeded)
            {
                return 0;
            }

            var allowed = (int)Math.Min(count, Math.Max(0, _maxBytes - _bytesRead));
            if (allowed <= 0)
            {
                LimitExceeded = true;
                return 0;
            }

            var read = _inner.Read(buffer, offset, allowed);
            _bytesRead += read;
            if (_bytesRead >= _maxBytes)
            {
                LimitExceeded = true;
            }

            return read;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (LimitExceeded)
            {
                return 0;
            }

            var allowed = (int)Math.Min(buffer.Length, Math.Max(0, _maxBytes - _bytesRead));
            if (allowed <= 0)
            {
                LimitExceeded = true;
                return 0;
            }

            var read = await _inner.ReadAsync(buffer[..allowed], cancellationToken);
            _bytesRead += read;
            if (_bytesRead >= _maxBytes)
            {
                LimitExceeded = true;
            }

            return read;
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LocalServerContext
    {
        public required InstanceProfile Profile { get; init; }
        public required ServerRuntimeStatus RuntimeStatus { get; init; }
        public required ServerCommonSettings ServerSettings { get; init; }
        public required WorldSettings WorldSettings { get; init; }
        public required JsonObject? RawConfigRoot { get; init; }
        public required int MapSizeX { get; init; }
        public required int MapSizeY { get; init; }
        public required int MapSizeZ { get; init; }
        public required string SaveDatabasePath { get; init; }
        public required string Description { get; init; }
    }

    private enum ServerSignalKind
    {
        Chat = 1,
        PlayerEvent = 2,
        Notification = 3
    }

    private sealed class ParsedServerSignal
    {
        public ServerSignalKind Kind { get; init; }
        public DateTimeOffset SortTimeUtc { get; init; }
        public string TimestampUtc { get; init; } = string.Empty;
        public string Sender { get; init; } = string.Empty;
        public string Content { get; init; } = string.Empty;
        public string EventType { get; init; } = string.Empty;
        public string PlayerName { get; init; } = string.Empty;
        public string ConnectionState { get; init; } = string.Empty;
    }

    private sealed class LocalServerActivitySnapshot
    {
        public List<OsqChatInfo> Chats { get; init; } = [];
        public List<OsqPlayerEventInfo> PlayerEvents { get; init; } = [];
        public List<OsqServerNotificationInfo> Notifications { get; init; } = [];
    }

    private sealed class ServerMapRootCandidate
    {
        public required string RootPath { get; init; }
        public required string SavegameIdentifier { get; init; }
    }

    private sealed class OsqSnapshotEnvelope
    {
        public string ModId { get; set; } = string.Empty;
        public int SchemaVersion { get; set; } = 2;
        public string TimestampUtc { get; set; } = string.Empty;
        public long UnixTime { get; set; }
        public string Nonce { get; set; } = string.Empty;
        public List<string> Capabilities { get; set; } = [];
        public OsqServerInfo Server { get; set; } = new();
        public List<OsqPlayerInfo> Players { get; set; } = [];
        public List<OsqPlayerEventInfo> PlayerEvents { get; set; } = [];
        public List<OsqChatInfo> RecentChats { get; set; } = [];
        public List<OsqServerNotificationInfo> ServerNotifications { get; set; } = [];

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public OsqServerMapInfo? ServerMap { get; set; }
    }

    private sealed class OsqServerInfo
    {
        public string Name { get; set; } = string.Empty;
        public string Version { get; set; } = string.Empty;
        public string NetworkVersion { get; set; } = string.Empty;
        public string ApiVersion { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string WhitelistMode { get; set; } = string.Empty;
        public bool WhitelistEnforced { get; set; }
        public bool HasPassword { get; set; }
        public int PlayerCount { get; set; }
        public int OnlinePlayerCount { get; set; }
        public int MaxPlayers { get; set; }
        public string Description { get; set; } = string.Empty;
        public string WelcomeMessage { get; set; } = string.Empty;
        public bool Dedicated { get; set; }
        public string ServerIp { get; set; } = string.Empty;
        public int ServerPort { get; set; }
        public string WorldName { get; set; } = string.Empty;
        public int UptimeSeconds { get; set; }
    }

    private sealed class OsqPlayerInfo
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public bool IsOnline { get; set; }
        public bool IsPlaying { get; set; }
        public string ConnectionState { get; set; } = string.Empty;
        public int? PingMs { get; set; }
        public string DelayLevel { get; set; } = "unknown";
        public string LastSeenUtc { get; set; } = string.Empty;
    }

    private sealed class OsqPlayerEventInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public string EventType { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public string PlayerUid { get; set; } = string.Empty;
        public string ConnectionState { get; set; } = string.Empty;
    }

    private sealed class OsqChatInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public int ChannelId { get; set; }
        public string SenderName { get; set; } = string.Empty;
        public string SenderUid { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
        public string Data { get; set; } = string.Empty;
    }

    private sealed class OsqServerNotificationInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;
        public string Message { get; set; } = string.Empty;
    }

    private sealed class OsqServerMapInfo
    {
        public string Standard { get; set; } = "osq-server-map-v1";
        public bool Enabled { get; set; }
        public bool FullSnapshot { get; set; }
        public bool IncludeTilePayload { get; set; }
        public string Source { get; set; } = string.Empty;
        public string SavegameIdentifier { get; set; } = string.Empty;
        public string ServerMapRoot { get; set; } = string.Empty;
        public string DynamicDataRoot { get; set; } = string.Empty;
        public string ServerMapUrl { get; set; } = string.Empty;
        public string GeneratedAtUtc { get; set; } = string.Empty;
        public string DataDigest { get; set; } = string.Empty;
        public string SkippedReason { get; set; } = string.Empty;
        public int PlayersCount { get; set; }
        public int MarkersCount { get; set; }
        public JsonElement? ServerMapConfig { get; set; }
        public JsonElement? Settings { get; set; }
        public JsonElement? Players { get; set; }
        public JsonElement? MarkersIndex { get; set; }
        public Dictionary<string, JsonElement> MarkerLayers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public List<MapTileEntry> Tiles { get; set; } = [];
        public JsonElement? OverviewData { get; set; }
    }

    private sealed class MapTileEntry
    {
        public string RelativePath { get; set; } = string.Empty;
        public string FileName { get; set; } = string.Empty;
        public string Renderer { get; set; } = string.Empty;
        public int? Zoom { get; set; }
        public string Tile { get; set; } = string.Empty;
        public string MimeType { get; set; } = string.Empty;
        public long SizeBytes { get; set; }
        public string LastWriteUtc { get; set; } = string.Empty;
        public string Sha256 { get; set; } = string.Empty;
        public bool ContentIncluded { get; set; }
        public string ContentEncoding { get; set; } = string.Empty;
        public string DataBase64 { get; set; } = string.Empty;
        public string SkippedReason { get; set; } = string.Empty;
    }

    private sealed class MapChunkLightInfo
    {
        public MapChunkLightInfo(int chunkX, int chunkZ, int[] pixels)
        {
            ChunkX = chunkX;
            ChunkZ = chunkZ;
            Pixels = pixels;
        }

        public int ChunkX { get; }
        public int ChunkZ { get; }
        public int[] Pixels { get; }
    }

    private sealed class TileCanvas
    {
        public TileCanvas(int width, int height)
        {
            Width = width;
            Height = height;
            Pixels = new int[width * height];
        }

        public int Width { get; }
        public int Height { get; }
        public int[] Pixels { get; }
    }

    [ProtoContract]
    private sealed class SavegameMapChunk
    {
        [ProtoMember(3)]
        public ushort[]? RainHeightMap { get; set; }

        [ProtoMember(7)]
        public ushort[]? TerrainHeightMap { get; set; }

        [ProtoMember(13)]
        public int[]? TopRockIdMap { get; set; }
    }
}
