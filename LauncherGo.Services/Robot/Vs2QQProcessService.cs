using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Features;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed class Vs2QQProcessService
{
    private static int _encodingProviderRegistered;
    private static readonly JsonSerializerOptions OsqJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private const string LocalServerSnapshotHost = "local";
    private const string LocalProfileSnapshotHostPrefix = "local:";
    private const int MaxOsqStatusHistoryPerHost = 30;
    private const int MaxServerStatusQueryCount = 10;
    private const int MaxOneBotMessageLength = 1800;
    private static readonly TimeSpan RecentRelaySignatureWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan GroupMemberDisplayNameCacheDuration = TimeSpan.FromMinutes(10);
    private static readonly Regex CqImageRegex = new(@"\[CQ:image,[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CqAtRegex = new(@"\[CQ:at,(?<params>[^\]]+)\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CqAtQqParameterRegex = new(@"(?:^|,)qq=(?<qq>[^,]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex CqCodeRegex = new(@"\[CQ:[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QqIdentifierRegex = new(@"^[1-9]\d{4,11}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex QqNumberMentionRegex = new(@"(?<![\p{L}\p{N}_])@[1-9]\d{4,11}(?!\d)", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimePartRegex = new(@"(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroupRelayEchoRegex = new(@"^\[(?:群聊|group)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerRelayEchoRegex = new(@"^\[(?:服务器|server)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly IServerProcessService _serverProcessService;
    private readonly IServerTransport _serverTransport;
    private readonly IInstanceProfileService _instanceProfileService;
    private readonly IInstanceSaveService _instanceSaveService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly ILauncherPreferencesService _launcherPreferencesService;
    private readonly IOsqSnapshotCacheService _osqSnapshotCacheService;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Vs2QQRuntimeContext? _runtime;

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<RobotRuntimeStatus>? StatusChanged;

    public RobotRuntimeStatus CurrentStatus { get; private set; } = new();

    public Vs2QQProcessService(
        IServerProcessService serverProcessService,
        IServerTransport serverTransport,
        IInstanceProfileService instanceProfileService,
        IInstanceSaveService instanceSaveService,
        IInstanceServerConfigService instanceServerConfigService,
        ILauncherPreferencesService launcherPreferencesService,
        IOsqSnapshotCacheService osqSnapshotCacheService)
    {
        _serverProcessService = serverProcessService;
        _serverTransport = serverTransport;
        _instanceProfileService = instanceProfileService;
        _instanceSaveService = instanceSaveService;
        _instanceServerConfigService = instanceServerConfigService;
        _launcherPreferencesService = launcherPreferencesService;
        _osqSnapshotCacheService = osqSnapshotCacheService;
        if (Interlocked.Exchange(ref _encodingProviderRegistered, 1) == 0)
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
    }

    public async Task<OperationResult> StartAsync(RobotSettings settings, CancellationToken cancellationToken = default)
    {
        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is not null && CurrentStatus.IsRunning)
            {
                return OperationResult.Failed("VS2QQ 已在运行中。");
            }

            var normalizeResult = NormalizeLaunchSettings(settings);
            if (!normalizeResult.IsSuccess || normalizeResult.Value is null)
            {
                return OperationResult.Failed(normalizeResult.Message ?? "VS2QQ 配置无效。");
            }

            var normalized = normalizeResult.Value;
            var storage = new Vs2QQStorage(normalized.DatabasePath);
            Vs2QQRuntimeContext runtime = new(normalized, storage);
            var oneBot = new Vs2QQOneBotClient(
                normalized.OneBotWsUrl,
                normalized.AccessToken,
                normalized.ReconnectIntervalSec,
                EmitOutput,
                (eventPayload, token) => HandleOneBotEventAsync(runtime, eventPayload, token));
            runtime.OneBot = oneBot;
            runtime.OsqSnapshotHandler = (_, args) => OnSharedOsqSnapshotReceived(runtime, args);
            _osqSnapshotCacheService.SnapshotReceived += runtime.OsqSnapshotHandler;
            TryImportLatestSharedOsqSnapshot(runtime, LocalServerSnapshotHost);
            runtime.ServerOutputHandler = (_, output) => OnServerProfileOutputReceived(runtime, output);
            _serverProcessService.ProfileOutputReceived += runtime.ServerOutputHandler;

            _runCts = new CancellationTokenSource();
            _runtime = runtime;
            _runTask = Task.Run(() => RunRuntimeAsync(runtime, _runCts.Token), CancellationToken.None);

            CurrentStatus = new RobotRuntimeStatus
            {
                IsRunning = true,
                ProcessId = Environment.ProcessId,
                StartedAtUtc = DateTimeOffset.UtcNow,
                OneBotWsUrl = normalized.OneBotWsUrl
            };
            StatusChanged?.Invoke(this, CurrentStatus);
            EmitOutput($"[system] VS2QQ 已启动。OneBot={normalized.OneBotWsUrl}");
            EmitOutput($"[system] VS2QQ 数据库：{normalized.DatabasePath}");

            return OperationResult.Success("VS2QQ 已启动。");
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("启动 VS2QQ 失败。", ex);
        }
        finally
        {
            _runtimeGate.Release();
        }
    }

    public async Task<OperationResult> StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        Task? runTask;
        Vs2QQRuntimeContext? runtime;

        await _runtimeGate.WaitAsync(cancellationToken);
        try
        {
            if (_runtime is null || _runTask is null || !CurrentStatus.IsRunning)
            {
                return OperationResult.Success("VS2QQ 未运行。");
            }

            runTask = _runTask;
            runtime = _runtime;
            _runCts?.Cancel();
        }
        finally
        {
            _runtimeGate.Release();
        }

        try
        {
            var timeoutTask = Task.Delay(gracefulTimeout, cancellationToken);
            var completed = await Task.WhenAny(runTask!, timeoutTask);
            cancellationToken.ThrowIfCancellationRequested();

            if (!ReferenceEquals(completed, runTask))
            {
                return OperationResult.Failed("停止 VS2QQ 超时。");
            }

            await runTask!;
            return OperationResult.Success("VS2QQ 已停止。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return OperationResult.Failed("停止 VS2QQ 失败。", ex);
        }
    }

    private async Task RunRuntimeAsync(Vs2QQRuntimeContext runtime, CancellationToken cancellationToken)
    {
        try
        {
            await runtime.OneBot.RunForeverAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal cancellation.
        }
        catch (Exception ex)
        {
            EmitOutput($"[system] VS2QQ 运行异常: {ex.Message}");
        }
        finally
        {
            await FinalizeRuntimeAsync(runtime);
        }
    }

    private async Task FinalizeRuntimeAsync(Vs2QQRuntimeContext runtime)
    {
        bool shouldNotifyStopped = false;
        string? wsUrl = null;
        CancellationTokenSource? ctsToDispose = null;

        await _runtimeGate.WaitAsync();
        try
        {
            if (!ReferenceEquals(_runtime, runtime))
            {
                return;
            }

            wsUrl = runtime.Settings.OneBotWsUrl;
            ctsToDispose = _runCts;
            _runCts = null;
            _runTask = null;
            _runtime = null;
            shouldNotifyStopped = CurrentStatus.IsRunning;

            CurrentStatus = new RobotRuntimeStatus
            {
                IsRunning = false,
                ProcessId = null,
                StartedAtUtc = null,
                OneBotWsUrl = wsUrl
            };
        }
        finally
        {
            _runtimeGate.Release();
        }

        ctsToDispose?.Dispose();
        if (runtime.OsqSnapshotHandler is not null)
        {
            _osqSnapshotCacheService.SnapshotReceived -= runtime.OsqSnapshotHandler;
        }
        if (runtime.ServerOutputHandler is not null)
        {
            _serverProcessService.ProfileOutputReceived -= runtime.ServerOutputHandler;
        }
        await runtime.DisposeAsync();

        if (shouldNotifyStopped)
        {
            StatusChanged?.Invoke(this, CurrentStatus);
            EmitOutput("[system] VS2QQ 已停止。");
        }
    }

    private void OnSharedOsqSnapshotReceived(Vs2QQRuntimeContext runtime, OsqSnapshotReceivedEventArgs args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var host = ResolveBoundServerHostForSnapshot(runtime, args.ServerHost);
                if (string.IsNullOrWhiteSpace(host) || !runtime.HasBoundGroupsForSnapshotHost(host))
                {
                    return;
                }

                var payload = JsonSerializer.Deserialize<OsqSnapshotEnvelope>(
                    args.Payload.ToJsonString(),
                    OsqJsonOptions);
                if (payload?.Server is null)
                {
                    return;
                }

                runtime.Storage.AddOsqSnapshot(host, payload);
                await ForwardOsqSnapshotAsync(runtime, host, payload, CancellationToken.None);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] 共享 OSQ 快照处理失败 host={args.ServerHost}: {ex.Message}");
            }
        });
    }

    private async Task HandleOneBotEventAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        if (!string.Equals(GetString(eventPayload, "post_type"), "message", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        var selfId = GetInt64(eventPayload, "self_id", -1);
        if (userId > 0 && userId == selfId)
        {
            return;
        }

        var rawMessage = (await ExtractPlainTextAsync(runtime, eventPayload, cancellationToken)).Trim();
        if (string.IsNullOrWhiteSpace(rawMessage))
        {
            return;
        }

        if (rawMessage.StartsWith('/'))
        {
            try
            {
                await HandleCommandAsync(runtime, eventPayload, rawMessage, cancellationToken);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] 命令处理异常: {ex.Message}");
                try
                {
                    await ReplyAsync(runtime, eventPayload, $"命令执行异常：{ex.Message}", cancellationToken);
                }
                catch (Exception replyEx)
                {
                    EmitOutput($"[warn] 命令异常回包失败: {replyEx.Message}");
                }
            }
            return;
        }

        if (TryBuildOutboundGroupMessage(runtime, eventPayload, rawMessage, out var outboundMessage))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                await SendToGameServerAsync(runtime, groupId, outboundMessage, cancellationToken);
            }
            return;
        }
    }

    private async Task HandleCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string rawCommand,
        CancellationToken cancellationToken)
    {
        var firstSpace = rawCommand.IndexOf(' ');
        var command = (firstSpace >= 0 ? rawCommand[..firstSpace] : rawCommand).Trim().ToLowerInvariant();
        var args = firstSpace >= 0 ? rawCommand[(firstSpace + 1)..].Trim() : string.Empty;

        switch (command)
        {
            case "/help":
                await ReplyAsync(runtime, eventPayload, BuildHelpText(), cancellationToken);
                return;
            case "/send":
                await HandleSendCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/server":
                await HandleServerCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            default:
                await ReplyAsync(runtime, eventPayload, "Unknown command. Use /help.", cancellationToken);
                return;
        }
    }

    private async Task HandleSendCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string args,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var commandText = NormalizeServerCommand(args);
        if (string.IsNullOrWhiteSpace(commandText))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /send <server_command>", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        await _serverProcessService.SendCommandAsync(
            targetResolution.Profile.Id,
            commandText,
            cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已发送服务端指令：{commandText}", cancellationToken);
    }

    private async Task HandleServerCommandAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server status [n] | /server players [n] | /server start [档案名或ID] | /server stop [档案名或ID] | /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        var subCommand = parts[0].ToLowerInvariant();
        if (subCommand == "status")
        {
            await HandleServerStatusCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "players")
        {
            await HandleServerPlayersCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "password")
        {
            await HandleServerPasswordCommandAsync(runtime, eventPayload, parts, cancellationToken);
            return;
        }

        if (subCommand == "start")
        {
            await HandleServerStartCommandAsync(
                runtime,
                eventPayload,
                string.Join(' ', parts.Skip(1)),
                cancellationToken);
            return;
        }

        if (subCommand == "stop")
        {
            await HandleServerStopCommandAsync(
                runtime,
                eventPayload,
                string.Join(' ', parts.Skip(1)),
                cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, "Only /server status [n], /server players [n], /server start [档案名或ID], /server stop [档案名或ID], and /server password get|set are supported.", cancellationToken);
    }

    private async Task HandleServerStatusCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var index = 1;
        if (parts.Count > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            index = parsed;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound in robot settings.", cancellationToken);
            return;
        }

        var snapshotHost = ResolveBoundSnapshotHostForGroup(runtime, groupId);
        if (string.IsNullOrWhiteSpace(snapshotHost))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound to a server profile.", cancellationToken);
            return;
        }

        var snapshot = runtime.Storage.GetLatestOsqSnapshot(snapshotHost, index);
        if (snapshot is null && index == 1)
        {
            snapshot = TryImportLatestSharedOsqSnapshot(runtime, snapshotHost);
        }

        if (snapshot is null)
        {
            await ReplyAsync(runtime, eventPayload, $"No server status #{index}.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, BuildOsqSummaryMessage(snapshot), cancellationToken);
    }

    private async Task HandleServerPasswordCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        if (parts.Count < 2)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        var action = parts[1].ToLowerInvariant();
        var isGet = action == "get";
        var isSet = action == "set";
        if (!isGet && !isSet)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password get | /server password set <new_password>", cancellationToken);
            return;
        }

        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, string.Empty);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage) || targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var status = _serverProcessService.GetCurrentStatus(targetResolution.Profile.Id);
        if (!status.IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, "No local running profile. Password command only supports local bound server.", cancellationToken);
            return;
        }

        var profile = _instanceProfileService.GetProfileById(targetResolution.Profile.Id);
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot resolve local profile for password operation.", cancellationToken);
            return;
        }

        var serverSettings = await _instanceServerConfigService.LoadServerSettingsAsync(profile, cancellationToken);
        var worldSettings = await _instanceServerConfigService.LoadWorldSettingsAsync(profile, cancellationToken);
        var worldRules = await _instanceServerConfigService.LoadWorldRulesAsync(profile, cancellationToken);

        if (isGet)
        {
            var passwordText = string.IsNullOrWhiteSpace(serverSettings.Password) ? "(empty)" : serverSettings.Password.Trim();
            var userId = GetInt64(eventPayload, "user_id");
            if (userId > 0)
            {
                await runtime.OneBot.SendPrivateMsgAsync(userId, $"服务器加入密码：{passwordText}", cancellationToken);
                if (IsGroupMessage(eventPayload))
                {
                    await ReplyAsync(runtime, eventPayload, "密码已通过私聊发送。", cancellationToken);
                }
            }
            else
            {
                await ReplyAsync(runtime, eventPayload, "无法识别用户，不能发送密码。", cancellationToken);
            }
            return;
        }

        if (parts.Count < 3)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server password set <new_password>", cancellationToken);
            return;
        }

        var newPassword = string.Join(' ', parts.Skip(2)).Trim();
        if (newPassword.Length > 128)
        {
            await ReplyAsync(runtime, eventPayload, "Password too long. Maximum 128 characters.", cancellationToken);
            return;
        }

        serverSettings.Password = string.Equals(newPassword, "-", StringComparison.Ordinal)
            ? null
            : newPassword;
        await _instanceServerConfigService.SaveSettingsAsync(profile, serverSettings, worldSettings, worldRules, cancellationToken);

        await ReplyAsync(runtime, eventPayload, string.IsNullOrWhiteSpace(serverSettings.Password) ? "密码已清空。" : "密码已更新。", cancellationToken);
    }

    private async Task HandleServerPlayersCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        IReadOnlyList<string> parts,
        CancellationToken cancellationToken)
    {
        var index = 1;
        if (parts.Count > 1 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed > 0)
        {
            index = parsed;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound in robot settings.", cancellationToken);
            return;
        }

        var snapshotHost = ResolveBoundSnapshotHostForGroup(runtime, groupId);
        if (string.IsNullOrWhiteSpace(snapshotHost))
        {
            await ReplyAsync(runtime, eventPayload, "This group is not bound to a server profile.", cancellationToken);
            return;
        }

        var snapshot = runtime.Storage.GetLatestOsqSnapshot(snapshotHost, index);
        if (snapshot is null && index == 1)
        {
            snapshot = TryImportLatestSharedOsqSnapshot(runtime, snapshotHost);
        }

        if (snapshot is null)
        {
            await ReplyAsync(runtime, eventPayload, $"No server status #{index}.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, BuildOsqPlayersMessage(snapshot), cancellationToken);
    }

    private async Task HandleServerStartCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, targetSelector);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage))
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        if (profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "未解析到可启动的服务器档案。", cancellationToken);
            return;
        }

        if (_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, $"服务器已在运行：{profile.Name}", cancellationToken);
            return;
        }

        var launchableProfile = await EnsureLaunchableProfileAsync(profile, profile.ActiveSaveFile, cancellationToken);
        await _serverProcessService.StartAsync(launchableProfile, cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已启动服务器：{launchableProfile.Name}", cancellationToken);
    }

    private async Task HandleServerStopCommandAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector,
        CancellationToken cancellationToken)
    {
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Super admin only.", cancellationToken);
            return;
        }

        var targetResolution = ResolveServerCommandProfile(runtime, eventPayload, targetSelector);
        if (!string.IsNullOrWhiteSpace(targetResolution.ErrorMessage))
        {
            await ReplyAsync(runtime, eventPayload, targetResolution.ErrorMessage, cancellationToken);
            return;
        }

        if (targetResolution.Profile is null)
        {
            await ReplyAsync(runtime, eventPayload, "未解析到可停止的服务器档案。", cancellationToken);
            return;
        }

        var profile = targetResolution.Profile;
        if (!_serverProcessService.GetCurrentStatus(profile.Id).IsRunning)
        {
            await ReplyAsync(runtime, eventPayload, $"服务器档案未运行：{profile.Name}", cancellationToken);
            return;
        }

        await _serverProcessService.StopAsync(profile.Id, TimeSpan.FromSeconds(15), cancellationToken);
        await ReplyAsync(runtime, eventPayload, $"已停止服务器：{profile.Name}", cancellationToken);
    }

    private RobotServerCommandTargetResolution ResolveServerCommandProfile(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string targetSelector)
    {
        var preferences = _launcherPreferencesService.Load();
        var fallbackProfileIds = SplitProfileIds(
            preferences.AutoStartServerProfileIds,
            preferences.AutoStartServerProfileId);
        if (fallbackProfileIds.Count == 0)
        {
            fallbackProfileIds = SplitProfileIds(
                preferences.DefaultLaunchProfileIds,
                preferences.DefaultLaunchProfileId);
        }

        var groupId = IsGroupMessage(eventPayload)
            ? GetInt64(eventPayload, "group_id")
            : 0;
        return RobotServerCommandTargetResolver.Resolve(
            _instanceProfileService.GetProfiles(),
            fallbackProfileIds,
            runtime.CommandScope,
            GetInt64(eventPayload, "user_id"),
            groupId > 0 ? groupId : null,
            targetSelector);
    }

    private static HashSet<string> SplitProfileIds(IEnumerable<string>? values, string legacyValue = "")
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in values ?? [])
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        foreach (var value in (legacyValue ?? string.Empty).Split(
                     [';', ',', '|'],
                     StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                result.Add(value.Trim());
            }
        }

        return result;
    }

    private static string ResolveBoundSnapshotHostForGroup(Vs2QQRuntimeContext runtime, long groupId)
    {
        var profileId = runtime.GetPrimaryProfileIdForGroup(groupId);
        if (!string.IsNullOrWhiteSpace(profileId))
        {
            return BuildLocalProfileSnapshotHost(profileId);
        }

        return runtime.BoundGroupIds.Contains(groupId) && !runtime.HasProfileBindings
            ? LocalServerSnapshotHost
            : string.Empty;
    }

    private async Task SendToGameServerAsync(Vs2QQRuntimeContext runtime, long groupId, string message, CancellationToken cancellationToken)
    {
        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            return;
        }

        var outbound = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(outbound))
        {
            return;
        }

        var boundProfileIds = runtime.CommandScope.GetProfileIdsForGroup(groupId);
        if (boundProfileIds.Count > 1)
        {
            EmitOutput($"[warn] 群绑定了多个服务器档案，已拒绝转发群消息 group={groupId}");
            return;
        }

        if (boundProfileIds.Count == 0 && runtime.HasProfileBindings)
        {
            return;
        }

        var boundProfileId = boundProfileIds.Count == 1 ? boundProfileIds[0] : string.Empty;

        Exception? lastError = null;
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (string.IsNullOrWhiteSpace(boundProfileId))
                {
                    _serverProcessService.GetCurrentStatus();
                    await _serverTransport.SendGroupMessageToServerAsync(groupId, outbound, cancellationToken);
                }
                else
                {
                    await _serverProcessService.SendCommandAsync(boundProfileId, $"/announce {outbound}", cancellationToken);
                }
                if (attempt > 1)
                {
                    EmitOutput($"[vs2qq] 群消息补发成功 group={groupId}");
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
                EmitOutput($"[warn] 群消息转发到服务器失败 group={groupId} attempt={attempt}: {ex.Message}");
                if (attempt < 2)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                }
            }
        }

        throw new InvalidOperationException($"群消息转发到服务器失败 group={groupId}", lastError);
    }

    private static bool TryBuildOutboundGroupMessage(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string rawMessage, out string outboundMessage)
    {
        outboundMessage = string.Empty;
        if (!IsGroupMessage(eventPayload))
        {
            return false;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        if (groupId <= 0)
        {
            return false;
        }

        if (!runtime.BoundGroupIds.Contains(groupId))
        {
            return false;
        }

        var senderName = GetSenderDisplayName(eventPayload);
        var plain = NormalizeOutboundText(rawMessage);
        if (string.IsNullOrWhiteSpace(plain))
        {
            return false;
        }

        if (IsServerRelayEchoText(plain))
        {
            return false;
        }

        var timeLabel = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        outboundMessage = $"[群聊 {timeLabel}]{Safe(senderName)}：{plain}";
        return true;
    }

    private static string NormalizeOutboundText(string rawMessage)
    {
        var text = NormalizeDisplayText(rawMessage);
        text = CqImageRegex.Replace(text, "[图片]");
        text = CqCodeRegex.Replace(text, "[消息]");
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = MultiWhitespaceRegex.Replace(text, " ");
        text = SanitizeOutboundMentionText(text);
        return text;
    }

    private static string GetSenderDisplayName(JsonObject eventPayload)
    {
        if (eventPayload["sender"] is JsonObject senderObject)
        {
            var card = GetString(senderObject, "card");
            if (!string.IsNullOrWhiteSpace(card))
            {
                return card;
            }

            var nickname = GetString(senderObject, "nickname");
            if (!string.IsNullOrWhiteSpace(nickname))
            {
                return nickname;
            }
        }

        var name = GetString(eventPayload, "sender_name");
        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        return GetString(eventPayload, "nickname");
    }

    private async Task<string> ExtractPlainTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        CancellationToken cancellationToken)
    {
        if (eventPayload.TryGetPropertyValue("message", out var messageNode) && messageNode is not null)
        {
            var segmentText = await ExtractOneBotMessageNodeTextAsync(runtime, eventPayload, messageNode, cancellationToken);
            if (!string.IsNullOrWhiteSpace(segmentText))
            {
                return NormalizeOutboundText(segmentText);
            }
        }

        var message = GetString(eventPayload, "raw_message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            var expandedMessage = await ExpandCqAtSegmentsAsync(runtime, eventPayload, message, cancellationToken);
            return NormalizeOutboundText(expandedMessage);
        }

        var fallbackMessage = await ExpandCqAtSegmentsAsync(
            runtime,
            eventPayload,
            GetString(eventPayload, "message"),
            cancellationToken);
        return NormalizeOutboundText(fallbackMessage);
    }

    private async Task<string> ExtractOneBotMessageNodeTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        JsonNode messageNode,
        CancellationToken cancellationToken)
    {
        if (messageNode is JsonValue valueNode && valueNode.TryGetValue<string>(out var textValue))
        {
            return await ExpandCqAtSegmentsAsync(runtime, eventPayload, textValue, cancellationToken);
        }

        if (messageNode is not JsonArray segments)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        foreach (var segment in segments.OfType<JsonObject>())
        {
            var type = GetString(segment, "type").Trim().ToLowerInvariant();
            var data = segment["data"] as JsonObject;
            switch (type)
            {
                case "text":
                    parts.Add(data is null ? string.Empty : GetString(data, "text"));
                    break;
                case "image":
                case "mface":
                case "face":
                case "marketface":
                    parts.Add("[图片]");
                    break;
                case "at":
                    parts.Add(await FormatAtSegmentTextAsync(runtime, eventPayload, data, cancellationToken));
                    break;
                case "record":
                    parts.Add("[语音]");
                    break;
                case "video":
                    parts.Add("[视频]");
                    break;
                case "file":
                    parts.Add("[文件]");
                    break;
                case "reply":
                    break;
                default:
                    if (!string.IsNullOrWhiteSpace(type))
                    {
                        parts.Add("[消息]");
                    }
                    break;
            }
        }

        return string.Concat(parts);
    }

    private async Task<string> ExpandCqAtSegmentsAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        string text,
        CancellationToken cancellationToken)
    {
        var source = text ?? string.Empty;
        var matches = CqAtRegex.Matches(source);
        if (matches.Count == 0)
        {
            return source;
        }

        var result = new StringBuilder(source.Length);
        var position = 0;
        foreach (Match match in matches)
        {
            result.Append(source, position, match.Index - position);
            var qqMatch = CqAtQqParameterRegex.Match(match.Groups["params"].Value);
            if (!qqMatch.Success)
            {
                result.Append("@用户");
            }
            else
            {
                var data = new JsonObject
                {
                    ["qq"] = WebUtility.HtmlDecode(qqMatch.Groups["qq"].Value)
                };
                result.Append(await FormatAtSegmentTextAsync(runtime, eventPayload, data, cancellationToken));
            }

            position = match.Index + match.Length;
        }

        result.Append(source, position, source.Length - position);
        return result.ToString();
    }

    private async Task<string> FormatAtSegmentTextAsync(
        Vs2QQRuntimeContext runtime,
        JsonObject eventPayload,
        JsonObject? data,
        CancellationToken cancellationToken)
    {
        if (data is null)
        {
            return "@用户";
        }

        var qq = GetString(data, "qq");
        if (string.Equals(qq, "all", StringComparison.OrdinalIgnoreCase))
        {
            return "@全体成员";
        }

        var displayName = GetSafeAtDisplayName(data, qq);
        if (string.IsNullOrWhiteSpace(displayName)
            && long.TryParse(qq, NumberStyles.Integer, CultureInfo.InvariantCulture, out var userId)
            && userId > 0)
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                try
                {
                    displayName = await runtime.OneBot.GetGroupMemberDisplayNameAsync(
                        groupId,
                        userId,
                        cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    EmitOutput($"[warn] OneBot 提及昵称查询失败 group={groupId}: {ex.Message}");
                }
            }
        }

        return string.IsNullOrWhiteSpace(displayName)
            ? "@用户"
            : "@" + Safe(displayName);
    }

    private static string GetSafeAtDisplayName(JsonObject data, string qq)
    {
        var displayName = NormalizeSafeAtDisplayName(GetString(data, "card"), qq);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        displayName = NormalizeSafeAtDisplayName(GetString(data, "name"), qq);
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            return displayName;
        }

        return NormalizeSafeAtDisplayName(GetString(data, "nickname"), qq);
    }

    private static string NormalizeSafeAtDisplayName(string candidate, string qq)
    {
        var displayName = NormalizeDisplayText(candidate).Trim();
        if (string.IsNullOrWhiteSpace(displayName))
        {
            return string.Empty;
        }

        if (IsQqIdentifierText(displayName))
        {
            return string.Empty;
        }

        var normalizedCandidate = displayName.TrimStart('@').Trim();
        if (string.IsNullOrWhiteSpace(normalizedCandidate))
        {
            return string.Empty;
        }

        var normalizedQq = (qq ?? string.Empty).Trim().TrimStart('@').Trim();
        return !string.IsNullOrWhiteSpace(normalizedQq) &&
               string.Equals(normalizedCandidate, normalizedQq, StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : normalizedCandidate;
    }

    private static bool IsQqIdentifierText(string value)
    {
        var text = (value ?? string.Empty).Trim().TrimStart('@').Trim();
        return QqIdentifierRegex.IsMatch(text);
    }

    private static string SanitizeOutboundMentionText(string text)
    {
        return QqNumberMentionRegex.Replace(text ?? string.Empty, "@用户");
    }

    private async Task ReplyAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string message, CancellationToken cancellationToken)
    {
        if (IsGroupMessage(eventPayload))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            if (groupId > 0)
            {
                await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken);
                return;
            }
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId > 0)
        {
            await runtime.OneBot.SendPrivateMsgAsync(userId, message, cancellationToken);
        }
    }

    private static bool IsGroupMessage(JsonObject eventPayload)
    {
        return string.Equals(GetString(eventPayload, "message_type"), "group", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdminPermission(Vs2QQRuntimeContext runtime, JsonObject eventPayload)
    {
        return runtime.CommandScope.IsAdmin(GetInt64(eventPayload, "user_id"));
    }

    private void OnServerProfileOutputReceived(Vs2QQRuntimeContext runtime, ServerOutputLine output)
    {
        if (!ExperimentalFeatures.AntiCheatEnabled)
            return;

        if (!AntiCheatAlertRelay.TryBuildMessage(output, out var message))
            return;
        var messages = SplitOneBotMessages([message]);

        _ = Task.Run(async () =>
        {
            try
            {
                if (!ReferenceEquals(_runtime, runtime))
                    return;

                var groups = runtime.GetBoundGroupIdsForProfile(output.ProfileId);
                foreach (var groupId in groups)
                {
                    foreach (var part in messages)
                    {
                        await runtime.OneBot.SendGroupMsgAsync(groupId, part, CancellationToken.None);
                    }
                }
            }
            catch (Exception ex)
            {
                EmitOutput(
                    $"[warn] 反作弊告警转发失败 profile={output.ProfileId}: {ex.Message}");
            }
        }, CancellationToken.None);
    }

    private static string BuildHelpText()
    {
        return """
            VS2QQ Commands
            /help - 帮助
            /send <server_command> - 发送服务端指令（仅超级管理员）
            /server status [n] - 获取最近第 n 次服务器状态（默认1）
            /server players [n] - 获取最近第 n 次在线玩家列表（默认1）
            /server start [档案名或ID] - 启动指定或唯一绑定的服务器档案（仅超级管理员）
            /server stop [档案名或ID] - 停止指定或唯一绑定的服务器档案（仅超级管理员）
            /server password get - 获取服务器密码
            /server password set <new_password> - 修改服务器密码（- 表示清空，仅超级管理员）
            """;
    }

    private async Task<InstanceProfile> EnsureLaunchableProfileAsync(
        InstanceProfile profile,
        string preferredSavePath,
        CancellationToken cancellationToken)
    {
        var normalizedPreferredSavePath = NormalizeFullPath(preferredSavePath);
        if (!string.IsNullOrWhiteSpace(normalizedPreferredSavePath))
        {
            var saves = await _instanceSaveService.GetSavesAsync(profile, cancellationToken);
            var preferredSave = saves.FirstOrDefault(save =>
                NormalizeFullPath(save.FullPath).Equals(normalizedPreferredSavePath, StringComparison.OrdinalIgnoreCase));
            if (preferredSave is not null)
            {
                await PrepareProfileSaveForLaunchAsync(profile, preferredSave.FullPath, cancellationToken);
                return _instanceProfileService.GetProfileById(profile.Id) ?? profile;
            }
        }

        var currentSavePath = NormalizeFullPath(profile.ActiveSaveFile);
        if (string.IsNullOrWhiteSpace(currentSavePath))
        {
            currentSavePath = NormalizeFullPath(_instanceProfileService.GetDefaultSaveFilePath(profile.Id));
        }

        if (!string.IsNullOrWhiteSpace(currentSavePath))
        {
            await PrepareProfileSaveForLaunchAsync(profile, currentSavePath, cancellationToken);
        }

        return _instanceProfileService.GetProfileById(profile.Id) ?? profile;
    }

    private async Task PrepareProfileSaveForLaunchAsync(
        InstanceProfile profile,
        string savePath,
        CancellationToken cancellationToken)
    {
        var normalizedSavePath = NormalizeFullPath(savePath);
        if (string.IsNullOrWhiteSpace(normalizedSavePath))
        {
            return;
        }

        if (File.Exists(normalizedSavePath))
        {
            var fileInfo = new FileInfo(normalizedSavePath);
            if (fileInfo.Length == 0)
            {
                File.Delete(normalizedSavePath);
            }
        }

        await _instanceSaveService.SetActiveSaveAsync(profile, normalizedSavePath, cancellationToken);
    }

    private static string NormalizeFullPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeServerCommand(string? value)
    {
        var text = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = string.Join(' ', text.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries));
        return text.StartsWith('/') ? text : "/" + text;
    }

    private static string GetString(JsonObject obj, string key)
    {
        return obj.TryGetPropertyValue(key, out var node) && node is not null
            ? node.ToString()
            : string.Empty;
    }

    private static long GetInt64(JsonObject obj, string key, long fallback = 0)
    {
        if (!obj.TryGetPropertyValue(key, out var node) || node is null)
        {
            return fallback;
        }

        if (node is JsonValue valueNode)
        {
            if (valueNode.TryGetValue<long>(out var longValue))
            {
                return longValue;
            }

            if (valueNode.TryGetValue<int>(out var intValue))
            {
                return intValue;
            }
        }

        return long.TryParse(node.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private void EmitOutput(string message)
    {
        OutputReceived?.Invoke(this, message);
    }

    private async Task ForwardOsqSnapshotAsync(Vs2QQRuntimeContext runtime, string host, OsqSnapshotEnvelope payload, CancellationToken cancellationToken)
    {
        var forwardGate = runtime.GetOsqForwardGate(host);
        await forwardGate.WaitAsync(cancellationToken);
        try
        {
            var groups = runtime.GetBoundGroupIdsForSnapshotHost(host).ToList();
            if (groups.Count == 0)
            {
                return;
            }

            var chats = payload.RecentChats ?? [];
            var events = payload.PlayerEvents ?? [];
            var notifications = payload.ServerNotifications ?? [];
            foreach (var groupId in groups)
            {
                var forwardState = runtime.Storage.GetOsqForwardState(host, groupId);
                var skipCurrentSnapshotWhenNoState = forwardState is null;

                var newChatLines = CollectNewChatLines(
                    runtime,
                    groupId,
                    chats,
                    forwardState?.LastChatSignature,
                    skipCurrentSnapshotWhenNoState,
                    out var lastChatSignature);
                var newEventLines = CollectNewEventLines(
                    runtime,
                    groupId,
                    events,
                    forwardState?.LastEventSignature,
                    skipCurrentSnapshotWhenNoState,
                    out var lastEventSignature);
                var newNotificationLines = CollectNewNotificationLines(
                    runtime,
                    groupId,
                    notifications,
                    forwardState?.LastNotificationSignature,
                    skipCurrentSnapshotWhenNoState,
                    out var lastNotificationSignature);

                if (newChatLines.Count == 0 && newEventLines.Count == 0 && newNotificationLines.Count == 0)
                {
                    runtime.Storage.UpsertOsqForwardState(host, groupId, lastChatSignature, lastEventSignature, lastNotificationSignature);
                    continue;
                }

                var lines = new List<string>();
                lines.AddRange(newEventLines);
                lines.AddRange(newNotificationLines);
                lines.AddRange(newChatLines);
                var messages = SplitOneBotMessages(lines);
                try
                {
                    foreach (var message in messages)
                    {
                        await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken);
                    }
                    runtime.Storage.UpsertOsqForwardState(host, groupId, lastChatSignature, lastEventSignature, lastNotificationSignature);
                }
                catch (Exception ex)
                {
                    EmitOutput($"[warn] OSQ 转发失败 host={host} group={groupId}: {ex.Message}");
                }
            }
        }
        finally
        {
            forwardGate.Release();
        }
    }

    private static IReadOnlyList<string> SplitOneBotMessages(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0)
        {
            return [];
        }

        var result = new List<string>();
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            var safeLine = line ?? string.Empty;
            if (builder.Length > 0 &&
                builder.Length + Environment.NewLine.Length + safeLine.Length > MaxOneBotMessageLength)
            {
                result.Add(builder.ToString());
                builder.Clear();
            }

            if (safeLine.Length > MaxOneBotMessageLength)
            {
                if (builder.Length > 0)
                {
                    result.Add(builder.ToString());
                    builder.Clear();
                }

                for (var offset = 0; offset < safeLine.Length; offset += MaxOneBotMessageLength)
                {
                    result.Add(safeLine.Substring(offset, Math.Min(MaxOneBotMessageLength, safeLine.Length - offset)));
                }
                continue;
            }

            if (builder.Length > 0)
            {
                builder.AppendLine();
            }
            builder.Append(safeLine);
        }

        if (builder.Length > 0)
        {
            result.Add(builder.ToString());
        }

        return result;
    }

    private static string BuildOsqSummaryMessage(OsqSnapshotEnvelope payload)
    {
        var server = payload.Server ?? new OsqServerInfo();
        var players = payload.Players ?? [];
        var events = payload.PlayerEvents ?? [];
        var chats = payload.RecentChats ?? [];
        var onlinePlayers = players
            .Where(p => p.IsOnline && !string.IsNullOrWhiteSpace(p.PlayerName))
            .Select(p => Safe(p.PlayerName))
            .Where(name => name != "-")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var onlinePlayerCount = onlinePlayers.Count > 0
            ? onlinePlayers.Count
            : Math.Max(0, server.OnlinePlayerCount > 0 ? server.OnlinePlayerCount : server.PlayerCount);
        var timeLabel = FormatDisplayTime(payload.TimestampUtc);

        var lines = new List<string>
        {
            $"[服务器状态 {timeLabel}]",
            $"服务器：{Safe(server.Name)}",
            $"状态：{FormatOsqServerStatus(server.Status)}",
            $"版本：{Safe(server.Version)}",
            $"人数：{onlinePlayerCount}/{server.MaxPlayers}",
            $"世界：{Safe(server.WorldName)}",
            $"地址：{Safe(server.ServerIp)}:{server.ServerPort}"
        };

        if (onlinePlayers.Count > 0)
        {
            lines.Add("玩家：" + string.Join("、", onlinePlayers));
        }

        if (events.Count > 0)
        {
            var topEvents = events.TakeLast(3)
                .Select(e => $"{Safe(e.PlayerName)}-{Safe(e.EventType)}-{Safe(e.ConnectionState)}");
            lines.Add("连接事件：" + string.Join("；", topEvents));
        }

        if (chats.Count > 0)
        {
            var topChats = chats
                .Where(c => !ServerLogPrivacyFilter.ShouldSuppressRelayParts(c.SenderName, c.Message))
                .TakeLast(3)
                .Select(c => $"{Safe(c.SenderName)}: {Safe(NormalizeInboundServerText(c.SenderName, c.Message))}")
                .ToList();
            if (topChats.Count > 0)
            {
                lines.Add("聊天：" + string.Join(" | ", topChats));
            }
        }

        return string.Join('\n', lines.Where(line => !string.IsNullOrWhiteSpace(line)));
    }

    private static string BuildOsqPlayersMessage(OsqSnapshotEnvelope payload)
    {
        var server = payload.Server ?? new OsqServerInfo();
        var players = payload.Players ?? [];
        var onlinePlayers = players
            .Where(p => p.IsOnline)
            .OrderBy(p => p.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var timeLabel = FormatDisplayTime(payload.TimestampUtc);
        var lines = new List<string>
        {
            $"[在线玩家 {timeLabel}] {onlinePlayers.Count}/{server.MaxPlayers}"
        };

        if (onlinePlayers.Count == 0)
        {
            lines.Add("当前无在线玩家。");
            return string.Join('\n', lines);
        }

        foreach (var player in onlinePlayers)
        {
            var latency = player.PingMs.HasValue ? $"{player.PingMs.Value}ms/{Safe(player.DelayLevel)}" : Safe(player.DelayLevel);
            lines.Add($"- {Safe(player.PlayerName)} ({Safe(player.ConnectionState)}, {latency})");
        }

        return string.Join('\n', lines);
    }

    private static IReadOnlyList<string> CollectNewChatLines(
        Vs2QQRuntimeContext runtime,
        long groupId,
        IReadOnlyList<OsqChatInfo> chats,
        string? previousSignature,
        bool skipWhenNoPreviousSignature,
        out string? lastSignature)
    {
        var signatures = chats
            .Select(c => BuildChatSignature(c))
            .ToList();

        lastSignature = signatures.Count == 0 ? previousSignature : signatures[^1];
        var startIndex = ResolveNewItemsStartIndex(signatures, previousSignature, skipWhenNoPreviousSignature);
        if (startIndex >= signatures.Count)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = startIndex; i < chats.Count; i++)
        {
            var chat = chats[i];
            var sender = Safe(chat.SenderName);
            var content = NormalizeInboundServerText(chat.SenderName, chat.Message);
            if (IsGroupRelayEchoText(sender)
                || IsGroupRelayEchoText(content)
                || ServerLogPrivacyFilter.ShouldSuppressRelayParts(sender, content))
            {
                continue;
            }
            var timeLabel = FormatDisplayTime(chat.TimestampUtc);
            var line = $"[服务器 {timeLabel}]{sender}：{Safe(content)}";
            if (ShouldSkipRecentRelaySignature(runtime, groupId, BuildRelaySignature("chat", signatures[i]), chat.TimestampUtc))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static IReadOnlyList<string> CollectNewEventLines(
        Vs2QQRuntimeContext runtime,
        long groupId,
        IReadOnlyList<OsqPlayerEventInfo> events,
        string? previousSignature,
        bool skipWhenNoPreviousSignature,
        out string? lastSignature)
    {
        var signatures = events
            .Select(e => BuildEventSignature(e))
            .ToList();

        lastSignature = signatures.Count == 0 ? previousSignature : signatures[^1];
        var startIndex = ResolveNewItemsStartIndex(signatures, previousSignature, skipWhenNoPreviousSignature);
        if (startIndex >= signatures.Count)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = startIndex; i < events.Count; i++)
        {
            var entry = events[i];
            var mapped = MapJoinLeaveText(entry.EventType);
            if (mapped is null)
            {
                continue;
            }

            var playerName = Safe(entry.PlayerName);
            var timeLabel = FormatDisplayTime(entry.TimestampUtc);
            var line = $"[服务器 {timeLabel}]{playerName} {mapped}";
            if (ShouldSkipRecentRelaySignature(runtime, groupId, BuildRelaySignature("event", signatures[i]), entry.TimestampUtc))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static IReadOnlyList<string> CollectNewNotificationLines(
        Vs2QQRuntimeContext runtime,
        long groupId,
        IReadOnlyList<OsqServerNotificationInfo> notifications,
        string? previousSignature,
        bool skipWhenNoPreviousSignature,
        out string? lastSignature)
    {
        var signatures = notifications
            .Select(n => BuildNotificationSignature(n))
            .ToList();

        lastSignature = signatures.Count == 0 ? previousSignature : signatures[^1];
        var startIndex = ResolveNewItemsStartIndex(signatures, previousSignature, skipWhenNoPreviousSignature);
        if (startIndex >= signatures.Count)
        {
            return [];
        }

        var result = new List<string>();
        for (var i = startIndex; i < notifications.Count; i++)
        {
            var notification = notifications[i];
            var content = NormalizeInboundServerText(null, notification.Message);
            if (IsGroupRelayEchoText(content) || ServerLogPrivacyFilter.ShouldSuppressRelayParts(content))
            {
                continue;
            }
            var timeLabel = FormatDisplayTime(notification.TimestampUtc);
            var line = $"[服务器 {timeLabel}]{Safe(content)}";
            if (ShouldSkipRecentRelaySignature(runtime, groupId, BuildRelaySignature("notification", signatures[i]), notification.TimestampUtc))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static int ResolveNewItemsStartIndex(
        IReadOnlyList<string> signatures,
        string? previousSignature,
        bool skipWhenNoPreviousSignature)
    {
        if (signatures.Count == 0)
        {
            return 0;
        }

        if (string.IsNullOrWhiteSpace(previousSignature))
        {
            return skipWhenNoPreviousSignature ? signatures.Count : 0;
        }

        for (var i = signatures.Count - 1; i >= 0; i--)
        {
            if (string.Equals(signatures[i], previousSignature, StringComparison.Ordinal))
            {
                return i + 1;
            }
        }

        // If the previous marker cannot be found, prefer silence over replaying
        // stale history into QQ groups.
        return signatures.Count;
    }

    private static string BuildChatSignature(OsqChatInfo chat)
    {
        return $"{Safe(chat.TimestampUtc)}|{Safe(chat.SenderName)}|{Safe(NormalizeDisplayText(chat.Message))}";
    }

    private static string BuildEventSignature(OsqPlayerEventInfo entry)
    {
        return $"{Safe(entry.TimestampUtc)}|{Safe(entry.EventType)}|{Safe(entry.PlayerName)}|{Safe(entry.ConnectionState)}";
    }

    private static string BuildNotificationSignature(OsqServerNotificationInfo notification)
    {
        return $"{Safe(notification.TimestampUtc)}|{Safe(NormalizeDisplayText(notification.Message))}";
    }

    private static string? MapJoinLeaveText(string? eventType)
    {
        var normalized = (eventType ?? string.Empty).Trim().ToLowerInvariant();
        return normalized switch
        {
            "join" => "进入服务器",
            "leave" => "离开服务器",
            "disconnect" => "离开服务器",
            "death" => "死亡",
            "die" => "死亡",
            "dead" => "死亡",
            _ => null
        };
    }

    private static string BuildRelaySignature(string category, string itemSignature)
    {
        return $"{category}|{itemSignature}";
    }

    private static bool ShouldSkipRecentRelaySignature(
        Vs2QQRuntimeContext runtime,
        long groupId,
        string signature,
        string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(signature))
        {
            return false;
        }

        var eventTime = ParseRelayEventTime(timestamp);
        if (!eventTime.HasValue)
        {
            return false;
        }

        var key = $"{groupId}|{signature}";
        lock (runtime.RecentRelaySignatures)
        {
            PruneRecentRelaySignatures(runtime.RecentRelaySignatures, eventTime.Value);
            if (runtime.RecentRelaySignatures.TryGetValue(key, out var previous)
                && (eventTime.Value - previous).Duration() <= RecentRelaySignatureWindow)
            {
                if (eventTime.Value > previous)
                {
                    runtime.RecentRelaySignatures[key] = eventTime.Value;
                }

                return true;
            }

            runtime.RecentRelaySignatures[key] = eventTime.Value;
            return false;
        }
    }

    private static void PruneRecentRelaySignatures(Dictionary<string, DateTimeOffset> recentRelaySignatures, DateTimeOffset now)
    {
        foreach (var item in recentRelaySignatures.ToArray())
        {
            if ((now - item.Value).Duration() > RecentRelaySignatureWindow)
            {
                recentRelaySignatures.Remove(item.Key);
            }
        }
    }

    private static DateTimeOffset? ParseRelayEventTime(string? timestamp)
    {
        if (string.IsNullOrWhiteSpace(timestamp))
        {
            return DateTimeOffset.UtcNow;
        }

        if (DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeLocal,
                out var parsed))
        {
            return parsed.ToUniversalTime();
        }

        if (TimeSpan.TryParseExact(
                timestamp.Trim(),
                @"hh\:mm\:ss",
                CultureInfo.InvariantCulture,
                out var timeOfDay))
        {
            var localDate = DateTimeOffset.Now.Date;
            return new DateTimeOffset(localDate + timeOfDay, TimeZoneInfo.Local.GetUtcOffset(localDate + timeOfDay))
                .ToUniversalTime();
        }

        return DateTimeOffset.UtcNow;
    }

    private static bool IsGroupRelayEchoText(string? text)
    {
        var normalized = NormalizeDisplayText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return GroupRelayEchoRegex.IsMatch(normalized);
    }

    private static bool IsServerRelayEchoText(string? text)
    {
        var normalized = NormalizeDisplayText(text);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        return ServerRelayEchoRegex.IsMatch(normalized);
    }

    private OsqSnapshotEnvelope? TryImportLatestSharedOsqSnapshot(Vs2QQRuntimeContext runtime, string boundHost)
    {
        foreach (var candidate in BuildServerHostCandidates(boundHost))
        {
            var cachedPayload = _osqSnapshotCacheService.GetLatestPayload(candidate);
            if (cachedPayload is null)
            {
                continue;
            }

            var payload = DeserializeOsqSnapshot(cachedPayload);
            if (payload?.Server is null)
            {
                continue;
            }

            runtime.Storage.AddOsqSnapshot(boundHost, payload);
            return payload;
        }

        return null;
    }

    private static string ResolveBoundServerHostForSnapshot(
        Vs2QQRuntimeContext runtime,
        string reportedHost)
    {
        foreach (var candidate in BuildServerHostCandidates(reportedHost))
        {
            if (runtime.HasBoundGroupsForSnapshotHost(candidate))
            {
                return candidate;
            }
        }

        var normalized = NormalizeServerHost(reportedHost);
        return string.IsNullOrWhiteSpace(normalized)
            ? (reportedHost ?? string.Empty).Trim()
            : normalized;
    }

    private static OsqSnapshotEnvelope? DeserializeOsqSnapshot(JsonObject payload)
    {
        try
        {
            return JsonSerializer.Deserialize<OsqSnapshotEnvelope>(
                payload.ToJsonString(),
                OsqJsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static IReadOnlyList<string> BuildServerHostCandidates(string? host)
    {
        var result = new List<string>();
        AddServerHostCandidate(result, host);

        var raw = (host ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            AddServerHostCandidate(result, LocalServerSnapshotHost);
            return result;
        }

        if (IsLocalSnapshotHost(raw))
        {
            AddServerHostCandidate(result, NormalizeLocalSnapshotHost(raw));
            return result;
        }

        AddServerHostCandidate(result, NormalizeServerHost(raw));
        if (raw.Contains("://", StringComparison.Ordinal) &&
            Uri.TryCreate(raw.EndsWith('/') ? raw : raw + '/', UriKind.Absolute, out var uri))
        {
            var authority = uri.IsDefaultPort ? uri.Host.ToLowerInvariant() : $"{uri.Host.ToLowerInvariant()}:{uri.Port}";
            AddServerHostCandidate(result, authority);
            AddServerHostCandidate(result, $"{uri.Scheme.ToLowerInvariant()}://{authority}");
            var path = uri.AbsolutePath.TrimEnd('/');
            if (!string.IsNullOrWhiteSpace(path) && path != "/")
            {
                AddServerHostCandidate(result, $"{uri.Scheme.ToLowerInvariant()}://{authority}{path}");
            }
        }
        else
        {
            AddServerHostCandidate(result, "https://" + raw.TrimEnd('/').ToLowerInvariant());
            AddServerHostCandidate(result, "http://" + raw.TrimEnd('/').ToLowerInvariant());
        }

        return result;
    }

    private static void AddServerHostCandidate(List<string> candidates, string? host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('/');
        if (value.Length == 0 ||
            candidates.Any(existing => existing.Equals(value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        candidates.Add(value);
    }

    private static string BuildLocalProfileSnapshotHost(string profileId)
    {
        var normalized = (profileId ?? string.Empty).Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? LocalServerSnapshotHost
            : LocalProfileSnapshotHostPrefix + normalized;
    }

    private static bool IsLocalSnapshotHost(string? host)
    {
        var value = (host ?? string.Empty).Trim();
        return value.Equals(LocalServerSnapshotHost, StringComparison.OrdinalIgnoreCase)
            || value.StartsWith(LocalProfileSnapshotHostPrefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeLocalSnapshotHost(string host)
    {
        var value = (host ?? string.Empty).Trim().TrimEnd('/');
        if (!value.StartsWith(LocalProfileSnapshotHostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return LocalServerSnapshotHost;
        }

        var profileId = value[LocalProfileSnapshotHostPrefix.Length..].Trim();
        return BuildLocalProfileSnapshotHost(profileId);
    }

    private static bool TryGetLocalSnapshotProfileId(string? host, out string profileId)
    {
        var value = (host ?? string.Empty).Trim();
        if (!value.StartsWith(LocalProfileSnapshotHostPrefix, StringComparison.OrdinalIgnoreCase))
        {
            profileId = string.Empty;
            return false;
        }

        profileId = value[LocalProfileSnapshotHostPrefix.Length..].Trim();
        return !string.IsNullOrWhiteSpace(profileId);
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
            raw = "https://" + raw;
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            return raw.ToLowerInvariant();
        }

        var host = uri.Host.ToLowerInvariant();
        var port = uri.IsDefaultPort ? string.Empty : $":{uri.Port}";
        return host + port;
    }

    private static string NormalizeInboundServerText(string? senderName, string? rawText)
    {
        var text = NormalizeDisplayText(rawText);

        if (!string.IsNullOrWhiteSpace(senderName) && !string.IsNullOrWhiteSpace(text))
        {
            var escapedSender = Regex.Escape(senderName.Trim());
            text = Regex.Replace(
                text,
                $"^{escapedSender}\\s*[:：]\\s*",
                string.Empty,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            text = text.Trim();
        }

        return text;
    }

    private static string NormalizeDisplayText(string? rawText)
    {
        var text = WebUtility.HtmlDecode(rawText ?? string.Empty);
        text = HtmlTagRegex.Replace(text, string.Empty);
        text = CqImageRegex.Replace(text, "[图片]");
        text = CqCodeRegex.Replace(text, "[消息]");
        text = text.Replace('\r', ' ').Replace('\n', ' ').Trim();
        text = MultiWhitespaceRegex.Replace(text, " ");
        return text;
    }

    private static string FormatDisplayTime(string? rawTimestamp)
    {
        if (!string.IsNullOrWhiteSpace(rawTimestamp))
        {
            var value = rawTimestamp.Trim();

            if (HasExplicitTimeZone(value) &&
                DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var offsetParsed))
            {
                return offsetParsed.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                    out var parsed))
            {
                return parsed.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            }

            var match = TimePartRegex.Match(value);
            if (match.Success)
            {
                return match.Groups["time"].Value;
            }
        }

        return DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
    }

    private static bool HasExplicitTimeZone(string value)
    {
        var trimmed = value.Trim();
        return trimmed.EndsWith('Z')
               || trimmed.EndsWith('z')
               || Regex.IsMatch(trimmed, @"[+-]\d{2}:?\d{2}$", RegexOptions.CultureInvariant);
    }

    private static string FormatOsqServerStatus(string? status)
    {
        var normalized = Safe(status);
        return normalized.ToLowerInvariant() switch
        {
            "rungame" => "运行中",
            "running" => "运行中",
            "run" => "运行中",
            "standby" => "待机",
            "starting" => "启动中",
            "stopping" => "停止中",
            "stopped" => "已停止",
            "offline" => "离线",
            _ => normalized
        };
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();
    }

    private static OperationResult<RobotSettings> NormalizeLaunchSettings(RobotSettings settings)
    {
        var wsUrl = (settings.OneBotWsUrl ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(wsUrl))
        {
            return OperationResult<RobotSettings>.Failed("缺少 OneBot WebSocket 地址。");
        }

        if (!Uri.TryCreate(wsUrl, UriKind.Absolute, out var wsUri)
            || (wsUri.Scheme != "ws" && wsUri.Scheme != "wss"))
        {
            return OperationResult<RobotSettings>.Failed("OneBot WebSocket 地址格式无效，必须是 ws:// 或 wss://。");
        }

        var dbPath = string.IsNullOrWhiteSpace(settings.DatabasePath)
            ? Path.Combine(WorkspacePathHelper.WorkspaceRoot, "vs2qq", "vs2qq.db")
            : settings.DatabasePath.Trim();
        if (!Path.IsPathRooted(dbPath))
        {
            dbPath = Path.Combine(WorkspacePathHelper.WorkspaceRoot, dbPath);
        }
        dbPath = Path.GetFullPath(dbPath);

        var reconnectInterval = settings.ReconnectIntervalSec <= 0 ? 5 : settings.ReconnectIntervalSec;
        var pollInterval = settings.PollIntervalSec <= 0 ? 1.0 : settings.PollIntervalSec;
        var defaultEncoding = string.IsNullOrWhiteSpace(settings.DefaultEncoding) ? "utf-8" : settings.DefaultEncoding.Trim();
        var fallbackEncoding = string.IsNullOrWhiteSpace(settings.FallbackEncoding) ? "gbk" : settings.FallbackEncoding.Trim();
        var osqListenPrefix = NormalizeListenPrefix(settings.OsqListenPrefix);
        var normalizedSuperUsers = (settings.SuperUsers ?? [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        var normalizedBoundGroupIds = (settings.BoundGroupIds ?? [])
            .Where(x => x > 0)
            .Distinct()
            .ToList();
        var normalizedProfileBindings = NormalizeProfileBindings(settings.ProfileBindings);
        foreach (var groupId in normalizedProfileBindings
                     .Select(static binding => ParsePositiveInt64(binding.GroupId))
                     .Where(static id => id > 0))
        {
            if (!normalizedBoundGroupIds.Contains(groupId))
            {
                normalizedBoundGroupIds.Add(groupId);
            }
        }

        foreach (var superUserId in normalizedProfileBindings
                     .Select(static binding => ParsePositiveInt64(binding.SuperUserId))
                     .Where(static id => id > 0))
        {
            if (!normalizedSuperUsers.Contains(superUserId))
            {
                normalizedSuperUsers.Add(superUserId);
            }
        }

        return OperationResult<RobotSettings>.Success(new RobotSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? null : settings.AccessToken.Trim(),
            BoundGroupIds = normalizedBoundGroupIds,
            ProfileBindings = normalizedProfileBindings,
            ReconnectIntervalSec = reconnectInterval,
            DatabasePath = dbPath,
            PollIntervalSec = pollInterval,
            DefaultEncoding = defaultEncoding,
            FallbackEncoding = fallbackEncoding,
            SuperUsers = normalizedSuperUsers,
            OsqPollIntervalSec = settings.OsqPollIntervalSec <= 0 ? 20 : settings.OsqPollIntervalSec,
            OsqRequestTimeoutSec = settings.OsqRequestTimeoutSec <= 0 ? 8 : settings.OsqRequestTimeoutSec,
            OsqAllowInsecureHttp = settings.OsqAllowInsecureHttp,
            OsqListenPrefix = osqListenPrefix,
            EnableOsqListener = false
        });
    }

    private static List<RobotProfileBinding> NormalizeProfileBindings(IEnumerable<RobotProfileBinding>? bindings)
    {
        var result = new List<RobotProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding.ProfileId?.Trim() ?? string.Empty;
            var groupId = ParsePositiveInt64(binding.GroupId);
            var superUserId = ParsePositiveInt64(binding.SuperUserId);
            if (string.IsNullOrWhiteSpace(profileId) && groupId <= 0 && superUserId <= 0)
            {
                continue;
            }

            var normalizedGroupId = groupId > 0 ? groupId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var normalizedSuperUserId = superUserId > 0 ? superUserId.ToString(CultureInfo.InvariantCulture) : string.Empty;
            var key = $"{profileId}|{normalizedGroupId}|{normalizedSuperUserId}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new RobotProfileBinding
            {
                ProfileId = profileId,
                GroupId = normalizedGroupId,
                SuperUserId = normalizedSuperUserId
            });
        }

        return result;
    }

    private static long ParsePositiveInt64(string? value)
    {
        return long.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && id > 0
            ? id
            : 0;
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:18089/"
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
            return "http://127.0.0.1:18089/";
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return "http://127.0.0.1:18089/";
        }

        if (string.IsNullOrWhiteSpace(uri.Host))
        {
            return "http://127.0.0.1:18089/";
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
        var prefix = value.Trim();
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

    private sealed class Vs2QQRuntimeContext : IAsyncDisposable
    {
        private int _disposedFlag;

        public Vs2QQRuntimeContext(
            RobotSettings settings,
            Vs2QQStorage storage)
        {
            Settings = settings;
            Storage = storage;
            BoundGroupIds = settings.BoundGroupIds?.Where(id => id > 0).ToHashSet() ?? [];
            CommandScope = new RobotCommandScope(settings.SuperUsers, BoundGroupIds, settings.ProfileBindings);
            ProfileBindings = BuildRuntimeProfileBindings(settings.ProfileBindings);
            GroupsByProfileId = BuildGroupsByProfileId(ProfileBindings);
        }

        public RobotSettings Settings { get; }

        public HashSet<long> BoundGroupIds { get; }

        public RobotCommandScope CommandScope { get; }

        public IReadOnlyList<Vs2QQProfileBinding> ProfileBindings { get; }

        public bool HasProfileBindings => ProfileBindings.Any(static binding => binding.GroupId > 0);

        private IReadOnlyDictionary<string, HashSet<long>> GroupsByProfileId { get; }

        public Vs2QQStorage Storage { get; }

        public Vs2QQOneBotClient OneBot { get; set; } = null!;

        public EventHandler<OsqSnapshotReceivedEventArgs>? OsqSnapshotHandler { get; set; }

        public EventHandler<ServerOutputLine>? ServerOutputHandler { get; set; }

        public Dictionary<string, DateTimeOffset> RecentRelaySignatures { get; } = new(StringComparer.OrdinalIgnoreCase);

        private readonly ConcurrentDictionary<string, SemaphoreSlim> _osqForwardGates = new(StringComparer.OrdinalIgnoreCase);

        public SemaphoreSlim GetOsqForwardGate(string host)
        {
            var key = string.IsNullOrWhiteSpace(host) ? "local" : host.Trim();
            return _osqForwardGates.GetOrAdd(key, _ => new SemaphoreSlim(1, 1));
        }

        public IReadOnlyList<long> GetBoundGroupIdsForSnapshotHost(string host)
        {
            if (TryGetLocalSnapshotProfileId(host, out var profileId))
            {
                return GetBoundGroupIdsForProfile(profileId);
            }

            if (!HasProfileBindings)
            {
                return BoundGroupIds.ToList();
            }

            return [];
        }

        public bool HasBoundGroupsForSnapshotHost(string host)
        {
            return GetBoundGroupIdsForSnapshotHost(host).Count > 0;
        }

        public string GetPrimaryProfileIdForGroup(long groupId)
        {
            var profiles = CommandScope.GetProfileIdsForGroup(groupId);
            return profiles.Count == 1 ? profiles[0] : string.Empty;
        }

        public IReadOnlyList<long> GetBoundGroupIdsForProfile(string profileId)
        {
            return GroupsByProfileId.TryGetValue(profileId, out var groups)
                ? groups.ToList()
                : [];
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
            {
                return;
            }

            await OneBot.DisposeAsync();
            foreach (var gate in _osqForwardGates.Values)
            {
                gate.Dispose();
            }

            Storage.Dispose();
        }

        private static IReadOnlyList<Vs2QQProfileBinding> BuildRuntimeProfileBindings(IEnumerable<RobotProfileBinding>? bindings)
        {
            var result = new List<Vs2QQProfileBinding>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings ?? [])
            {
                var profileId = binding.ProfileId?.Trim() ?? string.Empty;
                var groupId = ParsePositiveInt64(binding.GroupId);
                var superUserId = ParsePositiveInt64(binding.SuperUserId);
                if (string.IsNullOrWhiteSpace(profileId) ||
                    (groupId <= 0 && superUserId <= 0))
                {
                    continue;
                }

                var key = $"{profileId}|{groupId}|{superUserId}";
                if (!seen.Add(key))
                {
                    continue;
                }

                result.Add(new Vs2QQProfileBinding(profileId, groupId, superUserId));
            }

            return result;
        }

        private static IReadOnlyDictionary<string, HashSet<long>> BuildGroupsByProfileId(IEnumerable<Vs2QQProfileBinding> bindings)
        {
            var result = new Dictionary<string, HashSet<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var binding in bindings)
            {
                if (binding.GroupId <= 0)
                {
                    continue;
                }

                if (!result.TryGetValue(binding.ProfileId, out var groups))
                {
                    groups = [];
                    result[binding.ProfileId] = groups;
                }

                groups.Add(binding.GroupId);
            }

            return result;
        }
    }

    private readonly record struct Vs2QQProfileBinding(string ProfileId, long GroupId, long SuperUserId);

    private readonly record struct GroupMemberDisplayNameCacheEntry(string DisplayName, DateTimeOffset ExpiresAtUtc);

    private sealed class Vs2QQOneBotClient : IAsyncDisposable
    {
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            WriteIndented = false
        };

        private readonly Uri _wsUri;
        private readonly string? _accessToken;
        private readonly int _reconnectIntervalSec;
        private readonly Action<string> _log;
        private readonly Func<JsonObject, CancellationToken, Task> _eventHandler;
        private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject>> _echoWaiters = new();
        private readonly ConcurrentDictionary<(long GroupId, long UserId), GroupMemberDisplayNameCacheEntry> _groupMemberDisplayNames = new();
        private readonly SemaphoreSlim _sendGate = new(1, 1);
        private readonly object _socketGate = new();
        private ClientWebSocket? _socket;

        public Vs2QQOneBotClient(
            string wsUrl,
            string? accessToken,
            int reconnectIntervalSec,
            Action<string> log,
            Func<JsonObject, CancellationToken, Task> eventHandler)
        {
            _wsUri = new Uri(wsUrl, UriKind.Absolute);
            _accessToken = accessToken;
            _reconnectIntervalSec = reconnectIntervalSec;
            _log = log;
            _eventHandler = eventHandler;
        }

        public async Task RunForeverAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var socket = new ClientWebSocket();
                if (!string.IsNullOrWhiteSpace(_accessToken))
                {
                    socket.Options.SetRequestHeader("Authorization", $"Bearer {_accessToken}");
                }

                try
                {
                    _log($"[onebot] Connecting {_wsUri} ...");
                    await socket.ConnectAsync(_wsUri, cancellationToken);
                    SetSocket(socket);
                    _log("[onebot] Connected.");
                    await ConsumeMessagesAsync(socket, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _log($"[onebot] Disconnected: {ex.Message}");
                }
                finally
                {
                    SetSocket(null);
                    FailPendingWaiters(new InvalidOperationException("OneBot connection closed."));
                }

                await Task.Delay(TimeSpan.FromSeconds(_reconnectIntervalSec), cancellationToken);
            }
        }

        public async Task SendGroupMsgAsync(long groupId, string message, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["group_id"] = groupId,
                ["message"] = message
            };

            await CallActionAsync("send_group_msg", parameters, TimeSpan.FromSeconds(20), cancellationToken);
        }

        public async Task SendPrivateMsgAsync(long userId, string message, CancellationToken cancellationToken)
        {
            var parameters = new JsonObject
            {
                ["user_id"] = userId,
                ["message"] = message
            };

            await CallActionAsync("send_private_msg", parameters, TimeSpan.FromSeconds(20), cancellationToken);
        }

        public async Task<string> GetGroupMemberDisplayNameAsync(
            long groupId,
            long userId,
            CancellationToken cancellationToken)
        {
            var cacheKey = (groupId, userId);
            if (_groupMemberDisplayNames.TryGetValue(cacheKey, out var cached))
            {
                if (cached.ExpiresAtUtc > DateTimeOffset.UtcNow)
                {
                    return cached.DisplayName;
                }

                _groupMemberDisplayNames.TryRemove(cacheKey, out _);
            }

            var parameters = new JsonObject
            {
                ["group_id"] = groupId,
                ["user_id"] = userId,
                ["no_cache"] = false
            };
            var data = await CallActionAsync(
                "get_group_member_info",
                parameters,
                TimeSpan.FromSeconds(5),
                cancellationToken);
            if (data is not JsonObject memberInfo)
            {
                return string.Empty;
            }

            var displayName = GetSafeAtDisplayName(
                memberInfo,
                userId.ToString(CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(displayName))
            {
                _groupMemberDisplayNames[cacheKey] = new GroupMemberDisplayNameCacheEntry(
                    displayName,
                    DateTimeOffset.UtcNow.Add(GroupMemberDisplayNameCacheDuration));
            }

            return displayName;
        }

        public async Task<JsonNode?> CallActionAsync(
            string action,
            JsonObject parameters,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            var echo = Guid.NewGuid().ToString("N");
            var waiter = new TaskCompletionSource<JsonObject>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!_echoWaiters.TryAdd(echo, waiter))
            {
                throw new InvalidOperationException("Cannot create action waiter.");
            }

            try
            {
                var payload = new JsonObject
                {
                    ["action"] = action,
                    ["params"] = parameters,
                    ["echo"] = echo
                };

                await SendTextAsync(payload.ToJsonString(JsonOptions), cancellationToken);

                var delayTask = Task.Delay(timeout, cancellationToken);
                var completed = await Task.WhenAny(waiter.Task, delayTask);
                cancellationToken.ThrowIfCancellationRequested();
                if (!ReferenceEquals(completed, waiter.Task))
                {
                    throw new TimeoutException(
                        $"OneBot action timeout: {action}. " +
                        "未收到动作回包，请检查 OneBot WS 地址/AccessToken/协议版本是否匹配。");
                }

                var response = await waiter.Task;
                var status = response["status"]?.ToString();
                if (!string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase))
                {
                    var retCode = response["retcode"]?.ToString();
                    var msg = response["msg"]?.ToString();
                    throw new InvalidOperationException($"OneBot action failed: action={action}, retcode={retCode}, msg={msg}");
                }

                return response["data"];
            }
            finally
            {
                _echoWaiters.TryRemove(echo, out _);
            }
        }

        public async ValueTask DisposeAsync()
        {
            SetSocket(null);
            FailPendingWaiters(new OperationCanceledException("OneBot client disposed."));

            ClientWebSocket? snapshot;
            lock (_socketGate)
            {
                snapshot = _socket;
                _socket = null;
            }

            if (snapshot is not null)
            {
                try
                {
                    if (snapshot.State is WebSocketState.Open or WebSocketState.CloseReceived)
                    {
                        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await snapshot.CloseAsync(WebSocketCloseStatus.NormalClosure, "dispose", cts.Token);
                    }
                }
                catch
                {
                    // Ignore shutdown errors.
                }
                finally
                {
                    snapshot.Dispose();
                }
            }
        }

        private void SetSocket(ClientWebSocket? socket)
        {
            lock (_socketGate)
            {
                _socket = socket;
            }
        }

        private ClientWebSocket? GetSocket()
        {
            lock (_socketGate)
            {
                return _socket;
            }
        }

        private async Task SendTextAsync(string text, CancellationToken cancellationToken)
        {
            var socket = GetSocket();
            if (socket is null || socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("OneBot is not connected.");
            }

            await _sendGate.WaitAsync(cancellationToken);
            try
            {
                var bytes = Encoding.UTF8.GetBytes(text);
                await socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Text,
                    endOfMessage: true,
                    cancellationToken);
            }
            finally
            {
                _sendGate.Release();
            }
        }

        private async Task ConsumeMessagesAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var text = await ReceiveTextAsync(socket, cancellationToken);
                if (text is null)
                {
                    break;
                }

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(text);
                }
                catch
                {
                    continue;
                }

                if (node is not JsonObject payload)
                {
                    continue;
                }

                var echoValue = payload["echo"]?.ToString();
                if (!string.IsNullOrWhiteSpace(echoValue)
                    && _echoWaiters.TryGetValue(echoValue, out var waiter))
                {
                    waiter.TrySetResult(payload);
                    continue;
                }

                if (payload["post_type"] is not null)
                {
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await _eventHandler(payload, cancellationToken);
                        }
                        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                        {
                            // Normal shutdown.
                        }
                        catch (Exception ex)
                        {
                            _log($"[warn] OneBot 事件处理异常: {ex.Message}");
                        }
                    }, cancellationToken);
                }
            }
        }

        private static async Task<string?> ReceiveTextAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[8 * 1024];
            using var stream = new MemoryStream();

            while (true)
            {
                var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    try
                    {
                        if (socket.State == WebSocketState.CloseReceived)
                        {
                            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "close-received", cancellationToken);
                        }
                    }
                    catch
                    {
                        // Ignore close errors.
                    }

                    return null;
                }

                if (result.Count > 0)
                {
                    await stream.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                }

                if (result.EndOfMessage)
                {
                    break;
                }
            }

            if (stream.Length == 0)
            {
                return string.Empty;
            }

            return Encoding.UTF8.GetString(stream.ToArray());
        }

        private void FailPendingWaiters(Exception exception)
        {
            foreach (var item in _echoWaiters.Values)
            {
                item.TrySetException(exception);
            }

            _echoWaiters.Clear();
        }
    }

    private sealed class Vs2QQStorage : IDisposable
    {
        private readonly object _sync = new();
        private readonly SqliteConnection _connection;
        private bool _disposed;
        private const string LegacyOsqSnapshotsTable = "osq_snapshots";
        private const string LegacyOsqForwardStateTable = "osq_forward_state";
        private const string OsqSnapshotsTable = "osq_snapshots_v2";
        private const string OsqForwardStateTable = "osq_forward_state_v2";

        public Vs2QQStorage(string dbPath)
        {
            var directory = Path.GetDirectoryName(dbPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            _connection = new SqliteConnection($"Data Source={dbPath}");
            _connection.Open();
            using (var pragma = _connection.CreateCommand())
            {
                pragma.CommandText = "PRAGMA foreign_keys = ON;";
                pragma.ExecuteNonQuery();
            }
            InitializeSchema();
        }

        public void Dispose()
        {
            lock (_sync)
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                _connection.Dispose();
            }
        }

        public void AddOsqSnapshot(string serverHost, OsqSnapshotEnvelope payload)
        {
            lock (_sync)
            {
                using (var command = _connection.CreateCommand())
                {
                    command.CommandText =
                        """
                        INSERT INTO osq_snapshots_v2 (server_host, payload_json, created_at)
                        VALUES ($serverHost, $payloadJson, $createdAt);
                        """;
                    command.Parameters.AddWithValue("$serverHost", serverHost);
                    command.Parameters.AddWithValue("$payloadJson", JsonSerializer.Serialize(payload, OsqJsonOptions));
                    command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                    command.ExecuteNonQuery();
                }

                using (var cleanup = _connection.CreateCommand())
                {
                    cleanup.CommandText =
                        """
                        DELETE FROM osq_snapshots_v2
                        WHERE server_host = $serverHost
                          AND snapshot_id NOT IN (
                              SELECT snapshot_id
                              FROM osq_snapshots_v2
                              WHERE server_host = $serverHost
                              ORDER BY snapshot_id DESC
                              LIMIT $maxRows
                          );
                        """;
                    cleanup.Parameters.AddWithValue("$serverHost", serverHost);
                    cleanup.Parameters.AddWithValue("$maxRows", MaxOsqStatusHistoryPerHost);
                    cleanup.ExecuteNonQuery();
                }
            }
        }

        public OsqSnapshotEnvelope? GetLatestOsqSnapshot(string serverHost, int index)
        {
            lock (_sync)
            {
                return ReadLatestOsqSnapshotLocked(serverHost, index);
            }
        }

        private OsqSnapshotEnvelope? ReadLatestOsqSnapshotLocked(string serverHost, int index)
        {
            if (index <= 0)
            {
                return null;
            }

            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT payload_json
                FROM osq_snapshots_v2
                WHERE server_host = $serverHost
                ORDER BY snapshot_id DESC
                LIMIT 1 OFFSET $offset;
                """;
            command.Parameters.AddWithValue("$serverHost", serverHost);
            command.Parameters.AddWithValue("$offset", index - 1);
            var value = command.ExecuteScalar();
            if (value is null || value == DBNull.Value)
            {
                return null;
            }

            try
            {
                return JsonSerializer.Deserialize<OsqSnapshotEnvelope>(value.ToString() ?? string.Empty, OsqJsonOptions);
            }
            catch
            {
                return null;
            }
        }

        public OsqForwardState? GetOsqForwardState(string serverHost, long groupId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT last_chat_signature, last_event_signature, last_notification_signature
                    FROM osq_forward_state_v2
                    WHERE server_host = $serverHost AND group_id = $groupId
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
                command.Parameters.AddWithValue("$groupId", groupId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                return new OsqForwardState(
                    reader.IsDBNull(0) ? null : reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.FieldCount > 2 && !reader.IsDBNull(2) ? reader.GetString(2) : null);
            }
        }

        public void UpsertOsqForwardState(string serverHost, long groupId, string? lastChatSignature, string? lastEventSignature, string? lastNotificationSignature)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO osq_forward_state_v2 (server_host, group_id, last_chat_signature, last_event_signature, last_notification_signature, updated_at)
                    VALUES ($serverHost, $groupId, $lastChatSignature, $lastEventSignature, $lastNotificationSignature, $updatedAt)
                    ON CONFLICT(server_host, group_id) DO UPDATE SET
                        last_chat_signature = excluded.last_chat_signature,
                        last_event_signature = excluded.last_event_signature,
                        last_notification_signature = excluded.last_notification_signature,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
                command.Parameters.AddWithValue("$groupId", groupId);
                command.Parameters.AddWithValue("$lastChatSignature", (object?)lastChatSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$lastEventSignature", (object?)lastEventSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$lastNotificationSignature", (object?)lastNotificationSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        private void InitializeSchema()
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    CREATE TABLE IF NOT EXISTS osq_snapshots_v2 (
                        snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_host TEXT NOT NULL,
                        payload_json TEXT NOT NULL,
                        created_at TEXT NOT NULL
                    );

                    CREATE INDEX IF NOT EXISTS idx_osq_snapshots_host_id
                        ON osq_snapshots_v2 (server_host, snapshot_id DESC);

                    CREATE TABLE IF NOT EXISTS osq_forward_state_v2 (
                        server_host TEXT NOT NULL,
                        group_id INTEGER NOT NULL,
                        last_chat_signature TEXT,
                        last_event_signature TEXT,
                        last_notification_signature TEXT,
                        updated_at TEXT NOT NULL,
                        PRIMARY KEY (server_host, group_id)
                    );
                    """;
                command.ExecuteNonQuery();
            }

            MigrateLegacySchema();
        }

        private static string GetUtcNowIso()
        {
            return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void MigrateLegacySchema()
        {
            lock (_sync)
            {
                if (!TableExists(LegacyOsqSnapshotsTable) && !TableExists(LegacyOsqForwardStateTable))
                {
                    return;
                }

                using var transaction = _connection.BeginTransaction();

                if (TableExists(LegacyOsqSnapshotsTable))
                {
                    using var copySnapshots = _connection.CreateCommand();
                    copySnapshots.Transaction = transaction;
                    copySnapshots.CommandText =
                        $"""
                        INSERT INTO {OsqSnapshotsTable} (server_host, payload_json, created_at)
                        SELECT legacy.server_host, legacy.payload_json, legacy.created_at
                        FROM {LegacyOsqSnapshotsTable} legacy
                        WHERE NOT EXISTS (
                            SELECT 1
                            FROM {OsqSnapshotsTable} current
                            WHERE current.server_host = legacy.server_host
                              AND current.payload_json = legacy.payload_json
                              AND current.created_at = legacy.created_at
                        );
                        """;
                    copySnapshots.ExecuteNonQuery();
                }

                if (TableExists(LegacyOsqForwardStateTable))
                {
                    var columns = GetTableColumns(LegacyOsqForwardStateTable);
                    var hasGroupId = columns.Contains("group_id");
                    var hasLastNotificationSignature = columns.Contains("last_notification_signature");
                    var lastNotificationProjection = hasLastNotificationSignature
                        ? "legacy.last_notification_signature"
                        : "NULL";
                    var groupIdProjection = hasGroupId
                        ? "COALESCE(legacy.group_id, 0)"
                        : "0";

                    using var copyForwardState = _connection.CreateCommand();
                    copyForwardState.Transaction = transaction;
                    copyForwardState.CommandText =
                        $"""
                        INSERT INTO {OsqForwardStateTable} (
                            server_host,
                            group_id,
                            last_chat_signature,
                            last_event_signature,
                            last_notification_signature,
                            updated_at
                        )
                        SELECT
                            legacy.server_host,
                            {groupIdProjection},
                            legacy.last_chat_signature,
                            legacy.last_event_signature,
                            {lastNotificationProjection},
                            legacy.updated_at
                        FROM {LegacyOsqForwardStateTable} legacy
                        ON CONFLICT(server_host, group_id) DO UPDATE SET
                            last_chat_signature = excluded.last_chat_signature,
                            last_event_signature = excluded.last_event_signature,
                            last_notification_signature = excluded.last_notification_signature,
                            updated_at = excluded.updated_at;
                        """;
                    copyForwardState.ExecuteNonQuery();
                }

                DropLegacyTable(transaction, "group_remote_servers");
                DropLegacyTable(transaction, "osq_replay_nonce");
                DropLegacyTable(transaction, "remote_servers");
                DropLegacyTable(transaction, LegacyOsqSnapshotsTable);
                DropLegacyTable(transaction, LegacyOsqForwardStateTable);

                transaction.Commit();
            }
        }

        private void DropLegacyTable(SqliteTransaction transaction, string tableName)
        {
            if (!TableExists(tableName))
            {
                return;
            }

            using var drop = _connection.CreateCommand();
            drop.Transaction = transaction;
            drop.CommandText = $"DROP TABLE IF EXISTS {tableName};";
            drop.ExecuteNonQuery();
        }

        private bool TableExists(string tableName)
        {
            using var command = _connection.CreateCommand();
            command.CommandText =
                """
                SELECT 1
                FROM sqlite_master
                WHERE type = 'table' AND name = $tableName
                LIMIT 1;
                """;
            command.Parameters.AddWithValue("$tableName", tableName);
            var value = command.ExecuteScalar();
            return value is not null && value != DBNull.Value;
        }

        private HashSet<string> GetTableColumns(string tableName)
        {
            var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                columns.Add(reader.GetString(1));
            }

            return columns;
        }

    }

    private readonly record struct OsqForwardState(string? LastChatSignature, string? LastEventSignature, string? LastNotificationSignature);

    private sealed class OsqSnapshotEnvelope
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public int SchemaVersion { get; set; }

        public List<string>? Capabilities { get; set; }

        public string SourceHost { get; set; } = string.Empty;

        public string ReceivedAtUtc { get; set; } = string.Empty;

        public OsqServerInfo? Server { get; set; }

        public List<OsqPlayerInfo>? Players { get; set; }

        public List<OsqPlayerEventInfo>? PlayerEvents { get; set; }

        public List<OsqChatInfo>? RecentChats { get; set; }

        public List<OsqServerNotificationInfo>? ServerNotifications { get; set; }

        public JsonElement? ServerMap { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OsqServerInfo
    {
        public string Name { get; set; } = string.Empty;

        public string Version { get; set; } = string.Empty;

        public string Status { get; set; } = string.Empty;

        public int PlayerCount { get; set; }

        public int OnlinePlayerCount { get; set; }

        public int MaxPlayers { get; set; }

        public string ServerIp { get; set; } = string.Empty;

        public int ServerPort { get; set; }

        public string WorldName { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OsqPlayerInfo
    {
        public string PlayerUid { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

        public bool IsOnline { get; set; }

        public string ConnectionState { get; set; } = string.Empty;

        public int? PingMs { get; set; }

        public string DelayLevel { get; set; } = string.Empty;

        public string LastSeenUtc { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OsqPlayerEventInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public string EventType { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

        public string ConnectionState { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OsqChatInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public string SenderName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class OsqServerNotificationInfo
    {
        public string TimestampUtc { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

}
