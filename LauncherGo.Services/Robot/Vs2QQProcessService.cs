using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed class Vs2QQProcessService
{
    private static int _encodingProviderRegistered;
    private static readonly JsonSerializerOptions OsqJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions RobotJsonOptions = new()
    {
        WriteIndented = false
    };

    private static readonly JsonSerializerOptions ServerAuthJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private const int MaxOsqStatusHistoryPerHost = 30;
    private const int MaxServerStatusQueryCount = 10;
    private const int MaxOneBotMessageLength = 1800;
    private const int MaxOsqRequestBodyBytes = 8 * 1024 * 1024;
    private const int ServerAuthMinPasswordLength = 6;
    private const int ServerAuthMaxPasswordLength = 128;
    private const int RequiredQqBindingVersion = 2;
    private static readonly TimeSpan PlayerJoinRelayDedupeWindow = TimeSpan.FromSeconds(30);

    private static readonly Regex NonceRegex = new("^[a-z0-9]{8,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex OsqBindServerPattern = new(@"^(\S+)\s+(\S+)\s+(\d{5,20})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CqImageRegex = new(@"\[CQ:image,[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex CqCodeRegex = new(@"\[CQ:[^\]]+\]", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex LocalJoinLeaveRegex = new(@"^玩家\s+(?<player>.+?)\s+(?<action>加入了服务器|进入服务器|离开了服务器|离开服务器)$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex HtmlTagRegex = new(@"<[^>]+>", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex MultiWhitespaceRegex = new(@"\s{2,}", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex TimePartRegex = new(@"(?<time>\d{2}:\d{2}:\d{2})", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex GroupRelayEchoRegex = new(@"^\[(?:群聊|group)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ServerRelayEchoRegex = new(@"^\[(?:服务器|server)\s+\d{1,2}:\d{2}:\d{2}\]", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private readonly SemaphoreSlim _runtimeGate = new(1, 1);
    private readonly IServerProcessService _serverProcessService;
    private readonly IInstanceProfileService _instanceProfileService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IOsqSnapshotCacheService _osqSnapshotCacheService;
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private Vs2QQRuntimeContext? _runtime;

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<RobotRuntimeStatus>? StatusChanged;

    public RobotRuntimeStatus CurrentStatus { get; private set; } = new();

    public Vs2QQProcessService(
        IServerProcessService serverProcessService,
        IInstanceProfileService instanceProfileService,
        IInstanceServerConfigService instanceServerConfigService,
        IOsqSnapshotCacheService osqSnapshotCacheService)
    {
        _serverProcessService = serverProcessService;
        _instanceProfileService = instanceProfileService;
        _instanceServerConfigService = instanceServerConfigService;
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
            var parser = new Vs2QQTalkLineParser();
            var tailer = new Vs2QQLogTailer(
                storage,
                parser,
                normalized.DefaultEncoding,
                normalized.FallbackEncoding,
                EmitOutput);

            Vs2QQRuntimeContext runtime = new(normalized, storage, tailer);
            var oneBot = new Vs2QQOneBotClient(
                normalized.OneBotWsUrl,
                normalized.AccessToken,
                normalized.ReconnectIntervalSec,
                EmitOutput,
                (eventPayload, token) => HandleOneBotEventAsync(runtime, eventPayload, token));
            runtime.OneBot = oneBot;
            runtime.OsqSnapshotHandler = (_, args) => OnSharedOsqSnapshotReceived(runtime, args);
            _osqSnapshotCacheService.SnapshotReceived += runtime.OsqSnapshotHandler;

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
            runtime?.OsqListener?.Close();
        }
        catch
        {
            // ignore shutdown errors.
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
            var oneBotTask = runtime.OneBot.RunForeverAsync(cancellationToken);
            var pollTask = PollLogsLoopAsync(runtime, cancellationToken);
            await Task.WhenAll(oneBotTask, pollTask);
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
        await runtime.DisposeAsync();

        if (shouldNotifyStopped)
        {
            StatusChanged?.Invoke(this, CurrentStatus);
            EmitOutput("[system] VS2QQ 已停止。");
        }
    }

    private async Task PollLogsLoopAsync(Vs2QQRuntimeContext runtime, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await PollLogsOnceAsync(runtime, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] VS2QQ 日志轮询异常: {ex.Message}");
            }

            await Task.Delay(TimeSpan.FromSeconds(runtime.Settings.PollIntervalSec), cancellationToken);
        }
    }

    private async Task PollLogsOnceAsync(Vs2QQRuntimeContext runtime, CancellationToken cancellationToken)
    {
        var servers = runtime.Storage.ListActiveServers();
        foreach (var server in servers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<Vs2QQTalkMessage> messages;
            try
            {
                messages = runtime.Tailer.PollServer(server);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] 轮询服务器日志失败 server={server.ServerId}: {ex.Message}");
                continue;
            }

            foreach (var message in messages)
            {
                await ForwardTalkMessageAsync(runtime, message, cancellationToken);
            }
        }
    }

    private async Task ForwardTalkMessageAsync(Vs2QQRuntimeContext runtime, Vs2QQTalkMessage talk, CancellationToken cancellationToken)
    {
        if (IsGroupRelayEchoText(talk.Sender) || IsGroupRelayEchoText(talk.Content))
        {
            EmitOutput($"[vs2qq] 已跳过群聊回环 server={talk.ServerId} sender={talk.Sender}");
            return;
        }

        if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(talk.Sender, talk.Content))
        {
            EmitOutput($"[vs2qq] 已跳过敏感服务器日志 server={talk.ServerId}");
            return;
        }

        var groups = runtime.Storage.ListGroupsForServer(talk.ServerId);
        if (groups.Count == 0)
        {
            return;
        }

        var timeLabel = FormatDisplayTime(talk.Timestamp);
        string payload;
        if (IsServerNotificationSender(talk.Sender))
        {
            var content = NormalizeDisplayText(talk.Content);
            payload = $"[服务器 {timeLabel}]{Safe(content)}";
        }
        else if (!TryBuildLocalJoinLeaveMessage(timeLabel, talk.Content, out payload))
        {
            var sender = Safe(talk.Sender);
            var content = NormalizeInboundServerText(talk.Sender, talk.Content);
            payload = $"[服务器 {timeLabel}]{sender}：{Safe(content)}";
        }

        if (ShouldSkipDuplicateJoinRelay(runtime, payload, talk.Timestamp))
        {
            EmitOutput($"[vs2qq] 已跳过重复进服消息 server={talk.ServerId}");
            return;
        }

        EmitOutput($"[vs2qq] 日志转发 server={talk.ServerId} groups={groups.Count} sender={talk.Sender}");

        foreach (var groupId in groups)
        {
            try
            {
                await runtime.OneBot.SendGroupMsgAsync(groupId, payload, cancellationToken);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] 发送群消息失败 group={groupId} server={talk.ServerId}: {ex.Message}");
            }
        }
    }

    private static bool TryBuildLocalJoinLeaveMessage(string timeLabel, string content, out string payload)
    {
        payload = string.Empty;
        var match = LocalJoinLeaveRegex.Match(NormalizeDisplayText(content));
        if (!match.Success)
        {
            return false;
        }

        var playerName = match.Groups["player"].Value.Trim();
        var action = match.Groups["action"].Value.Trim();
        if (string.IsNullOrWhiteSpace(playerName))
        {
            return false;
        }

        string normalizedAction = action switch
        {
            "加入了服务器" => "进入服务器",
            "进入服务器" => "进入服务器",
            "离开了服务器" => "离开服务器",
            "离开服务器" => "离开服务器",
            _ => string.Empty
        };

        if (string.IsNullOrWhiteSpace(normalizedAction))
        {
            return false;
        }

        payload = $"[服务器 {timeLabel}]{playerName} {normalizedAction}";
        return true;
    }

    private static bool IsServerNotificationSender(string? sender)
    {
        return string.Equals(sender?.Trim(), Vs2QQTalkLineParser.ServerNotificationSender, StringComparison.Ordinal);
    }

    private void OnSharedOsqSnapshotReceived(Vs2QQRuntimeContext runtime, OsqSnapshotReceivedEventArgs args)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var host = ResolveBoundRemoteServerHostForSnapshot(runtime, args.ServerHost, out var groups);
                if (groups.Count == 0)
                {
                    // 未绑定任何QQ群时忽略共享快照，避免无意义落库与外键告警刷屏。
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

        var rawMessage = ExtractPlainText(eventPayload).Trim();
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
            case "/bindqq":
                await HandleBindQqAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/passwd":
                await HandlePasswdAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/unbindqq":
                await HandleUnbindQqAsync(runtime, eventPayload, cancellationToken);
                return;
            case "/mybind":
                await HandleMyBindAsync(runtime, eventPayload, cancellationToken);
                return;
            case "/bindlogserver":
                await HandleBindServerAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/unbindlogserver":
                await HandleUnbindServerAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/listlogserver":
                await HandleListServerAsync(runtime, eventPayload, cancellationToken);
                return;
            case "/bindlogregex":
            case "/bindserverregex":
                await HandleBindServerRegexAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/bindserver":
            case "/bindhost":
            case "/bindremote":
            case "/绑定服务器":
                await HandleBindRemoteServerAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/unbindserver":
            case "/unbindhost":
            case "/unbindremote":
            case "/解绑服务器":
                await HandleUnbindRemoteServerAsync(runtime, eventPayload, args, cancellationToken);
                return;
            case "/listserver":
            case "/listhost":
            case "/listremote":
            case "/查看服务器":
                await HandleListRemoteServerAsync(runtime, eventPayload, cancellationToken);
                return;
            case "/server":
                await HandleServerCommandAsync(runtime, eventPayload, args, cancellationToken);
                return;
            default:
                await ReplyAsync(runtime, eventPayload, "Unknown command. Use /help.", cancellationToken);
                return;
        }
    }

    private async Task HandleBindQqAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        if (!IsPrivateMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请私聊机器人使用该命令。", cancellationToken);
            return;
        }

        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /bindqq <player_name> <password>. 中文：私聊绑定QQ到玩家名", cancellationToken);
            return;
        }

        var playerName = parts[0].Trim();
        var password = string.Join(' ', parts.Skip(1)).Trim();
        if (string.IsNullOrWhiteSpace(playerName) || string.IsNullOrWhiteSpace(password))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /bindqq <player_name> <password>. 中文：私聊绑定QQ到玩家名", cancellationToken);
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        if (!TryGetCurrentRunningProfile(out var profile, out var profileError))
        {
            await ReplyAsync(runtime, eventPayload, profileError, cancellationToken);
            return;
        }

        if (!TryLoadServerAuthSettings(profile, out var authSettings))
        {
            await ReplyAsync(runtime, eventPayload, "无法读取 ServerAuth 配置，请先在认证页保存一次。", cancellationToken);
            return;
        }

        if (authSettings.Discourse.Enabled)
        {
            await ReplyAsync(runtime, eventPayload, "当前档案启用了 Discourse-only 认证，密码绑定不可用。", cancellationToken);
            return;
        }

        if (!TryLoadServerAuthStore(profile, out var store, out var storeError))
        {
            await ReplyAsync(runtime, eventPayload, storeError, cancellationToken);
            return;
        }

        var player = FindServerAuthPlayer(store, playerName);
        if (player is null)
        {
            await ReplyAsync(runtime, eventPayload, $"未找到玩家：{playerName}", cancellationToken);
            return;
        }

        if (player.PasswordResetRequired || string.IsNullOrWhiteSpace(player.PasswordHash))
        {
            await ReplyAsync(runtime, eventPayload, "该玩家尚未设置可用密码，请先在游戏内完成 /register。", cancellationToken);
            return;
        }

        if (!ServerAuthPasswordHasher.Verify(password, player.PasswordHash))
        {
            await ReplyAsync(runtime, eventPayload, "密码错误，绑定失败。", cancellationToken);
            return;
        }

        runtime.Storage.BindQq(userId, player.PlayerName, RequiredQqBindingVersion);
        await ReplyAsync(runtime, eventPayload, $"绑定成功：QQ {userId} -> {player.PlayerName}", cancellationToken);
    }

    private async Task HandlePasswdAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        if (!IsPrivateMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请私聊机器人使用该命令。", cancellationToken);
            return;
        }

        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /passwd <old_password> <new_password> <confirm>. 中文：私聊修改密码", cancellationToken);
            return;
        }

        var oldPassword = parts[0].Trim();
        var newPassword = parts[1].Trim();
        var confirmPassword = string.Join(' ', parts.Skip(2)).Trim();
        if (string.IsNullOrWhiteSpace(oldPassword) ||
            string.IsNullOrWhiteSpace(newPassword) ||
            string.IsNullOrWhiteSpace(confirmPassword))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /passwd <old_password> <new_password> <confirm>. 中文：私聊修改密码", cancellationToken);
            return;
        }

        if (!newPassword.Equals(confirmPassword, StringComparison.Ordinal))
        {
            await ReplyAsync(runtime, eventPayload, "两次输入的新密码不一致。", cancellationToken);
            return;
        }

        if (newPassword.Length < ServerAuthMinPasswordLength || newPassword.Length > ServerAuthMaxPasswordLength)
        {
            await ReplyAsync(
                runtime,
                eventPayload,
                $"新密码长度必须在 {ServerAuthMinPasswordLength}-{ServerAuthMaxPasswordLength} 个字符之间。",
                cancellationToken);
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        var binding = runtime.Storage.GetQqBinding(userId);
        if (binding is null)
        {
            await ReplyAsync(runtime, eventPayload, "未检测到有效绑定，请先执行 /bindqq <player_name> <password>。", cancellationToken);
            return;
        }

        if (!TryGetCurrentRunningProfile(out var profile, out var profileError))
        {
            await ReplyAsync(runtime, eventPayload, profileError, cancellationToken);
            return;
        }

        if (!TryLoadServerAuthSettings(profile, out var authSettings))
        {
            await ReplyAsync(runtime, eventPayload, "无法读取 ServerAuth 配置，请先在认证页保存一次。", cancellationToken);
            return;
        }

        if (authSettings.Discourse.Enabled)
        {
            await ReplyAsync(runtime, eventPayload, "当前档案启用了 Discourse-only 认证，改密不可用。", cancellationToken);
            return;
        }

        if (!TryLoadServerAuthStore(profile, out var store, out var storeError))
        {
            await ReplyAsync(runtime, eventPayload, storeError, cancellationToken);
            return;
        }

        var player = FindServerAuthPlayer(store, binding.Value.PlayerName);
        if (player is null)
        {
            await ReplyAsync(runtime, eventPayload, $"未找到绑定玩家：{binding.Value.PlayerName}", cancellationToken);
            return;
        }

        if (player.PasswordResetRequired || string.IsNullOrWhiteSpace(player.PasswordHash))
        {
            await ReplyAsync(runtime, eventPayload, "该玩家当前无可校验密码，请先在游戏内重新注册。", cancellationToken);
            return;
        }

        if (!ServerAuthPasswordHasher.Verify(oldPassword, player.PasswordHash))
        {
            await ReplyAsync(runtime, eventPayload, "旧密码错误。", cancellationToken);
            return;
        }

        player.PasswordHash = ServerAuthPasswordHasher.Hash(newPassword);
        player.PasswordResetRequired = false;
        store.Sessions = store.Sessions
            .Where(session => !session.PlayerUid.Equals(player.PlayerUid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveServerAuthStore(profile, store);

        await ReplyAsync(runtime, eventPayload, "密码修改成功，下次进服需重新登录。", cancellationToken);
    }

    private async Task HandleUnbindQqAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        if (!IsPrivateMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请私聊机器人使用该命令。", cancellationToken);
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        var deleted = runtime.Storage.UnbindQq(userId);
        await ReplyAsync(runtime, eventPayload, deleted ? "Unbound current QQ." : "No QQ binding found.", cancellationToken);
    }

    private async Task HandleMyBindAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        if (!IsPrivateMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "请私聊机器人使用该命令。", cancellationToken);
            return;
        }

        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        var binding = runtime.Storage.GetQqBinding(userId);
        if (binding is null)
        {
            await ReplyAsync(runtime, eventPayload, "No QQ binding. Use /bindqq <player_name> <password>.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, $"Bound: QQ {binding.Value.QqId} -> {binding.Value.PlayerName}", cancellationToken);
    }

    private async Task HandleBindServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        var match = Regex.Match(args, @"^(\S+)\s+(.+)$");
        if (!match.Success)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /bindlogserver <server_id> <log_path>. 中文：绑定日志服务器", cancellationToken);
            return;
        }

        var serverId = match.Groups[1].Value.Trim();
        var rawLogPath = StripQuotes(match.Groups[2].Value.Trim());
        var logPath = ResolvePath(rawLogPath);
        var groupId = GetInt64(eventPayload, "group_id");

        runtime.Storage.UpsertServer(serverId, logPath);
        runtime.Storage.BindGroupServer(groupId, serverId);
        runtime.Tailer.PrimeServer(serverId, logPath);

        await ReplyAsync(
            runtime,
            eventPayload,
            $"Bound server: group {groupId} -> {serverId}\nlog: {logPath}",
            cancellationToken);
    }

    private async Task HandleUnbindServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }
        if (string.IsNullOrWhiteSpace(args))
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /unbindlogserver <server_id>. 中文：解绑日志服务器", cancellationToken);
            return;
        }

        var serverId = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        var groupId = GetInt64(eventPayload, "group_id");
        var deleted = runtime.Storage.UnbindGroupServer(groupId, serverId);

        await ReplyAsync(
            runtime,
            eventPayload,
            deleted
                ? $"Unbound server: {groupId} <-> {serverId}"
                : $"This group is not bound to {serverId}",
            cancellationToken);
    }

    private async Task HandleListServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }

        var groupId = GetInt64(eventPayload, "group_id");
        var servers = runtime.Storage.ListGroupServers(groupId);
        if (servers.Count == 0)
        {
            await ReplyAsync(runtime, eventPayload, "No bound log servers.", cancellationToken);
            return;
        }

        var lines = new List<string> { "Group servers:" };
        lines.AddRange(servers.Select(x => $"- {x.ServerId}: {x.LogPath}"));
        await ReplyAsync(runtime, eventPayload, string.Join('\n', lines), cancellationToken);
    }

    private async Task HandleBindServerRegexAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        if (!IsGroupMessage(eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Use in group chat.", cancellationToken);
            return;
        }
        if (!HasAdminPermission(runtime, eventPayload))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        var match = Regex.Match(args, @"^(\S+)\s+(.+)$");
        if (!match.Success)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /bindlogregex <server_id> <regex>. 中文：绑定日志正则", cancellationToken);
            return;
        }

        var serverId = match.Groups[1].Value.Trim();
        var regexValue = StripQuotes(match.Groups[2].Value.Trim());
        var ok = runtime.Storage.SetServerRegex(serverId, regexValue);
        if (!ok)
        {
            await ReplyAsync(runtime, eventPayload, $"Server not found: {serverId}", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, $"Updated regex for {serverId}.", cancellationToken);
    }

    private async Task HandleServerCommandAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /server status [n] | /server players [n] | /server password get | /server password set <new_password>", cancellationToken);
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

        await ReplyAsync(runtime, eventPayload, "Only /server status [n], /server players [n], and /server password get|set are supported.", cancellationToken);
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

        var host = runtime.Storage.FindRemoteServerHostByGroup(groupId);
        if (string.IsNullOrWhiteSpace(host))
        {
            await ReplyAsync(runtime, eventPayload, "This group has no remote server binding.", cancellationToken);
            return;
        }

        var snapshot = runtime.Storage.GetLatestOsqSnapshot(host, index);
        if (snapshot is null && index == 1)
        {
            snapshot = TryImportLatestSharedOsqSnapshot(runtime, host);
        }

        if (snapshot is null)
        {
            await ReplyAsync(runtime, eventPayload, $"No server status #{index} for {host}.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, BuildOsqSummaryMessage(host, snapshot), cancellationToken);
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
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        var status = _serverProcessService.GetCurrentStatus();
        if (string.IsNullOrWhiteSpace(status.ProfileId))
        {
            await ReplyAsync(runtime, eventPayload, "No local running profile. Password command only supports local bound server.", cancellationToken);
            return;
        }

        var profile = _instanceProfileService.GetProfileById(status.ProfileId);
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

        var host = runtime.Storage.FindRemoteServerHostByGroup(groupId);
        if (string.IsNullOrWhiteSpace(host))
        {
            await ReplyAsync(runtime, eventPayload, "This group has no remote server binding.", cancellationToken);
            return;
        }

        var snapshot = runtime.Storage.GetLatestOsqSnapshot(host, index);
        if (snapshot is null && index == 1)
        {
            snapshot = TryImportLatestSharedOsqSnapshot(runtime, host);
        }

        if (snapshot is null)
        {
            await ReplyAsync(runtime, eventPayload, $"No server status #{index} for {host}.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, BuildOsqPlayersMessage(host, snapshot), cancellationToken);
    }

    private async Task HandleBindRemoteServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        var match = OsqBindServerPattern.Match(args.Trim());
        if (!match.Success)
        {
            await ReplyAsync(
                runtime,
                eventPayload,
                "Usage: /bindremote <host> <token> <group_id>. 中文：绑定远程服务器",
                cancellationToken);
            return;
        }

        var host = NormalizeServerHost(match.Groups[1].Value);
        var token = match.Groups[2].Value.Trim();
        var groupId = ParseLong(match.Groups[3].Value);
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Invalid QQ group id.", cancellationToken);
            return;
        }

        if (!TryValidateToken(token, out var tokenError))
        {
            await ReplyAsync(runtime, eventPayload, $"Invalid token: {tokenError}", cancellationToken);
            return;
        }

        if (!HasRemoteBindPermission(runtime, eventPayload, groupId))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        runtime.Storage.UpsertRemoteServer(host, token, userId);
        runtime.Storage.BindGroupRemoteServer(groupId, host);
        TryImportLatestSharedOsqSnapshot(runtime, host);

        await ReplyAsync(runtime, eventPayload, $"已绑定远程服务器：{host} -> 群 {groupId}", cancellationToken);
    }

    private async Task HandleUnbindRemoteServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, string args, CancellationToken cancellationToken)
    {
        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        var parts = args.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            await ReplyAsync(runtime, eventPayload, "Usage: /unbindremote <host> <group_id>. 中文：解绑远程服务器", cancellationToken);
            return;
        }

        var host = NormalizeServerHost(parts[0]);
        var groupId = ParseLong(parts[1]);
        if (groupId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Invalid QQ group id.", cancellationToken);
            return;
        }

        if (!HasRemoteBindPermission(runtime, eventPayload, groupId))
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        var removed = runtime.Storage.UnbindGroupRemoteServer(groupId, host);
        if (!removed)
        {
            await ReplyAsync(runtime, eventPayload, $"Group {groupId} is not bound to {host}.", cancellationToken);
            return;
        }

        await ReplyAsync(runtime, eventPayload, $"已解绑：群 {groupId} <-> {host}", cancellationToken);
    }

    private async Task HandleListRemoteServerAsync(Vs2QQRuntimeContext runtime, JsonObject eventPayload, CancellationToken cancellationToken)
    {
        var userId = GetInt64(eventPayload, "user_id");
        if (userId <= 0)
        {
            await ReplyAsync(runtime, eventPayload, "Cannot identify user.", cancellationToken);
            return;
        }

        IReadOnlyList<Vs2QQRemoteGroupServerRecord> records;
        if (runtime.SuperUsers.Contains(userId))
        {
            records = runtime.Storage.ListGroupRemoteServersForAdmin();
        }
        else if (IsGroupMessage(eventPayload) && HasAdminPermission(runtime, eventPayload))
        {
            var groupId = GetInt64(eventPayload, "group_id");
            records = runtime.Storage.ListGroupRemoteServersForGroup(groupId);
        }
        else
        {
            await ReplyAsync(runtime, eventPayload, "Permission denied. Group admin/owner or super admin only.", cancellationToken);
            return;
        }

        if (records.Count == 0)
        {
            await ReplyAsync(runtime, eventPayload, "No remote server bindings.", cancellationToken);
            return;
        }

        var lines = new List<string> { "远程服务器：" };
        lines.AddRange(records.Select(x => $"- 群 {x.GroupId}: {x.ServerHost}"));
        await ReplyAsync(runtime, eventPayload, string.Join('\n', lines), cancellationToken);
    }

    private async Task SendToGameServerAsync(Vs2QQRuntimeContext runtime, long groupId, string message, CancellationToken cancellationToken)
    {
        var host = runtime.Storage.FindRemoteServerHostByGroup(groupId);
        if (string.IsNullOrWhiteSpace(host))
        {
            return;
        }

        var outbound = message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(outbound))
        {
            return;
        }

        await _serverProcessService.SendCommandAsync($"/announce {outbound}", cancellationToken);
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

        var host = runtime.Storage.FindRemoteServerHostByGroup(groupId);
        if (string.IsNullOrWhiteSpace(host))
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

    private static string ExtractPlainText(JsonObject eventPayload)
    {
        if (eventPayload.TryGetPropertyValue("message", out var messageNode) && messageNode is not null)
        {
            var segmentText = ExtractOneBotMessageNodeText(messageNode);
            if (!string.IsNullOrWhiteSpace(segmentText))
            {
                return NormalizeOutboundText(segmentText);
            }
        }

        var message = GetString(eventPayload, "raw_message");
        if (!string.IsNullOrWhiteSpace(message))
        {
            return NormalizeOutboundText(message);
        }

        return NormalizeOutboundText(GetString(eventPayload, "message"));
    }

    private static string ExtractOneBotMessageNodeText(JsonNode messageNode)
    {
        if (messageNode is JsonValue valueNode && valueNode.TryGetValue<string>(out var textValue))
        {
            return textValue;
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
                    parts.Add("@" + (data is null ? string.Empty : GetString(data, "qq")));
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

    private static bool IsPrivateMessage(JsonObject eventPayload)
    {
        return string.Equals(GetString(eventPayload, "message_type"), "private", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasAdminPermission(Vs2QQRuntimeContext runtime, JsonObject eventPayload)
    {
        var userId = GetInt64(eventPayload, "user_id");
        if (runtime.SuperUsers.Contains(userId))
        {
            return true;
        }

        if (eventPayload["sender"] is not JsonObject senderObject)
        {
            return false;
        }

        var role = GetString(senderObject, "role");
        return role is "admin" or "owner";
    }

    private static string BuildHelpText()
    {
        return """
            VS2QQ Commands
            /help - 帮助
            /bindqq <player_name> <password> - 私聊绑定QQ到玩家名
            /passwd <old_password> <new_password> <confirm> - 私聊修改绑定玩家密码
            /unbindqq - 私聊解绑当前QQ
            /mybind - 私聊查看当前QQ绑定
            /bindremote <host> <token> <group_id> - 绑定远程服务器
            /unbindremote <host> <group_id> - 解绑远程服务器
            /listremote - 查看远程服务器绑定
            /server status [n] - 获取最近第 n 次服务器状态（默认1）
            /server players [n] - 获取最近第 n 次在线玩家列表（默认1）
            /server password get - 获取服务器密码
            /server password set <new_password> - 修改服务器密码（- 表示清空）
            /bindlogserver <server_id> <log_path> - 绑定本机日志服务器（群管理/群主）
            /unbindlogserver <server_id> - 解绑本机日志服务器（群管理/群主）
            /listlogserver - 查看本机日志服务器（群）
            /bindlogregex <server_id> <regex> - 设置日志匹配正则（群管理/群主）
            """;
    }

    private bool TryGetCurrentRunningProfile(out InstanceProfile profile, out string error)
    {
        profile = new InstanceProfile();
        error = string.Empty;

        var status = _serverProcessService.GetCurrentStatus();
        var profileId = status.ProfileId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(profileId))
        {
            error = "当前没有正在运行的本地档案，命令仅支持当前运行档案。";
            return false;
        }

        profile = _instanceProfileService.GetProfileById(profileId) ??
                  _instanceProfileService.GetProfiles()
                      .FirstOrDefault(item => item.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase)) ??
                  new InstanceProfile();
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            error = "无法定位当前运行档案。";
            return false;
        }

        return true;
    }

    private static bool TryLoadServerAuthSettings(InstanceProfile profile, out VsServerAuthSettings settings)
    {
        settings = new VsServerAuthSettings();
        var path = Path.Combine(profile.DirectoryPath, "ModConfig", "serverauth.json");
        if (!File.Exists(path))
            return true;

        try
        {
            var json = File.ReadAllText(path);
            settings = JsonSerializer.Deserialize<VsServerAuthSettings>(json, ServerAuthJsonOptions) ?? new VsServerAuthSettings();
            settings.Discourse ??= new VsServerAuthDiscourseSettings();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryLoadServerAuthStore(
        InstanceProfile profile,
        out VsServerAuthStore store,
        out string error)
    {
        store = new VsServerAuthStore();
        error = string.Empty;

        var path = Path.Combine(profile.DirectoryPath, "ServerAuth", "players.json");
        if (!File.Exists(path))
        {
            error = "未找到 ServerAuth 玩家库，请先让玩家在游戏内完成注册。";
            return false;
        }

        try
        {
            var json = File.ReadAllText(path);
            store = JsonSerializer.Deserialize<VsServerAuthStore>(json, ServerAuthJsonOptions) ?? new VsServerAuthStore();
            store.Players ??= [];
            store.Sessions ??= [];
            return true;
        }
        catch (Exception ex)
        {
            error = $"读取 ServerAuth 玩家库失败：{ex.Message}";
            return false;
        }
    }

    private static void SaveServerAuthStore(InstanceProfile profile, VsServerAuthStore store)
    {
        var path = Path.Combine(profile.DirectoryPath, "ServerAuth", "players.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = path + ".tmp";
        var json = JsonSerializer.Serialize(store, ServerAuthJsonOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, path, true);
    }

    private static VsServerAuthPlayerRecord? FindServerAuthPlayer(VsServerAuthStore store, string playerNameOrUid)
    {
        var normalized = NormalizeServerAuthPlayerName(playerNameOrUid);
        return store.Players.FirstOrDefault(player =>
            player.PlayerUid.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.PlayerName.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeServerAuthPlayerName(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string ResolvePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(value.Trim()));
        }
        catch
        {
            return value.Trim();
        }
    }

    private static string StripQuotes(string value)
    {
        if (value.Length >= 2 && value[0] == value[^1] && (value[0] == '"' || value[0] == '\''))
        {
            return value[1..^1];
        }

        return value;
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

    private async Task OsqListenLoopAsync(Vs2QQRuntimeContext runtime, CancellationToken cancellationToken)
    {
        var prefix = runtime.Settings.OsqListenPrefix;
        if (string.IsNullOrWhiteSpace(prefix))
        {
            prefix = "http://127.0.0.1:18089/";
        }

        using var listener = new HttpListener();
        listener.Prefixes.Add(prefix);

        try
        {
            listener.Start();
            runtime.OsqListener = listener;
            using var cancellationRegistration = cancellationToken.Register(() =>
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
            EmitOutput($"[osq] 监听已启动：{prefix}");
        }
        catch (Exception ex)
        {
            EmitOutput($"[warn] OSQ 监听启动失败：{ex.Message}");
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
                _ = Task.Run(() => HandleOsqRequestAsync(runtime, context, cancellationToken), cancellationToken);
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
                EmitOutput($"[warn] OSQ 监听异常：{ex.Message}");
                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
        }
    }

    private async Task HandleOsqRequestAsync(Vs2QQRuntimeContext runtime, HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
            {
                await WriteOsqResponseAsync(context, 405, "method not allowed");
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (!string.Equals(path.TrimEnd('/'), "/api/osq/report", StringComparison.OrdinalIgnoreCase))
            {
                await WriteOsqResponseAsync(context, 404, "not found");
                return;
            }

            if (context.Request.ContentLength64 > MaxOsqRequestBodyBytes)
            {
                await WriteOsqResponseAsync(context, 413, "payload too large");
                return;
            }

            var rawBody = await ReadStreamBytesWithLimitAsync(
                context.Request.InputStream,
                MaxOsqRequestBodyBytes,
                cancellationToken);
            if (rawBody is null)
            {
                await WriteOsqResponseAsync(context, 413, "payload too large");
                return;
            }

            var token = ParseAuthorizationToken(context.Request.Headers["Authorization"]);
            if (string.IsNullOrEmpty(token))
            {
                await WriteOsqResponseAsync(context, 401, "missing bearer token");
                return;
            }

            var host = runtime.Storage.FindHostByToken(token);
            if (string.IsNullOrWhiteSpace(host))
            {
                await WriteOsqResponseAsync(context, 403, "unknown token");
                return;
            }

            var timestampRaw = context.Request.Headers["X-OSQ-Timestamp"] ?? string.Empty;
            var nonce = context.Request.Headers["X-OSQ-Nonce"] ?? string.Empty;
            var signature = context.Request.Headers["X-OSQ-Signature"] ?? string.Empty;
            if (!long.TryParse(timestampRaw, out var timestamp))
            {
                await WriteOsqResponseAsync(context, 401, "invalid timestamp");
                return;
            }

            if (!VerifyOsqSignature(token, rawBody, signature))
            {
                await WriteOsqResponseAsync(context, 401, "invalid signature");
                return;
            }

            var body = await DecodeOsqRequestBodyAsync(
                rawBody,
                context.Request.Headers["Content-Encoding"],
                cancellationToken);
            if (body is null)
            {
                await WriteOsqResponseAsync(context, 400, "invalid content encoding");
                return;
            }

            var drift = Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - timestamp);
            if (drift > 600)
            {
                await WriteOsqResponseAsync(context, 401, "timestamp drift too large");
                return;
            }

            if (!NonceRegex.IsMatch(nonce))
            {
                await WriteOsqResponseAsync(context, 401, "invalid nonce");
                return;
            }

            if (!runtime.Storage.TryUseOsqNonce(host, nonce, DateTimeOffset.FromUnixTimeSeconds(timestamp).AddMinutes(10)))
            {
                await WriteOsqResponseAsync(context, 409, "replay detected");
                return;
            }

            OsqSnapshotEnvelope? payload;
            try
            {
                payload = JsonSerializer.Deserialize<OsqSnapshotEnvelope>(body, OsqJsonOptions);
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] OSQ JSON 解析失败 host={host}: {ex.Message}");
                await WriteOsqResponseAsync(context, 400, "invalid json");
                return;
            }

            if (payload is null || payload.Server is null)
            {
                await WriteOsqResponseAsync(context, 400, "missing server payload");
                return;
            }

            runtime.Storage.AddOsqSnapshot(host, payload);
            await ForwardOsqSnapshotAsync(runtime, host, payload, cancellationToken);
            await WriteOsqResponseAsync(context, 200, "ok");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // ignore
        }
        catch (Exception ex)
        {
            EmitOutput($"[warn] OSQ 请求处理异常：{ex.Message}");
            try
            {
                await WriteOsqResponseAsync(context, 500, "internal error");
            }
            catch
            {
                // ignore
            }
        }
    }

    private async Task ForwardOsqSnapshotAsync(Vs2QQRuntimeContext runtime, string host, OsqSnapshotEnvelope payload, CancellationToken cancellationToken)
    {
        var groups = runtime.Storage.ListGroupsForRemoteServer(host);
        if (groups.Count == 0)
        {
            return;
        }

        var forwardState = runtime.Storage.GetOsqForwardState(host);
        var chats = payload.RecentChats ?? [];
        var events = payload.PlayerEvents ?? [];
        var notifications = payload.ServerNotifications ?? [];

        var skipCurrentSnapshotWhenNoState = forwardState is null;
        var newChatLines = CollectNewChatLines(
            chats,
            forwardState?.LastChatSignature,
            skipCurrentSnapshotWhenNoState,
            out var lastChatSignature);
        var newEventLines = CollectNewEventLines(
            runtime,
            host,
            events,
            forwardState?.LastEventSignature,
            skipCurrentSnapshotWhenNoState,
            out var lastEventSignature);
        var newNotificationLines = CollectNewNotificationLines(
            notifications,
            forwardState?.LastNotificationSignature,
            skipCurrentSnapshotWhenNoState,
            out var lastNotificationSignature);

        var lines = new List<string>();
        lines.AddRange(newEventLines);
        lines.AddRange(newNotificationLines);
        lines.AddRange(newChatLines);
        if (lines.Count == 0)
        {
            runtime.Storage.UpsertOsqForwardState(host, lastChatSignature, lastEventSignature, lastNotificationSignature);
            return;
        }

        var messages = SplitOneBotMessages(lines);
        var successfulGroups = 0;
        foreach (var groupId in groups)
        {
            try
            {
                foreach (var message in messages)
                {
                    await runtime.OneBot.SendGroupMsgAsync(groupId, message, cancellationToken);
                }

                successfulGroups++;
            }
            catch (Exception ex)
            {
                EmitOutput($"[warn] OSQ 转发失败 host={host} group={groupId}: {ex.Message}");
            }
        }

        if (successfulGroups == groups.Count)
        {
            runtime.Storage.UpsertOsqForwardState(host, lastChatSignature, lastEventSignature, lastNotificationSignature);
        }
        else
        {
            EmitOutput($"[warn] OSQ 转发未全部成功 host={host}: success={successfulGroups}/{groups.Count}，保留游标等待下次重试。");
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

    private static string BuildOsqSummaryMessage(string host, OsqSnapshotEnvelope payload)
    {
        var server = payload.Server ?? new OsqServerInfo();
        var players = payload.Players ?? [];
        var events = payload.PlayerEvents ?? [];
        var chats = payload.RecentChats ?? [];

        var lines = new List<string>
        {
            $"[OSQ:{host}]",
            FormatOsqSummaryTimestamp(payload.TimestampUtc),
            $"服务器：{Safe(server.Name)}",
            $"状态：{FormatOsqServerStatus(server.Status)}",
            $"版本：{Safe(server.Version)}",
            $"人数：{server.PlayerCount}/{server.MaxPlayers}",
            $"世界：{Safe(server.WorldName)}",
            $"地址：{Safe(server.ServerIp)}:{server.ServerPort}"
        };

        if (players.Count > 0)
        {
            var topPlayers = players
                .Select(p => Safe(p.PlayerName))
                .Where(name => name != "-")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList();
            if (topPlayers.Count > 0)
            {
                lines.Add("玩家：" + string.Join("、", topPlayers));
            }
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

    private static string BuildOsqPlayersMessage(string host, OsqSnapshotEnvelope payload)
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
            $"[OSQ:{host}] 在线玩家 {onlinePlayers.Count}/{server.MaxPlayers} @ {timeLabel}"
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
            result.Add($"[服务器 {timeLabel}]{sender}：{Safe(content)}");
        }

        return result;
    }

    private static IReadOnlyList<string> CollectNewEventLines(
        Vs2QQRuntimeContext runtime,
        string host,
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
            if (ShouldSkipDuplicateJoinRelay(runtime, line, entry.TimestampUtc))
            {
                continue;
            }

            result.Add(line);
        }

        return result;
    }

    private static IReadOnlyList<string> CollectNewNotificationLines(
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
            result.Add($"[服务器 {timeLabel}]{Safe(content)}");
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

        // If the previous marker aged out of the OSQ recent window, forwarding the
        // whole snapshot would replay stale chat history into QQ. Re-baseline here.
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

    private static bool ShouldSkipDuplicateJoinRelay(
        Vs2QQRuntimeContext runtime,
        string payload,
        string? timestamp)
    {
        if (!TryParseJoinRelayPayload(payload, out var playerName))
        {
            return false;
        }

        var eventTime = ParseRelayEventTime(timestamp);
        if (!eventTime.HasValue)
        {
            return false;
        }

        var key = $"join|{playerName.Trim().ToLowerInvariant()}";
        lock (runtime.RecentJoinRelays)
        {
            PruneRecentJoinRelays(runtime.RecentJoinRelays, eventTime.Value);
            if (runtime.RecentJoinRelays.TryGetValue(key, out var previous)
                && (eventTime.Value - previous).Duration() <= PlayerJoinRelayDedupeWindow)
            {
                if (eventTime.Value > previous)
                {
                    runtime.RecentJoinRelays[key] = eventTime.Value;
                }

                return true;
            }

            runtime.RecentJoinRelays[key] = eventTime.Value;
            return false;
        }
    }

    private static void PruneRecentJoinRelays(Dictionary<string, DateTimeOffset> recentJoinRelays, DateTimeOffset now)
    {
        foreach (var item in recentJoinRelays.ToArray())
        {
            if ((now - item.Value).Duration() > TimeSpan.FromMinutes(5))
            {
                recentJoinRelays.Remove(item.Key);
            }
        }
    }

    private static bool TryParseJoinRelayPayload(string? payload, out string playerName)
    {
        playerName = string.Empty;
        var normalized = NormalizeDisplayText(payload);
        if (normalized.Length == 0)
        {
            return false;
        }

        var closeIndex = normalized.IndexOf(']');
        if (closeIndex >= 0 && closeIndex + 1 < normalized.Length)
        {
            normalized = normalized[(closeIndex + 1)..].Trim();
        }

        const string joinSuffix = " 进入服务器";
        if (!normalized.EndsWith(joinSuffix, StringComparison.Ordinal))
        {
            return false;
        }

        playerName = normalized[..^joinSuffix.Length].Trim();
        return playerName.Length > 0;
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

    private static string ResolveBoundRemoteServerHostForSnapshot(
        Vs2QQRuntimeContext runtime,
        string reportedHost,
        out IReadOnlyList<long> groups)
    {
        foreach (var candidate in BuildServerHostCandidates(reportedHost))
        {
            var candidateGroups = runtime.Storage.ListGroupsForRemoteServer(candidate);
            if (candidateGroups.Count > 0)
            {
                groups = candidateGroups;
                return candidate;
            }
        }

        var normalized = NormalizeServerHost(reportedHost);
        groups = [];
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

    private static string ParseAuthorizationToken(string? authorizationHeader)
    {
        var header = (authorizationHeader ?? string.Empty).Trim();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        return header[7..].Trim();
    }

    private static async Task<byte[]?> ReadStreamBytesWithLimitAsync(Stream stream, int maxBytes, CancellationToken cancellationToken)
    {
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
            if (read <= 0)
            {
                break;
            }

            if (output.Length + read > maxBytes)
            {
                return null;
            }

            output.Write(buffer, 0, read);
        }

        return output.ToArray();
    }

    private static async Task<string?> DecodeOsqRequestBodyAsync(
        byte[] rawBody,
        string? contentEncoding,
        CancellationToken cancellationToken)
    {
        var encodings = (contentEncoding ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        byte[]? current = rawBody;
        try
        {
            for (var i = encodings.Length - 1; i >= 0; i--)
            {
                var encoding = encodings[i].ToLowerInvariant();
                if (encoding is "identity")
                {
                    continue;
                }

                using var input = new MemoryStream(current);
                Stream decoder = encoding switch
                {
                    "gzip" or "x-gzip" => new GZipStream(input, CompressionMode.Decompress),
                    "deflate" => new DeflateStream(input, CompressionMode.Decompress),
                    _ => throw new InvalidDataException($"Unsupported content encoding: {encoding}")
                };

                using (decoder)
                {
                    current = await ReadStreamBytesWithLimitAsync(decoder, MaxOsqRequestBodyBytes, cancellationToken);
                }

                if (current is null)
                {
                    return null;
                }
            }
        }
        catch
        {
            return null;
        }

        return Encoding.UTF8.GetString(current);
    }

    private static bool VerifyOsqSignature(string token, byte[] rawBody, string givenSignature)
    {
        if (string.IsNullOrWhiteSpace(token) || string.IsNullOrWhiteSpace(givenSignature))
        {
            return false;
        }

        try
        {
            var expected = HMACSHA256.HashData(Encoding.UTF8.GetBytes(token), rawBody ?? Array.Empty<byte>());
            return TryDecodeOsqSignature(givenSignature, out var given)
                && given.Length == expected.Length
                && CryptographicOperations.FixedTimeEquals(given, expected);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryDecodeOsqSignature(string signature, out byte[] bytes)
    {
        bytes = Array.Empty<byte>();
        var normalized = (signature ?? string.Empty)
            .Trim()
            .Replace('-', '+')
            .Replace('_', '/');
        if (normalized.Length == 0)
        {
            return false;
        }

        var remainder = normalized.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        if (remainder > 0)
        {
            normalized = normalized.PadRight(normalized.Length + (4 - remainder), '=');
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

    private static async Task WriteOsqResponseAsync(HttpListenerContext context, int statusCode, string message)
    {
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "application/json; charset=utf-8";
        var json = $"{{\"ok\":{(statusCode >= 200 && statusCode < 300 ? "true" : "false")},\"message\":\"{EscapeJson(message)}\"}}";
        var bytes = Encoding.UTF8.GetBytes(json);
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes, 0, bytes.Length);
        context.Response.OutputStream.Close();
    }

    private static string EscapeJson(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("\"", "\\\"", StringComparison.Ordinal)
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal);
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

    private static bool TryValidateToken(string token, out string error)
    {
        var value = token?.Trim() ?? string.Empty;
        if (value.Length < 16 || value.Length > 256)
        {
            error = "长度必须在 16 到 256 之间。";
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var ok = char.IsLetterOrDigit(c) || c is '_' or '-';
            if (!ok)
            {
                error = "仅允许字符 A-Z a-z 0-9 _ -";
                return false;
            }
        }

        error = string.Empty;
        return true;
    }

    private static long ParseLong(string value)
    {
        return long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : 0;
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

    private static string FormatOsqSummaryTimestamp(string? rawTimestamp)
    {
        if (!string.IsNullOrWhiteSpace(rawTimestamp))
        {
            var value = rawTimestamp.Trim();
            if (DateTimeOffset.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var offsetParsed))
            {
                return offsetParsed.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }

            if (DateTime.TryParse(
                    value,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                    out var parsed))
            {
                return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            }
        }

        return DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
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

    private static bool HasRemoteBindPermission(Vs2QQRuntimeContext runtime, JsonObject eventPayload, long targetGroupId)
    {
        var userId = GetInt64(eventPayload, "user_id");
        if (runtime.SuperUsers.Contains(userId))
        {
            return true;
        }

        if (!IsGroupMessage(eventPayload) || GetInt64(eventPayload, "group_id") != targetGroupId)
        {
            return false;
        }

        return HasAdminPermission(runtime, eventPayload);
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
            .ToArray();

        return OperationResult<RobotSettings>.Success(new RobotSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = string.IsNullOrWhiteSpace(settings.AccessToken) ? null : settings.AccessToken.Trim(),
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
            Vs2QQStorage storage,
            Vs2QQLogTailer tailer)
        {
            Settings = settings;
            Storage = storage;
            Tailer = tailer;
            SuperUsers = settings.SuperUsers?.ToHashSet() ?? [];
        }

        public RobotSettings Settings { get; }

        public HashSet<long> SuperUsers { get; }

        public Vs2QQStorage Storage { get; }

        public Vs2QQLogTailer Tailer { get; }

        public Vs2QQOneBotClient OneBot { get; set; } = null!;

        public HttpListener? OsqListener { get; set; }

        public EventHandler<OsqSnapshotReceivedEventArgs>? OsqSnapshotHandler { get; set; }

        public Dictionary<string, DateTimeOffset> RecentJoinRelays { get; } = new(StringComparer.OrdinalIgnoreCase);

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposedFlag, 1) == 1)
            {
                return;
            }

            try
            {
                OsqListener?.Close();
            }
            catch
            {
                // ignore
            }

            await OneBot.DisposeAsync();
            Storage.Dispose();
        }
    }

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

        public void BindQq(long qqId, string playerName, int bindingVersion)
        {
            lock (_sync)
            {
                using (var cleanup = _connection.CreateCommand())
                {
                    cleanup.CommandText =
                        """
                        DELETE FROM qq_bindings
                        WHERE lower(player_name) = lower($playerName) AND qq_id <> $qqId;
                        """;
                    cleanup.Parameters.AddWithValue("$playerName", playerName);
                    cleanup.Parameters.AddWithValue("$qqId", qqId);
                    cleanup.ExecuteNonQuery();
                }

                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO qq_bindings (qq_id, player_name, binding_version, created_at, updated_at)
                    VALUES ($qqId, $playerName, $bindingVersion, $createdAt, $updatedAt)
                    ON CONFLICT(qq_id) DO UPDATE SET
                        player_name = excluded.player_name,
                        binding_version = excluded.binding_version,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$qqId", qqId);
                command.Parameters.AddWithValue("$playerName", playerName);
                command.Parameters.AddWithValue("$bindingVersion", bindingVersion);
                command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public bool UnbindQq(long qqId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM qq_bindings WHERE qq_id = $qqId;";
                command.Parameters.AddWithValue("$qqId", qqId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public (long QqId, string PlayerName)? GetQqBinding(long qqId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT qq_id, player_name
                    FROM qq_bindings
                    WHERE qq_id = $qqId AND binding_version >= $requiredVersion
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$qqId", qqId);
                command.Parameters.AddWithValue("$requiredVersion", RequiredQqBindingVersion);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                return (reader.GetInt64(0), reader.GetString(1));
            }
        }

        public long? FindQqByPlayer(string playerName)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT qq_id
                    FROM qq_bindings
                    WHERE lower(player_name) = lower($playerName)
                      AND binding_version >= $requiredVersion
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$playerName", playerName);
                command.Parameters.AddWithValue("$requiredVersion", RequiredQqBindingVersion);
                var value = command.ExecuteScalar();
                if (value is null || value == DBNull.Value)
                {
                    return null;
                }

                return Convert.ToInt64(value, CultureInfo.InvariantCulture);
            }
        }

        public void UpsertServer(string serverId, string logPath, string? chatRegex = null)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO servers (server_id, log_path, chat_regex, enabled, created_at, updated_at)
                    VALUES ($serverId, $logPath, $chatRegex, 1, $createdAt, $updatedAt)
                    ON CONFLICT(server_id) DO UPDATE SET
                        log_path = excluded.log_path,
                        chat_regex = COALESCE(excluded.chat_regex, servers.chat_regex),
                        enabled = 1,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$serverId", serverId);
                command.Parameters.AddWithValue("$logPath", logPath);
                command.Parameters.AddWithValue("$chatRegex", (object?)chatRegex ?? DBNull.Value);
                command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public bool SetServerRegex(string serverId, string chatRegex)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    UPDATE servers
                    SET chat_regex = $chatRegex, updated_at = $updatedAt
                    WHERE server_id = $serverId;
                    """;
                command.Parameters.AddWithValue("$chatRegex", chatRegex);
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.Parameters.AddWithValue("$serverId", serverId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public void BindGroupServer(long groupId, string serverId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO group_servers (group_id, server_id, created_at)
                    VALUES ($groupId, $serverId, $createdAt);
                    """;
                command.Parameters.AddWithValue("$groupId", groupId);
                command.Parameters.AddWithValue("$serverId", serverId);
                command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public bool UnbindGroupServer(long groupId, string serverId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM group_servers WHERE group_id = $groupId AND server_id = $serverId;";
                command.Parameters.AddWithValue("$groupId", groupId);
                command.Parameters.AddWithValue("$serverId", serverId);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public IReadOnlyList<Vs2QQGroupServerRecord> ListGroupServers(long groupId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT s.server_id, s.log_path, s.chat_regex, s.enabled
                    FROM servers s
                    JOIN group_servers gs ON gs.server_id = s.server_id
                    WHERE gs.group_id = $groupId
                    ORDER BY s.server_id;
                    """;
                command.Parameters.AddWithValue("$groupId", groupId);
                using var reader = command.ExecuteReader();
                var result = new List<Vs2QQGroupServerRecord>();
                while (reader.Read())
                {
                    result.Add(new Vs2QQGroupServerRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2),
                        reader.GetInt64(3) == 1));
                }

                return result;
            }
        }

        public IReadOnlyList<Vs2QQServerRecord> ListActiveServers()
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT s.server_id, s.log_path, s.chat_regex
                    FROM servers s
                    WHERE s.enabled = 1
                      AND EXISTS (
                        SELECT 1 FROM group_servers gs WHERE gs.server_id = s.server_id
                      )
                    ORDER BY s.server_id;
                    """;
                using var reader = command.ExecuteReader();
                var result = new List<Vs2QQServerRecord>();
                while (reader.Read())
                {
                    result.Add(new Vs2QQServerRecord(
                        reader.GetString(0),
                        reader.GetString(1),
                        reader.IsDBNull(2) ? null : reader.GetString(2)));
                }

                return result;
            }
        }

        public IReadOnlyList<long> ListGroupsForServer(string serverId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT group_id
                    FROM group_servers
                    WHERE server_id = $serverId
                    ORDER BY group_id;
                    """;
                command.Parameters.AddWithValue("$serverId", serverId);
                using var reader = command.ExecuteReader();
                var result = new List<long>();
                while (reader.Read())
                {
                    result.Add(reader.GetInt64(0));
                }

                return result;
            }
        }

        public void UpsertRemoteServer(string serverHost, string token, long boundByQqId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO remote_servers (server_host, token, bound_by_qq_id, enabled, created_at, updated_at)
                    VALUES ($serverHost, $token, $boundByQqId, 1, $createdAt, $updatedAt)
                    ON CONFLICT(server_host) DO UPDATE SET
                        token = excluded.token,
                        bound_by_qq_id = excluded.bound_by_qq_id,
                        enabled = 1,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
                command.Parameters.AddWithValue("$token", token);
                command.Parameters.AddWithValue("$boundByQqId", boundByQqId);
                command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public void BindGroupRemoteServer(long groupId, string serverHost)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT OR IGNORE INTO group_remote_servers (group_id, server_host, created_at)
                    VALUES ($groupId, $serverHost, $createdAt);
                    """;
                command.Parameters.AddWithValue("$groupId", groupId);
                command.Parameters.AddWithValue("$serverHost", serverHost);
                command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public bool UnbindGroupRemoteServer(long groupId, string serverHost)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "DELETE FROM group_remote_servers WHERE group_id = $groupId AND server_host = $serverHost;";
                command.Parameters.AddWithValue("$groupId", groupId);
                command.Parameters.AddWithValue("$serverHost", serverHost);
                return command.ExecuteNonQuery() > 0;
            }
        }

        public string? FindHostByToken(string token)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT rs.server_host
                    FROM remote_servers rs
                    LEFT JOIN group_remote_servers grs ON grs.server_host = rs.server_host
                    WHERE rs.token = $token AND rs.enabled = 1
                    GROUP BY rs.server_host, rs.updated_at
                    ORDER BY
                        CASE WHEN COUNT(grs.group_id) > 0 THEN 1 ELSE 0 END DESC,
                        rs.updated_at DESC,
                        rs.server_host ASC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$token", token);
                var value = command.ExecuteScalar();
                return value is null || value == DBNull.Value ? null : value.ToString();
            }
        }

        public IReadOnlyList<long> ListGroupsForRemoteServer(string serverHost)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT group_id
                    FROM group_remote_servers
                    WHERE server_host = $serverHost
                    ORDER BY group_id;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
                using var reader = command.ExecuteReader();
                var result = new List<long>();
                while (reader.Read())
                {
                    result.Add(reader.GetInt64(0));
                }

                return result;
            }
        }

        public string? FindRemoteServerHostByGroup(long groupId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT server_host
                    FROM group_remote_servers
                    WHERE group_id = $groupId
                    ORDER BY created_at DESC
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$groupId", groupId);
                var value = command.ExecuteScalar();
                return value is null || value == DBNull.Value ? null : value.ToString();
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
                        INSERT INTO osq_snapshots (server_host, payload_json, created_at)
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
                        DELETE FROM osq_snapshots
                        WHERE server_host = $serverHost
                          AND snapshot_id NOT IN (
                              SELECT snapshot_id
                              FROM osq_snapshots
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
                FROM osq_snapshots
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

        public OsqForwardState? GetOsqForwardState(string serverHost)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT last_chat_signature, last_event_signature, last_notification_signature
                    FROM osq_forward_state
                    WHERE server_host = $serverHost
                    LIMIT 1;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
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

        public void UpsertOsqForwardState(string serverHost, string? lastChatSignature, string? lastEventSignature, string? lastNotificationSignature)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO osq_forward_state (server_host, last_chat_signature, last_event_signature, last_notification_signature, updated_at)
                    VALUES ($serverHost, $lastChatSignature, $lastEventSignature, $lastNotificationSignature, $updatedAt)
                    ON CONFLICT(server_host) DO UPDATE SET
                        last_chat_signature = excluded.last_chat_signature,
                        last_event_signature = excluded.last_event_signature,
                        last_notification_signature = excluded.last_notification_signature,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$serverHost", serverHost);
                command.Parameters.AddWithValue("$lastChatSignature", (object?)lastChatSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$lastEventSignature", (object?)lastEventSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$lastNotificationSignature", (object?)lastNotificationSignature ?? DBNull.Value);
                command.Parameters.AddWithValue("$updatedAt", GetUtcNowIso());
                command.ExecuteNonQuery();
            }
        }

        public IReadOnlyList<Vs2QQRemoteGroupServerRecord> ListGroupRemoteServersForGroup(long groupId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT grs.group_id, rs.server_host, rs.bound_by_qq_id
                    FROM group_remote_servers grs
                    JOIN remote_servers rs ON rs.server_host = grs.server_host
                    WHERE grs.group_id = $groupId AND rs.enabled = 1
                    ORDER BY rs.server_host;
                    """;
                command.Parameters.AddWithValue("$groupId", groupId);
                using var reader = command.ExecuteReader();
                var result = new List<Vs2QQRemoteGroupServerRecord>();
                while (reader.Read())
                {
                    result.Add(new Vs2QQRemoteGroupServerRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt64(2)));
                }

                return result;
            }
        }

        public IReadOnlyList<Vs2QQRemoteGroupServerRecord> ListGroupRemoteServersForAdmin()
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    SELECT grs.group_id, rs.server_host, rs.bound_by_qq_id
                    FROM group_remote_servers grs
                    JOIN remote_servers rs ON rs.server_host = grs.server_host
                    WHERE rs.enabled = 1
                    ORDER BY grs.group_id, rs.server_host;
                    """;
                using var reader = command.ExecuteReader();
                var result = new List<Vs2QQRemoteGroupServerRecord>();
                while (reader.Read())
                {
                    result.Add(new Vs2QQRemoteGroupServerRecord(
                        reader.GetInt64(0),
                        reader.GetString(1),
                        reader.GetInt64(2)));
                }

                return result;
            }
        }

        public bool TryUseOsqNonce(string serverHost, string nonce, DateTimeOffset expiresAt)
        {
            lock (_sync)
            {
                using (var cleanup = _connection.CreateCommand())
                {
                    cleanup.CommandText = "DELETE FROM osq_replay_nonce WHERE expires_at < $now;";
                    cleanup.Parameters.AddWithValue("$now", GetUtcNowIso());
                    cleanup.ExecuteNonQuery();
                }

                try
                {
                    using var command = _connection.CreateCommand();
                    command.CommandText =
                        """
                        INSERT INTO osq_replay_nonce (server_host, nonce, expires_at, created_at)
                        VALUES ($serverHost, $nonce, $expiresAt, $createdAt);
                        """;
                    command.Parameters.AddWithValue("$serverHost", serverHost);
                    command.Parameters.AddWithValue("$nonce", nonce);
                    command.Parameters.AddWithValue("$expiresAt", expiresAt.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture));
                    command.Parameters.AddWithValue("$createdAt", GetUtcNowIso());
                    command.ExecuteNonQuery();
                    return true;
                }
                catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
                {
                    return false;
                }
            }
        }

        public (string FileSignature, long Offset)? GetLogOffset(string serverId)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText = "SELECT file_signature, offset FROM log_offsets WHERE server_id = $serverId LIMIT 1;";
                command.Parameters.AddWithValue("$serverId", serverId);
                using var reader = command.ExecuteReader();
                if (!reader.Read())
                {
                    return null;
                }

                return (reader.GetString(0), reader.GetInt64(1));
            }
        }

        public void SetLogOffset(string serverId, string fileSignature, long offset)
        {
            lock (_sync)
            {
                using var command = _connection.CreateCommand();
                command.CommandText =
                    """
                    INSERT INTO log_offsets (server_id, file_signature, offset, updated_at)
                    VALUES ($serverId, $fileSignature, $offset, $updatedAt)
                    ON CONFLICT(server_id) DO UPDATE SET
                        file_signature = excluded.file_signature,
                        offset = excluded.offset,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$serverId", serverId);
                command.Parameters.AddWithValue("$fileSignature", fileSignature);
                command.Parameters.AddWithValue("$offset", offset);
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
                    CREATE TABLE IF NOT EXISTS qq_bindings (
                        qq_id INTEGER PRIMARY KEY,
                        player_name TEXT NOT NULL,
                        binding_version INTEGER NOT NULL DEFAULT 1,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS servers (
                        server_id TEXT PRIMARY KEY,
                        log_path TEXT NOT NULL,
                        chat_regex TEXT,
                        enabled INTEGER NOT NULL DEFAULT 1,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS group_servers (
                        group_id INTEGER NOT NULL,
                        server_id TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (group_id, server_id),
                        FOREIGN KEY (server_id) REFERENCES servers(server_id) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS log_offsets (
                        server_id TEXT PRIMARY KEY,
                        file_signature TEXT NOT NULL,
                        offset INTEGER NOT NULL,
                        updated_at TEXT NOT NULL,
                        FOREIGN KEY (server_id) REFERENCES servers(server_id) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS remote_servers (
                        server_host TEXT PRIMARY KEY,
                        token TEXT NOT NULL,
                        bound_by_qq_id INTEGER NOT NULL,
                        enabled INTEGER NOT NULL DEFAULT 1,
                        created_at TEXT NOT NULL,
                        updated_at TEXT NOT NULL
                    );

                    CREATE TABLE IF NOT EXISTS group_remote_servers (
                        group_id INTEGER NOT NULL,
                        server_host TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (group_id, server_host),
                        FOREIGN KEY (server_host) REFERENCES remote_servers(server_host) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS osq_replay_nonce (
                        server_host TEXT NOT NULL,
                        nonce TEXT NOT NULL,
                        expires_at TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        PRIMARY KEY (server_host, nonce),
                        FOREIGN KEY (server_host) REFERENCES remote_servers(server_host) ON DELETE CASCADE
                    );

                    CREATE TABLE IF NOT EXISTS osq_snapshots (
                        snapshot_id INTEGER PRIMARY KEY AUTOINCREMENT,
                        server_host TEXT NOT NULL,
                        payload_json TEXT NOT NULL,
                        created_at TEXT NOT NULL,
                        FOREIGN KEY (server_host) REFERENCES remote_servers(server_host) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_osq_snapshots_host_id
                        ON osq_snapshots (server_host, snapshot_id DESC);

                    CREATE TABLE IF NOT EXISTS osq_forward_state (
                        server_host TEXT PRIMARY KEY,
                        last_chat_signature TEXT,
                        last_event_signature TEXT,
                        last_notification_signature TEXT,
                        updated_at TEXT NOT NULL,
                        FOREIGN KEY (server_host) REFERENCES remote_servers(server_host) ON DELETE CASCADE
                    );

                    CREATE INDEX IF NOT EXISTS idx_group_servers_server_id
                        ON group_servers (server_id);

                    CREATE INDEX IF NOT EXISTS idx_remote_servers_token
                        ON remote_servers (token);

                    CREATE INDEX IF NOT EXISTS idx_group_remote_servers_host
                        ON group_remote_servers (server_host);

                    CREATE INDEX IF NOT EXISTS idx_osq_replay_nonce_expires_at
                        ON osq_replay_nonce (expires_at);
                    """;
                command.ExecuteNonQuery();
            }

            EnsureColumn("osq_forward_state", "last_notification_signature", "TEXT");
            EnsureColumn("qq_bindings", "binding_version", "INTEGER NOT NULL DEFAULT 1");
        }

        private static string GetUtcNowIso()
        {
            return DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);
        }

        private void EnsureColumn(string tableName, string columnName, string columnDefinition)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = $"PRAGMA table_info({tableName});";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var existingName = reader.GetString(1);
                if (string.Equals(existingName, columnName, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }
            }

            using var alter = _connection.CreateCommand();
            alter.CommandText = $"ALTER TABLE {tableName} ADD COLUMN {columnName} {columnDefinition};";
            alter.ExecuteNonQuery();
        }
    }

    private sealed class Vs2QQLogTailer
    {
        private readonly Vs2QQStorage _storage;
        private readonly Vs2QQTalkLineParser _parser;
        private readonly string _defaultEncoding;
        private readonly string _fallbackEncoding;
        private readonly Action<string> _log;
        private readonly Dictionary<string, (string Signature, long Offset)> _offsetCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _lineRemainder = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, (string Signature, long Offset)> _companionOffsetCache = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, string> _companionLineRemainder = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _missingWarned = new(StringComparer.OrdinalIgnoreCase);

        public Vs2QQLogTailer(
            Vs2QQStorage storage,
            Vs2QQTalkLineParser parser,
            string defaultEncoding,
            string fallbackEncoding,
            Action<string> log)
        {
            _storage = storage;
            _parser = parser;
            _defaultEncoding = defaultEncoding;
            _fallbackEncoding = fallbackEncoding;
            _log = log;
        }

        public void PrimeServer(string serverId, string logPath)
        {
            var path = new FileInfo(logPath);
            if (!path.Exists)
            {
                return;
            }

            var signature = BuildFileSignature(path);
            var offset = path.Length;
            SetOffset(serverId, signature, offset);
            _lineRemainder.Remove(serverId);

            foreach (var companionPath in GetCompanionLogPaths(path.FullName))
            {
                var companion = new FileInfo(companionPath);
                if (!companion.Exists)
                {
                    continue;
                }

                var companionKey = BuildCompanionKey(serverId, companion.FullName);
                _companionOffsetCache[companionKey] = (BuildFileSignature(companion), companion.Length);
                _companionLineRemainder.Remove(companionKey);
            }
        }

        public IReadOnlyList<Vs2QQTalkMessage> PollServer(Vs2QQServerRecord server)
        {
            var serverId = server.ServerId;
            var path = new FileInfo(server.LogPath);
            if (!path.Exists)
            {
                var missingKey = BuildMissingKey(serverId, path.FullName);
                if (_missingWarned.Add(missingKey))
                {
                    _log($"[warn] VS2QQ 日志文件不存在 server={serverId}: {path.FullName}");
                }

                return [];
            }

            _missingWarned.Remove(BuildMissingKey(serverId, path.FullName));

            var signature = BuildFileSignature(path);
            var fileSize = path.Length;
            if (!_offsetCache.TryGetValue(serverId, out var state))
            {
                var persisted = _storage.GetLogOffset(serverId);
                if (persisted.HasValue)
                {
                    state = persisted.Value;
                    _offsetCache[serverId] = state;
                }
            }

            if (state == default || string.IsNullOrWhiteSpace(state.Signature))
            {
                SetOffset(serverId, signature, fileSize);
                return [];
            }

            long offset = state.Offset;
            if (!string.Equals(state.Signature, signature, StringComparison.Ordinal))
            {
                offset = 0;
                _lineRemainder.Remove(serverId);
            }

            if (offset > fileSize)
            {
                offset = 0;
                _lineRemainder.Remove(serverId);
            }

            byte[] chunk;
            using (var stream = path.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                var remaining = fileSize - offset;
                if (remaining <= 0)
                {
                    chunk = [];
                }
                else
                {
                    chunk = new byte[remaining];
                    var read = stream.Read(chunk, 0, chunk.Length);
                    if (read < chunk.Length)
                    {
                        Array.Resize(ref chunk, read);
                    }
                }
            }

            var newOffset = offset + chunk.Length;
            SetOffset(serverId, signature, newOffset);
            var result = new List<Vs2QQTalkMessage>();
            if (chunk.Length > 0)
            {
                var text = DecodeChunk(chunk);
                if (!string.IsNullOrEmpty(text))
                {
                    if (_lineRemainder.TryGetValue(serverId, out var remainder) && !string.IsNullOrEmpty(remainder))
                    {
                        text = remainder + text;
                    }

                    string[] lines;
                    if (text.EndsWith('\n') || text.EndsWith('\r'))
                    {
                        lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                        _lineRemainder[serverId] = string.Empty;
                    }
                    else
                    {
                        lines = text.Split(['\r', '\n'], StringSplitOptions.None);
                        if (lines.Length == 0)
                        {
                            _lineRemainder[serverId] = text;
                            lines = [];
                        }
                        else
                        {
                            _lineRemainder[serverId] = lines[^1];
                            lines = lines[..^1];
                        }
                    }

                    foreach (var line in lines)
                    {
                        var parsed = _parser.Parse(line, server.ChatRegex);
                        if (parsed is null)
                        {
                            continue;
                        }

                        result.Add(new Vs2QQTalkMessage(
                            serverId,
                            parsed.Value.Timestamp,
                            parsed.Value.Sender,
                            parsed.Value.Content));
                    }
                }
            }

            foreach (var companionPath in GetCompanionLogPaths(path.FullName))
            {
                var companionMessages = PollCompanionLog(serverId, companionPath, server.ChatRegex);
                if (companionMessages.Count > 0)
                {
                    result.AddRange(companionMessages);
                }
            }

            return result;
        }

        private IReadOnlyList<Vs2QQTalkMessage> PollCompanionLog(string serverId, string logPath, string? chatRegex)
        {
            var path = new FileInfo(logPath);
            var companionKey = BuildCompanionKey(serverId, path.FullName);
            if (!path.Exists)
            {
                var missingKey = BuildMissingKey(serverId, path.FullName);
                _missingWarned.Add(missingKey);
                return [];
            }

            _missingWarned.Remove(BuildMissingKey(serverId, path.FullName));

            var signature = BuildFileSignature(path);
            var fileSize = path.Length;
            _companionOffsetCache.TryGetValue(companionKey, out var state);
            if (state == default || string.IsNullOrWhiteSpace(state.Signature))
            {
                _companionOffsetCache[companionKey] = (signature, fileSize);
                return [];
            }

            long offset = state.Offset;
            if (!string.Equals(state.Signature, signature, StringComparison.Ordinal))
            {
                offset = 0;
                _companionLineRemainder.Remove(companionKey);
            }

            if (offset > fileSize)
            {
                offset = 0;
                _companionLineRemainder.Remove(companionKey);
            }

            byte[] chunk;
            using (var stream = path.Open(FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                stream.Seek(offset, SeekOrigin.Begin);
                var remaining = fileSize - offset;
                if (remaining <= 0)
                {
                    chunk = [];
                }
                else
                {
                    chunk = new byte[remaining];
                    var read = stream.Read(chunk, 0, chunk.Length);
                    if (read < chunk.Length)
                    {
                        Array.Resize(ref chunk, read);
                    }
                }
            }

            _companionOffsetCache[companionKey] = (signature, offset + chunk.Length);
            if (chunk.Length == 0)
            {
                return [];
            }

            var text = DecodeChunk(chunk);
            if (string.IsNullOrEmpty(text))
            {
                return [];
            }

            if (_companionLineRemainder.TryGetValue(companionKey, out var remainder) && !string.IsNullOrEmpty(remainder))
            {
                text = remainder + text;
            }

            string[] lines;
            if (text.EndsWith('\n') || text.EndsWith('\r'))
            {
                lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                _companionLineRemainder[companionKey] = string.Empty;
            }
            else
            {
                lines = text.Split(['\r', '\n'], StringSplitOptions.None);
                if (lines.Length == 0)
                {
                    _companionLineRemainder[companionKey] = text;
                    return [];
                }

                _companionLineRemainder[companionKey] = lines[^1];
                lines = lines[..^1];
            }

            var result = new List<Vs2QQTalkMessage>();
            foreach (var line in lines)
            {
                var parsed = _parser.Parse(line, chatRegex);
                if (parsed is null)
                {
                    continue;
                }

                result.Add(new Vs2QQTalkMessage(
                    serverId,
                    parsed.Value.Timestamp,
                    parsed.Value.Sender,
                    parsed.Value.Content));
            }

            return result;
        }

        private string DecodeChunk(byte[] chunk)
        {
            try
            {
                return GetEncoding(_defaultEncoding).GetString(chunk);
            }
            catch
            {
                try
                {
                    return GetEncoding(_fallbackEncoding).GetString(chunk);
                }
                catch
                {
                    return Encoding.UTF8.GetString(chunk);
                }
            }
        }

        private static Encoding GetEncoding(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
            {
                return Encoding.UTF8;
            }

            try
            {
                return Encoding.GetEncoding(name.Trim());
            }
            catch
            {
                return Encoding.UTF8;
            }
        }

        private void SetOffset(string serverId, string fileSignature, long offset)
        {
            _offsetCache[serverId] = (fileSignature, offset);
            _storage.SetLogOffset(serverId, fileSignature, offset);
        }

        private static string BuildFileSignature(FileInfo file)
        {
            // Keep signature stable while file is appended; only rotate when file identity changes.
            return $"{file.FullName}:{file.CreationTimeUtc.Ticks}";
        }

        private static IReadOnlyList<string> GetCompanionLogPaths(string logPath)
        {
            var fileName = Path.GetFileName(logPath);
            var directory = Path.GetDirectoryName(logPath);
            if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(directory))
            {
                return [];
            }

            if (string.Equals(fileName, "server-main.log", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    Path.Combine(directory, "server-chat.log"),
                    Path.Combine(directory, "server-audit.log")
                ];
            }

            if (string.Equals(fileName, "server-chat.log", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    Path.Combine(directory, "server-main.log"),
                    Path.Combine(directory, "server-audit.log")
                ];
            }

            if (string.Equals(fileName, "server-audit.log", StringComparison.OrdinalIgnoreCase))
            {
                return
                [
                    Path.Combine(directory, "server-main.log"),
                    Path.Combine(directory, "server-chat.log")
                ];
            }

            return [];
        }

        private static string BuildCompanionKey(string serverId, string path)
        {
            return $"{serverId}|{path}";
        }

        private static string BuildMissingKey(string serverId, string path)
        {
            return $"{serverId}@{path}";
        }
    }

    private sealed class Vs2QQTalkLineParser
    {
        internal const string ServerNotificationSender = "__VS_SERVER_NOTIFICATION__";

        private static readonly string[] KnownTimeFormats =
        [
            "yyyy-MM-dd HH:mm:ss",
            "yyyy/MM/dd HH:mm:ss",
            "yyyy-MM-ddTHH:mm:ss",
            "d.M.yyyy HH:mm:ss",
            "M/d/yyyy HH:mm:ss"
        ];

        private static readonly Regex[] DefaultPatterns =
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

        private static readonly Regex[] NotificationPatterns =
        [
            new(@"^(?:\[log\]\s*)?(?<time>\d{1,2}\.\d{1,2}\.\d{4}\s+\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+Notification|Notification)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"^(?:\[log\]\s*)?(?<time>\d{4}[-/]\d{2}[-/]\d{2}[ T]\d{2}:\d{2}:\d{2})\s*\[(?:Server\s+Notification|Notification)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant),
            new(@"^(?:\[log\]\s*)?\[(?<time>[^\]]+)\]\s*\[(?:Server\s+Notification|Notification)\]\s*Message to all in group \d+:\s*(?<content>.+)$", RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)
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

        private static readonly Regex RuntimeLogLevelPattern =
            new(@"\[(?:Notification|Event|Debug|Verbose|Warning|Error|Audit)\]",
                RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private readonly Dictionary<string, Regex?> _customPatternCache = new(StringComparer.Ordinal);

        public (string Timestamp, string Sender, string Content)? Parse(string line, string? customRegex)
        {
            line = StripLauncherLogPrefix(line);

            if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(line))
            {
                return null;
            }

            var systemEvent = ParseSystemEvent(line);
            if (systemEvent.HasValue)
            {
                return systemEvent;
            }

            var notificationEvent = ParseNotificationEvent(line);
            if (notificationEvent.HasValue)
            {
                return notificationEvent;
            }

            if (IsLikelyRuntimeNoiseLine(line))
            {
                return null;
            }

            if (!string.IsNullOrWhiteSpace(customRegex))
            {
                var customPattern = GetCustomPattern(customRegex);
                if (customPattern is not null)
                {
                    var customMatch = customPattern.Match(line);
                    if (customMatch.Success)
                    {
                        var customResult = ExtractResult(customMatch);
                        if (customResult.HasValue)
                        {
                            if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(
                                    line,
                                    customResult.Value.Sender,
                                    customResult.Value.Content))
                            {
                                return null;
                            }

                            return customResult;
                        }
                    }
                }
            }

            foreach (var pattern in DefaultPatterns)
            {
                var match = pattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var result = ExtractResult(match);
                if (result.HasValue)
                {
                    if (ServerLogPrivacyFilter.ShouldSuppressRelayParts(line, result.Value.Sender, result.Value.Content))
                    {
                        continue;
                    }

                    return result;
                }
            }

            return null;
        }

        private Regex? GetCustomPattern(string pattern)
        {
            if (_customPatternCache.TryGetValue(pattern, out var cached))
            {
                return cached;
            }

            try
            {
                var compiled = new Regex(pattern, RegexOptions.Compiled | RegexOptions.CultureInvariant);
                _customPatternCache[pattern] = compiled;
                return compiled;
            }
            catch
            {
                _customPatternCache[pattern] = null;
                return null;
            }
        }

        private static (string Timestamp, string Sender, string Content)? ExtractResult(Match match)
        {
            var sender = match.Groups["sender"].Value.Trim();
            var content = match.Groups["content"].Value.Trim();
            if (string.IsNullOrWhiteSpace(sender) || string.IsNullOrWhiteSpace(content))
            {
                return null;
            }

            var timeRaw = match.Groups["time"].Value.Trim();
            var timestamp = NormalizeTime(timeRaw);
            return (timestamp, sender, content);
        }

        private static (string Timestamp, string Sender, string Content)? ParseSystemEvent(string line)
        {
            if (TryParsePlayerEventByPatterns(line, JoinEventPatterns, "进入服务器", out var joinSignal))
            {
                return joinSignal;
            }

            if (TryParsePlayerEventByPatterns(line, LeaveEventPatterns, "离开服务器", out var leaveSignal))
            {
                return leaveSignal;
            }

            if (TryParseDeathEventByPatterns(line, DeathEventPatterns, out var deathSignal)
                || TryParseDeathEventByPatterns(line, ChineseDeathEventPatterns, out deathSignal)
                || TryParseDeathEventByPatterns(line, FallDeathEventPatterns, out deathSignal))
            {
                return deathSignal;
            }

            return null;
        }

        private static bool TryParseDeathEventByPatterns(
            string line,
            IEnumerable<Regex> patterns,
            out (string Timestamp, string Sender, string Content) signal)
        {
            signal = default;
            foreach (var pattern in patterns)
            {
                var deathMatch = pattern.Match(line);
                if (!deathMatch.Success)
                {
                    continue;
                }

                var player = deathMatch.Groups["player"].Value.Trim();
                if (string.IsNullOrWhiteSpace(player))
                {
                    continue;
                }

                var timeRaw = deathMatch.Groups["time"].Value.Trim();
                var timestamp = NormalizeTime(timeRaw);
                var reason = deathMatch.Groups["reason"].Value.Trim();
                var content = string.IsNullOrWhiteSpace(reason)
                    ? $"玩家 {player} 死亡"
                    : $"玩家 {player} 死亡：{reason}";
                signal = (timestamp, "系统", content);
                return true;
            }

            return false;
        }

        private static bool TryParsePlayerEventByPatterns(
            string line,
            IEnumerable<Regex> patterns,
            string actionText,
            out (string Timestamp, string Sender, string Content) signal)
        {
            signal = default;
            foreach (var pattern in patterns)
            {
                var match = pattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var player = match.Groups["player"].Value.Trim();
                if (string.IsNullOrWhiteSpace(player))
                {
                    continue;
                }

                var timeRaw = match.Groups["time"].Value.Trim();
                var timestamp = NormalizeTime(timeRaw);
                signal = (timestamp, "系统", $"玩家 {player} {actionText}");
                return true;
            }

            return false;
        }

        private static (string Timestamp, string Sender, string Content)? ParseNotificationEvent(string line)
        {
            foreach (var pattern in NotificationPatterns)
            {
                var match = pattern.Match(line);
                if (!match.Success)
                {
                    continue;
                }

                var content = match.Groups["content"].Value.Trim();
                if (string.IsNullOrWhiteSpace(content))
                {
                    continue;
                }

                var timeRaw = match.Groups["time"].Value.Trim();
                var timestamp = NormalizeTime(timeRaw);
                return (timestamp, ServerNotificationSender, content);
            }

            return null;
        }

        private static string NormalizeTime(string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                foreach (var format in KnownTimeFormats)
                {
                    if (DateTime.TryParseExact(
                        value,
                        format,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeLocal,
                        out var parsed))
                    {
                        return parsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                    }
                }

                if (DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var freeParsed))
                {
                    return freeParsed.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
                }

                return value;
            }

            return DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
        }

        private static bool IsLikelyRuntimeNoiseLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return false;
            }

            if (!RuntimeLogLevelPattern.IsMatch(line))
            {
                return false;
            }

            if (line.Contains("[Talk]", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("[Chat]", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return !line.Contains("Message to all in group", StringComparison.OrdinalIgnoreCase);
        }

        private static string StripLauncherLogPrefix(string line)
        {
            const string prefix = "[log]";
            var trimmed = (line ?? string.Empty).Trim();
            return trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? trimmed[prefix.Length..].TrimStart()
                : trimmed;
        }
    }

    private static class ServerAuthPasswordHasher
    {
        private const string Prefix = "pbkdf2-sha256";

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                120000,
                HashAlgorithmName.SHA256,
                32);
            return string.Join(
                '$',
                Prefix,
                "120000",
                Convert.ToBase64String(salt),
                Convert.ToBase64String(derived));
        }

        public static bool Verify(string password, string encodedHash)
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
            {
                return false;
            }

            if (!int.TryParse(parts[1], out var iterations))
            {
                return false;
            }

            try
            {
                var salt = Convert.FromBase64String(parts[2]);
                var expected = Convert.FromBase64String(parts[3]);
                var derived = Rfc2898DeriveBytes.Pbkdf2(
                    password,
                    salt,
                    iterations,
                    HashAlgorithmName.SHA256,
                    expected.Length);
                return CryptographicOperations.FixedTimeEquals(derived, expected);
            }
            catch
            {
                return false;
            }
        }
    }

    private sealed class VsServerAuthSettings
    {
        public bool Enabled { get; set; }

        public int LoginTimeoutSeconds { get; set; } = 60;

        public int RememberSessionMinutes { get; set; } = 30;

        public VsServerAuthDiscourseSettings Discourse { get; set; } = new();
    }

    private sealed class VsServerAuthDiscourseSettings
    {
        public bool Enabled { get; set; }

        public string BaseUrl { get; set; } = string.Empty;

        public string SharedSecret { get; set; } = string.Empty;

        public string PublicCallbackBaseUrl { get; set; } = "http://127.0.0.1:18092/";

        public string ListenPrefix { get; set; } = "http://127.0.0.1:18092/";
    }

    private sealed class VsServerAuthStore
    {
        public List<VsServerAuthPlayerRecord> Players { get; set; } = [];

        public List<VsServerAuthSessionRecord> Sessions { get; set; } = [];
    }

    private sealed class VsServerAuthPlayerRecord
    {
        public string PlayerUid { get; set; } = string.Empty;

        public string PlayerName { get; set; } = string.Empty;

        public string NormalizedPlayerName { get; set; } = string.Empty;

        public string RegisteredIp { get; set; } = string.Empty;

        public DateTimeOffset RegisteredAtUtc { get; set; }

        public string LastIp { get; set; } = string.Empty;

        public DateTimeOffset? LastLoginAtUtc { get; set; }

        public string PasswordHash { get; set; } = string.Empty;

        public bool PasswordResetRequired { get; set; }

        public string DiscourseExternalId { get; set; } = string.Empty;

        public string DiscourseUsername { get; set; } = string.Empty;

        public string DiscourseEmail { get; set; } = string.Empty;
    }

    private sealed class VsServerAuthSessionRecord
    {
        public string PlayerUid { get; set; } = string.Empty;

        public string IpAddress { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    private readonly record struct Vs2QQTalkMessage(string ServerId, string Timestamp, string Sender, string Content);

    private readonly record struct Vs2QQServerRecord(string ServerId, string LogPath, string? ChatRegex);

    private readonly record struct Vs2QQGroupServerRecord(string ServerId, string LogPath, string? ChatRegex, bool Enabled);

    private readonly record struct Vs2QQRemoteGroupServerRecord(long GroupId, string ServerHost, long BoundByQqId);

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
