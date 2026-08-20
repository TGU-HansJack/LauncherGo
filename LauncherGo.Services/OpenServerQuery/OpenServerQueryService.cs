using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
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
    private const string LocalProfileSnapshotHostPrefix = "local:";
    private const int PushIntervalSec = 2;
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
                .OrderBy(x => x.Value.Settings.ProfileId, StringComparer.OrdinalIgnoreCase)
                .ThenBy(x => x.Value.Settings.ServerHost, StringComparer.OrdinalIgnoreCase)
                .Select(x => new OpenServerQueryEndpointRuntime
                {
                    ProfileId = x.Value.Settings.ProfileId,
                    ServerHost = x.Value.Settings.ServerHost,
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
                var endpointKey = BuildEndpointRuntimeKey(endpoint);
                endpoints[endpointKey] = new EndpointState
                {
                    Settings = endpoint
                };

                if (!endpoint.Enabled)
                {
                    continue;
                }

                var token = endpoint.Token.Trim();
                hostByToken.TryAdd(token, endpointKey);
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

    private static OpenServerQueryRuntimeSettings BuildEndpointRuntimeSettings(
        OpenServerQueryRuntimeSettings globalSettings,
        OpenServerQueryEndpointSettings endpointSettings)
    {
        return new OpenServerQueryRuntimeSettings
        {
            Enabled = globalSettings.Enabled,
            ListenPrefix = globalSettings.ListenPrefix,
            AllowInsecureHttp = globalSettings.AllowInsecureHttp || endpointSettings.AllowInsecureHttp,
            RequestTimeoutSec = globalSettings.RequestTimeoutSec,
            IncludeServerInfo = endpointSettings.IncludeServerInfo,
            IncludePlayers = endpointSettings.IncludePlayers,
            IncludePlayerEvents = endpointSettings.IncludePlayerEvents,
            IncludeChats = endpointSettings.IncludeChats,
            IncludeNotifications = endpointSettings.IncludeNotifications,
            Endpoints = [endpointSettings]
        };
    }

    private static OpenServerQueryRuntimeSettings? ResolveLocalProfileSnapshotSettings(RuntimeState runtime, string profileId)
    {
        var profileEndpoints = runtime.EndpointsByHost.Values
            .Where(endpoint =>
                !string.IsNullOrWhiteSpace(endpoint.Settings.ProfileId) &&
                endpoint.Settings.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (profileEndpoints.Count > 0)
        {
            var enabledProfileEndpoint = profileEndpoints.FirstOrDefault(static endpoint => endpoint.Settings.Enabled);
            return enabledProfileEndpoint is null
                ? null
                : BuildEndpointRuntimeSettings(runtime.Settings, enabledProfileEndpoint.Settings);
        }

        var legacyEndpoint = runtime.EndpointsByHost.Values.FirstOrDefault(endpoint =>
            endpoint.Settings.Enabled && string.IsNullOrWhiteSpace(endpoint.Settings.ProfileId));
        return legacyEndpoint is null
            ? runtime.Settings
            : BuildEndpointRuntimeSettings(runtime.Settings, legacyEndpoint.Settings);
    }

    private static string BuildLocalProfileSnapshotHost(string profileId)
    {
        var normalized = (profileId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? "local"
            : LocalProfileSnapshotHostPrefix + normalized;
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
            var cacheSettings = ResolveLocalProfileSnapshotSettings(runtime, context.Profile.Id);
            if (cacheSettings is not null)
            {
                var cachePayload = await BuildLocalServerSnapshotAsync(
                    context,
                    cacheSettings,
                    now,
                    nonce,
                    cancellationToken);
                var qqPayload = CreateSnapshotForOutputTarget(cachePayload, cacheSettings);
                var qqJson = JsonSerializer.Serialize(qqPayload, OutboundJsonOptions);

                var cachedNode = JsonNode.Parse(qqJson)?.AsObject();
                if (cachedNode is not null)
                {
                    _osqSnapshotCacheService?.AddSnapshot("local", cachedNode, now);
                    _osqSnapshotCacheService?.AddSnapshot(BuildLocalProfileSnapshotHost(context.Profile.Id), cachedNode.DeepClone().AsObject(), now);
                }
            }

            foreach (var pair in runtime.EndpointsByHost)
            {
                var endpoint = pair.Value;
                if (!endpoint.Settings.Enabled)
                {
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(endpoint.Settings.ProfileId) &&
                    !endpoint.Settings.ProfileId.Equals(context.Profile.Id, StringComparison.OrdinalIgnoreCase))
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

                var reportUri = BuildEndpointReportUri(
                    endpoint.Settings.ServerHost,
                    runtime.Settings.AllowInsecureHttp || endpoint.Settings.AllowInsecureHttp);
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
                    var endpointSettings = BuildEndpointRuntimeSettings(runtime.Settings, endpoint.Settings);
                    var endpointPayload = await BuildLocalServerSnapshotAsync(
                        context,
                        endpointSettings,
                        now,
                        nonce,
                        cancellationToken);
                    var json = JsonSerializer.Serialize(endpointPayload, OutboundJsonOptions);

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

        var description = ResolveServerDescription(configRoot);

        return new LocalServerContext
        {
            Profile = profile,
            RuntimeStatus = runtimeStatus,
            ServerSettings = serverSettings,
            WorldSettings = worldSettings,
            Description = description
        };
    }

    private Task<OsqSnapshotEnvelope> BuildLocalServerSnapshotAsync(
        LocalServerContext context,
        OpenServerQueryRuntimeSettings settings,
        DateTimeOffset now,
        string nonce,
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
            Capabilities = BuildSnapshotCapabilities(settings),
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

        return Task.FromResult(snapshot);
    }

    private static OsqSnapshotEnvelope CreateSnapshotForOutputTarget(
        OsqSnapshotEnvelope source,
        OpenServerQueryRuntimeSettings settings)
    {
        return new OsqSnapshotEnvelope
        {
            ModId = source.ModId,
            SchemaVersion = source.SchemaVersion,
            TimestampUtc = source.TimestampUtc,
            UnixTime = source.UnixTime,
            Nonce = source.Nonce,
            Capabilities = BuildSnapshotCapabilities(settings),
            Server = source.Server,
            Players = settings.IncludePlayers ? source.Players ?? [] : [],
            PlayerEvents = settings.IncludePlayerEvents ? source.PlayerEvents ?? [] : [],
            RecentChats = settings.IncludeChats ? source.RecentChats ?? [] : [],
            ServerNotifications = settings.IncludeNotifications ? source.ServerNotifications ?? [] : []
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

    private static List<string> BuildSnapshotCapabilities(OpenServerQueryRuntimeSettings settings)
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

            if (!TryResolveEndpointKeyByToken(runtime, token, out var endpointKey))
            {
                await RejectAsync(runtime, context, 403, "unknown token");
                return;
            }

            var serverHost = runtime.EndpointsByHost.TryGetValue(endpointKey, out var resolvedEndpoint)
                ? resolvedEndpoint.Settings.ServerHost
                : endpointKey;
            var endpointOutputSettings = resolvedEndpoint is null
                ? runtime.Settings
                : BuildEndpointRuntimeSettings(runtime.Settings, resolvedEndpoint.Settings);
            var endpointProfileId = resolvedEndpoint?.Settings.ProfileId ?? string.Empty;
            var cacheHost = !string.IsNullOrWhiteSpace(endpointProfileId)
                ? BuildLocalProfileSnapshotHost(endpointProfileId)
                : serverHost;

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

            if (!TryUseNonce(endpointKey, nonce, requestTimeUtc.Add(NonceTtl)))
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

            var cachePayload = CreateSnapshotForOutputTarget(payload, endpointOutputSettings);
            var cachePayloadNode = JsonSerializer.SerializeToNode(cachePayload, OutboundJsonOptions)?.AsObject();
            if (cachePayloadNode is not null)
            {
                _osqSnapshotCacheService?.AddSnapshot(cacheHost, cachePayloadNode, DateTimeOffset.UtcNow);
                if (!cacheHost.Equals(serverHost, StringComparison.OrdinalIgnoreCase))
                {
                    _osqSnapshotCacheService?.AddSnapshot(serverHost, cachePayloadNode.DeepClone().AsObject(), DateTimeOffset.UtcNow);
                }
            }

            lock (_stateSync)
            {
                if (runtime.EndpointsByHost.TryGetValue(endpointKey, out var endpoint))
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

    private static bool TryResolveEndpointKeyByToken(RuntimeState runtime, string token, out string endpointKey)
    {
        if (runtime.HostByToken.TryGetValue(token, out endpointKey!))
        {
            return true;
        }

        endpointKey = string.Empty;
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
                ProfileId = x.ProfileId?.Trim() ?? string.Empty,
                ServerHost = NormalizeServerHost(x.ServerHost),
                Token = x.Token?.Trim() ?? string.Empty,
                Enabled = x.Enabled,
                AllowInsecureHttp = x.AllowInsecureHttp,
                IncludeServerInfo = x.IncludeServerInfo,
                IncludePlayers = x.IncludePlayers,
                IncludePlayerEvents = x.IncludePlayerEvents,
                IncludeChats = x.IncludeChats,
                IncludeNotifications = x.IncludeNotifications
            })
            .Where(x => !string.IsNullOrWhiteSpace(x.ServerHost) && IsValidToken(x.Token))
            .GroupBy(BuildEndpointRuntimeKey, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(x => x.ProfileId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.ServerHost, StringComparer.OrdinalIgnoreCase)
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
            Endpoints = endpoints
        };
    }

    private static string BuildEndpointRuntimeKey(OpenServerQueryEndpointSettings endpoint)
    {
        var profileId = endpoint.ProfileId?.Trim() ?? string.Empty;
        var host = endpoint.ServerHost?.Trim() ?? string.Empty;
        return string.IsNullOrWhiteSpace(profileId)
            ? host
            : $"{profileId}|{host}";
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

}
