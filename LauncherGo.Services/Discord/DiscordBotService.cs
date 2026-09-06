using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Discord;
using Discord.WebSocket;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>Discord transport adapter for the launcher robot command surface.</summary>
public sealed class DiscordBotService : IDiscordBotService, IAsyncDisposable
{
    private const int MessageLimit = 2000;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly string[] BridgeEvents = ["player.joined", "player.left", "player.died", "chat", "server.notification"];
    private readonly IServerProcessService _serverProcessService;
    private readonly IInstanceProfileService _profileService;
    private readonly IInstanceModService _instanceModService;
    private readonly IModListExportService _modListExportService;
    private readonly IModFileArchiveService _modFileArchiveService;
    private readonly IServerBridgeService _serverBridgeService;
    private readonly IInstanceServerConfigService _instanceServerConfigService;
    private readonly IAutomationService _automationService;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _commandDeploymentGate = new(1, 1);
    private readonly object _consoleGate = new();
    private readonly List<string> _console = [];
    private readonly List<ServerBridgeSubscription> _subscriptions = [];
    private readonly Dictionary<string, DiscordPlayerBinding> _playerBindings = new(StringComparer.OrdinalIgnoreCase);
    private DiscordSocketClient? _client;
    private CancellationTokenSource? _runCts;
    private TaskCompletionSource<bool>? _readySource;
    private bool _stopping;
    private DiscordIntegrationSettings _settings = new();
    private DiscordRuntimeStatus _status = new();

    public DiscordBotService(
        IServerProcessService serverProcessService,
        IInstanceProfileService profileService,
        IInstanceModService instanceModService,
        IModListExportService modListExportService,
        IModFileArchiveService modFileArchiveService,
        IServerBridgeService serverBridgeService,
        IInstanceServerConfigService instanceServerConfigService,
        IAutomationService automationService)
    {
        _serverProcessService = serverProcessService;
        _profileService = profileService;
        _instanceModService = instanceModService;
        _modListExportService = modListExportService;
        _modFileArchiveService = modFileArchiveService;
        _serverBridgeService = serverBridgeService;
        _instanceServerConfigService = instanceServerConfigService;
        _automationService = automationService;
        _automationService.RuntimeLogReceived += OnAutomationLogReceived;
    }

    public event EventHandler<DiscordRuntimeStatus>? StatusChanged;
    public event EventHandler<string>? OutputReceived;
    public DiscordRuntimeStatus GetCurrentStatus() => _status;
    public IReadOnlyList<string> GetConsoleLines() { lock (_consoleGate) return _console.ToList(); }
    public void ClearConsole() { lock (_consoleGate) _console.Clear(); }

    public async Task<DiscordIntegrationSettings> LoadSettingsAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        LoadPlayerBindings();
        if (!File.Exists(WorkspacePathHelper.DiscordSettingsPath))
        {
            var defaults = DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings());
            await SaveSettingsAsync(defaults, cancellationToken).ConfigureAwait(false);
            return defaults;
        }
        try
        {
            var json = await File.ReadAllTextAsync(WorkspacePathHelper.DiscordSettingsPath, cancellationToken).ConfigureAwait(false);
            return DiscordIntegrationSettingsRules.Normalize(JsonSerializer.Deserialize<DiscordIntegrationSettings>(json) ?? new());
        }
        catch (Exception ex)
        {
            Append($"[warn] Discord 配置读取失败：{ex.Message}");
            return DiscordIntegrationSettingsRules.Normalize(new DiscordIntegrationSettings());
        }
    }

    public async Task SaveSettingsAsync(DiscordIntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        _settings = DiscordIntegrationSettingsRules.Normalize(settings);
        await File.WriteAllTextAsync(WorkspacePathHelper.DiscordSettingsPath, JsonSerializer.Serialize(_settings, JsonOptions), cancellationToken).ConfigureAwait(false);
    }

    public async Task StartAsync(DiscordIntegrationSettings settings, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_status.IsRunning) throw new InvalidOperationException("Discord 机器人已在运行中。");
            _settings = DiscordIntegrationSettingsRules.Normalize(settings);
            if (string.IsNullOrWhiteSpace(_settings.BotToken)) throw new InvalidOperationException("缺少 Discord Bot Token。");
            if (!DiscordIntegrationSettingsRules.IsValidBotToken(_settings.BotToken)) throw new InvalidOperationException("Discord Bot Token 格式无效。");
            await SaveSettingsAsync(_settings, cancellationToken).ConfigureAwait(false);
            LoadPlayerBindings();
            _stopping = false;
            _readySource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _runCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var client = new DiscordSocketClient(new DiscordSocketConfig { GatewayIntents = GatewayIntents.Guilds, AlwaysDownloadUsers = false, LogLevel = LogSeverity.Info });
            client.Log += OnDiscordLogAsync;
            client.Ready += OnReadyAsync;
            client.Disconnected += OnDisconnectedAsync;
            client.InteractionCreated += OnInteractionCreatedAsync;
            _client = client;
            await client.LoginAsync(TokenType.Bot, _settings.BotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            await _readySource.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
            Append("[system] Discord 机器人已启动。");
        }
        catch (Exception ex)
        {
            var message = FormatConnectionError(ex);
            Append($"[error] {message}");
            SetStatus(new DiscordRuntimeStatus { LastError = message });
            await DisposeClientAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _stopping = true;
            _runCts?.Cancel();
            ServerBridgeSubscription[] subscriptions;
            lock (_subscriptions) subscriptions = _subscriptions.ToArray();
            foreach (var subscription in subscriptions) { try { await subscription.DisposeAsync().ConfigureAwait(false); } catch { } }
            lock (_subscriptions) _subscriptions.Clear();
            if (_client is not null) { try { await _client.StopAsync().WaitAsync(gracefulTimeout, cancellationToken).ConfigureAwait(false); } catch { } }
            await DisposeClientAsync().ConfigureAwait(false);
            SetStatus(new DiscordRuntimeStatus());
            Append("[system] Discord 机器人已停止。");
        }
        finally { _gate.Release(); }
    }

    public async Task RedeployCommandsAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null || !_status.IsConnected)
            throw new InvalidOperationException("Discord 机器人尚未连接，无法重新部署命令。");

        await DeployCommandsAsync(throwOnFailure: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task OnReadyAsync()
    {
        if (_client is null) return;
        SetStatus(new DiscordRuntimeStatus { IsRunning = true, IsConnected = true, StartedAtUtc = _status.StartedAtUtc ?? DateTimeOffset.UtcNow, BotUserId = _client.CurrentUser.Id.ToString(CultureInfo.InvariantCulture) });
        _readySource?.TrySetResult(true);
        await DeployCommandsAsync(throwOnFailure: false, cancellationToken: _runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
        foreach (var profileId in _settings.ProfileBindings.Select(x => x.ProfileId).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var profile = _profileService.GetProfileById(profileId);
            if (profile is null) continue;
            _ = MaintainBridgeSubscriptionAsync(profile, _runCts?.Token ?? CancellationToken.None);
        }
    }

    private async Task MaintainBridgeSubscriptionAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !_stopping)
        {
            try
            {
                var subscription = await _serverBridgeService.SubscribeAsync(profile, new ServerBridgeSubscriptionOptions { Events = BridgeEvents, MaxQueueSize = 256 }, evt => HandleBridgeEventAsync(profile.Id, evt), cancellationToken).ConfigureAwait(false);
                lock (_subscriptions) _subscriptions.Add(subscription);
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                Append($"[warn] Discord 桥接订阅失败 profile={profile.Id}：{ex.Message}");
                try { await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private async Task DeployCommandsAsync(bool throwOnFailure, CancellationToken cancellationToken)
    {
        if (_client is null) return;
        await _commandDeploymentGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var failures = new List<Exception>();
            var deployedGuilds = 0;
            foreach (var bindings in _settings.ProfileBindings.GroupBy(x => x.GuildId, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!ulong.TryParse(bindings.Key, NumberStyles.None, CultureInfo.InvariantCulture, out var guildId)) continue;
                var guild = _client.GetGuild(guildId);
                if (guild is null) continue;
                var language = await ResolveGuildLanguageAsync(bindings, cancellationToken).ConfigureAwait(false);
                try
                {
                    await guild.BulkOverwriteApplicationCommandAsync(BuildCommands(language).ToArray()).ConfigureAwait(false);
                    deployedGuilds++;
                    Append($"[system] Discord commands deployed. guild={guildId} language={language}");
                }
                catch (Exception ex)
                {
                    Append($"[warn] Discord 命令注册失败 guild={guildId}: {ex.Message}");
                    failures.Add(new InvalidOperationException($"Guild {guildId}: {ex.Message}", ex));
                }
            }

            if (throwOnFailure && failures.Count > 0)
                throw new AggregateException("部分 Discord Guild 命令部署失败。", failures);
            if (throwOnFailure && deployedGuilds == 0)
                throw new InvalidOperationException("没有可用的 Discord Guild 绑定，未部署任何命令。");
        }
        finally
        {
            _commandDeploymentGate.Release();
        }
    }

    private async Task<string> ResolveGuildLanguageAsync(
        IEnumerable<DiscordProfileBinding> bindings,
        CancellationToken cancellationToken)
    {
        foreach (var binding in bindings)
        {
            var profile = _profileService.GetProfileById(binding.ProfileId);
            if (profile is null) continue;
            return await ResolveProfileLanguageAsync(profile, cancellationToken).ConfigureAwait(false);
        }
        return "en";
    }

    private async Task<string> ResolveProfileLanguageAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var settings = await _instanceServerConfigService.LoadServerSettingsAsync(profile, cancellationToken).ConfigureAwait(false);
            return DiscordBotText.NormalizeLanguage(settings.ServerLanguage);
        }
        catch (Exception ex)
        {
            Append($"[warn] Discord server language read failed profile={profile.Id}: {ex.Message}");
            return "en";
        }
    }

    private IReadOnlyCollection<ApplicationCommandProperties> BuildCommands(string language)
    {
        string Text(DiscordBotPhrase phrase) => DiscordBotText.Get(language, phrase);
        var commands = new List<ApplicationCommandProperties>
        {
            new SlashCommandBuilder().WithName("help").WithDescription(Text(DiscordBotPhrase.ShowCommands)).Build(),
            new SlashCommandBuilder().WithName("myinfo").WithDescription(Text(DiscordBotPhrase.ShowMyPlayer)).Build(),
            new SlashCommandBuilder().WithName("bind").WithDescription(Text(DiscordBotPhrase.BindPlayer)).AddOption("player", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.Players), true).Build(),
            new SlashCommandBuilder().WithName("send").WithDescription(Text(DiscordBotPhrase.SendServerCommand)).AddOption("message", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.SendServerCommand), true).Build(),
            new SlashCommandBuilder().WithName("server").WithDescription(Text(DiscordBotPhrase.ManageServer))
                .AddOption(new SlashCommandOptionBuilder().WithName("status").WithDescription(Text(DiscordBotPhrase.ShowServerStatus)).WithType(ApplicationCommandOptionType.SubCommand).AddOption("profile", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.OptionalProfile), false))
                .AddOption(new SlashCommandOptionBuilder().WithName("players").WithDescription(Text(DiscordBotPhrase.ShowOnlinePlayers)).WithType(ApplicationCommandOptionType.SubCommand).AddOption("profile", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.OptionalProfile), false))
                .AddOption(new SlashCommandOptionBuilder().WithName("start").WithDescription(Text(DiscordBotPhrase.StartServer)).WithType(ApplicationCommandOptionType.SubCommand).AddOption("profile", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.OptionalProfile), false))
                .AddOption(new SlashCommandOptionBuilder().WithName("stop").WithDescription(Text(DiscordBotPhrase.StopServer)).WithType(ApplicationCommandOptionType.SubCommand).AddOption("profile", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.OptionalProfile), false))
                .AddOption(new SlashCommandOptionBuilder().WithName("password").WithDescription(Text(DiscordBotPhrase.GetOrSetPassword)).WithType(ApplicationCommandOptionType.SubCommand).AddOption("value", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.PasswordAction), false))
                .Build(),
            new SlashCommandBuilder().WithName("modslist").WithDescription(Text(DiscordBotPhrase.ExportMods)).AddOption("format", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.ExportFormat), true).Build(),
            new SlashCommandBuilder().WithName("modfile").WithDescription(Text(DiscordBotPhrase.ExportUniversalMods)).Build(),
            new SlashCommandBuilder().WithName("modfileall").WithDescription(Text(DiscordBotPhrase.ExportAllMods)).Build(),
            new SlashCommandBuilder().WithName("custom").WithDescription(Text(DiscordBotPhrase.RunCustomCommand)).AddOption("name", ApplicationCommandOptionType.String, Text(DiscordBotPhrase.CustomCommandName), true).Build()
        };
        foreach (var custom in _settings.CustomCommands.Where(x => DiscordIntegrationSettingsRules.IsNativeSlashCommandName(x.Command)))
        {
            var name = custom.Command.TrimStart('/').ToLowerInvariant();
            if (!commands.Any(x => x.Name.IsSpecified && string.Equals(x.Name.Value, name, StringComparison.OrdinalIgnoreCase))) commands.Add(new SlashCommandBuilder().WithName(name).WithDescription(Text(DiscordBotPhrase.ConfiguredCustomCommand)).Build());
        }
        return commands;
    }

    private async Task OnInteractionCreatedAsync(SocketInteraction interaction)
    {
        if (interaction is not SocketSlashCommand command) return;
        var language = "en";
        try
        {
            var binding = ResolveBinding(command);
            var boundProfile = GetProfile(binding);
            if (boundProfile is not null)
                language = await ResolveProfileLanguageAsync(boundProfile, _runCts?.Token ?? CancellationToken.None).ConfigureAwait(false);
            var admin = IsAdmin(command);
            switch (command.Data.Name.ToLowerInvariant())
            {
                case "help": await RespondTextAsync(command, BuildHelpText(language), false); return;
                case "myinfo": await HandleMyInfoAsync(command, binding); return;
                case "bind": await HandleBindAsync(command, binding); return;
                case "custom": await HandleCustomAsync(command, binding); return;
                case "send": if (!admin) { await RespondTextAsync(command, "没有执行该命令的权限。", true); return; } await HandleSendAsync(command, binding); return;
                case "server": await HandleServerAsync(command, binding, admin); return;
                case "modslist": if (!admin) { await RespondTextAsync(command, "没有执行该命令的权限。", true); return; } await HandleModsListAsync(command, binding); return;
                case "modfile": case "modfileall": if (!admin) { await RespondTextAsync(command, "没有执行该命令的权限。", true); return; } await HandleModFileAsync(command, binding, command.Data.Name.Equals("modfileall", StringComparison.OrdinalIgnoreCase)); return;
                default: await HandleNativeCustomAsync(command, binding); return;
            }
        }
        catch (Exception ex)
        {
            Append($"[warn] Discord 命令处理失败：{ex.Message}");
            if (!command.HasResponded) await RespondTextAsync(command, "命令执行失败，请查看机器人状态日志。", true);
        }
    }

    private async Task HandleSendAsync(SocketSlashCommand command, DiscordProfileBinding? binding)
    {
        var profile = GetProfile(binding);
        if (profile is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        var message = GetOption(command, "message");
        await _serverProcessService.SendCommandAsync(profile.Id, message, _runCts?.Token ?? CancellationToken.None);
        await RespondTextAsync(command, $"已发送服务端指令：{message}", false);
    }

    private async Task HandleServerAsync(SocketSlashCommand command, DiscordProfileBinding? binding, bool admin)
    {
        var subCommand = command.Data.Options.FirstOrDefault();
        var action = subCommand?.Name.Equals("action", StringComparison.OrdinalIgnoreCase) == true
            ? subCommand.Value?.ToString() ?? string.Empty
            : subCommand?.Name ?? GetOption(command, "action");
        var value = subCommand?.Options.FirstOrDefault()?.Value?.ToString() ?? GetOption(command, "value");
        action = action.ToLowerInvariant();
        var profile = ResolveProfile(binding, action == "password" ? string.Empty : value);
        if (profile is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        if (action is "start" or "stop" or "password" && !admin) { await RespondTextAsync(command, "没有执行该命令的权限。", true); return; }
        var token = _runCts?.Token ?? CancellationToken.None;
        if (action is "status" or "players")
        {
            var result = await _serverBridgeService.QueryAsync(profile, action == "status" ? "server.status" : "players.list", cancellationToken: token);
            var language = await ResolveProfileLanguageAsync(profile, token).ConfigureAwait(false);
            var formatted = result.Data is null
                ? string.Empty
                : action == "status"
                    ? DiscordBotText.FormatServerStatus(result.Data, language)
                    : DiscordBotText.FormatPlayers(result.Data, language);
            await RespondTextAsync(command, result.Success && result.Data is not null ? formatted : result.Error ?? "服务器桥接不可用。", false);
            return;
        }
        if (action == "start") { await _serverProcessService.StartAsync(profile, token); await RespondTextAsync(command, $"已启动服务器：{profile.Name}", false); return; }
        if (action == "stop") { await _serverProcessService.StopAsync(profile.Id, TimeSpan.FromSeconds(15), token); await RespondTextAsync(command, $"已停止服务器：{profile.Name}", false); return; }
        if (action == "password")
        {
            var server = await _instanceServerConfigService.LoadServerSettingsAsync(profile, token);
            var world = await _instanceServerConfigService.LoadWorldSettingsAsync(profile, token);
            var rules = await _instanceServerConfigService.LoadWorldRulesAsync(profile, token);
            if (string.Equals(value, "get", StringComparison.OrdinalIgnoreCase))
            {
                await RespondTextAsync(command, string.IsNullOrWhiteSpace(server.Password) ? "服务器密码为空。" : $"服务器加入密码：{server.Password}", true);
                return;
            }
            if (value.StartsWith("set ", StringComparison.OrdinalIgnoreCase)) server.Password = value[4..].Trim();
            else if (value == "-" || string.Equals(value, "clear", StringComparison.OrdinalIgnoreCase)) server.Password = null;
            else { await RespondTextAsync(command, "用法：/server action=password value=get|set <password>|-", true); return; }
            await _instanceServerConfigService.SaveSettingsAsync(profile, server, world, rules, token);
            await RespondTextAsync(command, string.IsNullOrWhiteSpace(server.Password) ? "密码已清空。" : "密码已更新。", true);
            return;
        }
        await RespondTextAsync(command, "用法：/server action=status|players|start|stop", true);
    }

    private async Task HandleBindAsync(SocketSlashCommand command, DiscordProfileBinding? binding)
    {
        var profile = GetProfile(binding);
        if (profile is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        var name = GetOption(command, "player");
        var result = await _serverBridgeService.QueryAsync(profile, "players.list", cancellationToken: _runCts?.Token ?? CancellationToken.None);
        var player = result.Data?["players"] is JsonArray players ? players.OfType<JsonObject>().FirstOrDefault(x => ReadString(x, "name").Equals(name, StringComparison.OrdinalIgnoreCase)) : null;
        if (!result.Success || player is null) { await RespondTextAsync(command, $"未找到在线玩家：{name}", true); return; }
        var userId = command.User.Id.ToString(CultureInfo.InvariantCulture);
        _playerBindings[userId] = new DiscordPlayerBinding { UserId = userId, ProfileId = profile.Id, PlayerUid = ReadString(player, "uid"), PlayerName = ReadString(player, "name") };
        SavePlayerBindings();
        await RespondTextAsync(command, $"已绑定玩家：{ReadString(player, "name")}（{profile.Name}）", true);
    }

    private async Task HandleMyInfoAsync(SocketSlashCommand command, DiscordProfileBinding? channelBinding)
    {
        if (channelBinding is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        if (!_playerBindings.TryGetValue(command.User.Id.ToString(CultureInfo.InvariantCulture), out var player)) { await RespondTextAsync(command, "尚未绑定游戏玩家，请先使用 /bind。", true); return; }
        var profile = _profileService.GetProfileById(player.ProfileId);
        if (profile is null) { await RespondTextAsync(command, "绑定的服务器档案已不存在，请重新绑定。", true); return; }
        var result = await _serverBridgeService.QueryAsync(profile, "player.info", new JsonObject { ["uid"] = player.PlayerUid, ["name"] = player.PlayerName }, _runCts?.Token ?? CancellationToken.None);
        await RespondTextAsync(command, result.Success && result.Data is not null ? FormatJsonResult("player", result.Data) : "当前无法读取玩家实时信息。", true);
    }

    private async Task HandleModsListAsync(SocketSlashCommand command, DiscordProfileBinding? binding)
    {
        var profile = GetProfile(binding);
        var value = GetOption(command, "format").ToLowerInvariant();
        var format = value switch { "txt" => ModListExportFormat.Txt, "csv" => ModListExportFormat.Csv, "pdf" => ModListExportFormat.Pdf, "md" or "markdown" => ModListExportFormat.Markdown, "xlsx" => ModListExportFormat.Xlsx, _ => (ModListExportFormat?)null };
        if (profile is null || format is null) { await RespondTextAsync(command, "当前频道未绑定档案，或导出格式无效。", true); return; }
        var directory = Path.Combine(WorkspacePathHelper.DiscordRoot, "exports"); Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"mods-{WorkspacePathHelper.SanitizeFileName(profile.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.{_modListExportService.GetFileExtension(format.Value)}");
        try
        {
            var mods = await _instanceModService.GetModsAsync(profile, _runCts?.Token ?? CancellationToken.None);
            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) await _modListExportService.ExportAsync(profile, mods, format.Value, stream, _runCts?.Token ?? CancellationToken.None);
            await RespondWithFileAsync(command, path, $"已输出模组清单：{Path.GetFileName(path)}");
        }
        finally { TryDelete(path); }
    }

    private async Task HandleModFileAsync(SocketSlashCommand command, DiscordProfileBinding? binding, bool all)
    {
        var profile = GetProfile(binding);
        if (profile is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        var path = Path.Combine(WorkspacePathHelper.DiscordRoot, "exports", $"mods-{(all ? "all" : "universal")}-{WorkspacePathHelper.SanitizeFileName(profile.Name)}-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.zip"); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        try
        {
            var mods = await _instanceModService.GetModsAsync(profile, _runCts?.Token ?? CancellationToken.None);
            await using (var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read)) await _modFileArchiveService.CreateModArchiveAsync(profile, mods, all ? ModFileArchiveScope.All : ModFileArchiveScope.UniversalOnly, stream, _runCts?.Token ?? CancellationToken.None);
            await RespondWithFileAsync(command, path, $"已发送模组压缩包：{Path.GetFileName(path)}");
        }
        finally { TryDelete(path); }
    }

    private async Task HandleCustomAsync(SocketSlashCommand command, DiscordProfileBinding? binding)
    {
        if (binding is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        var custom = _settings.CustomCommands.FirstOrDefault(x => x.Command.TrimStart('/').Equals(GetOption(command, "name").TrimStart('/'), StringComparison.OrdinalIgnoreCase));
        if (custom is null) { await RespondTextAsync(command, "未找到自定义指令。", true); return; }
        await RespondCustomAsync(command, custom);
    }

    private async Task HandleNativeCustomAsync(SocketSlashCommand command, DiscordProfileBinding? binding)
    {
        if (binding is null) { await RespondTextAsync(command, "当前频道未绑定服务器档案。", true); return; }
        var custom = _settings.CustomCommands.FirstOrDefault(x => x.Command.TrimStart('/').Equals(command.Data.Name, StringComparison.OrdinalIgnoreCase));
        if (custom is null) { await RespondTextAsync(command, "未找到自定义指令。", true); return; }
        await RespondCustomAsync(command, custom);
    }

    private async Task RespondCustomAsync(SocketSlashCommand command, RobotCustomCommand custom)
    {
        if (custom.MessageType == RobotCustomMessageType.Image && File.Exists(custom.Content)) await RespondWithFileAsync(command, custom.Content, string.Empty);
        else await RespondTextAsync(command, custom.Content, false);
    }

    private async Task HandleBridgeEventAsync(string profileId, ServerBridgeEvent evt)
    {
        var text = evt.Event switch
        {
            "player.joined" => $"[服务器 {evt.TimestampUtc.ToLocalTime():HH:mm:ss}] {ReadString(evt.Data, "name")} 进入服务器",
            "player.left" => $"[服务器 {evt.TimestampUtc.ToLocalTime():HH:mm:ss}] {ReadString(evt.Data, "name")} 离开服务器",
            "player.died" => $"[服务器 {evt.TimestampUtc.ToLocalTime():HH:mm:ss}] {ReadString(evt.Data, "name")} 死亡",
            "chat" => $"[服务器 {evt.TimestampUtc.ToLocalTime():HH:mm:ss}] {ReadString(evt.Data, "name")}：{ReadString(evt.Data, "message")}",
            "server.notification" => $"[服务器 {evt.TimestampUtc.ToLocalTime():HH:mm:ss}] {ReadString(evt.Data, "message")}",
            _ => string.Empty
        };
        if (_client is null || string.IsNullOrWhiteSpace(text)) return;
        foreach (var binding in _settings.ProfileBindings.Where(x => x.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (_client.GetChannel(ulong.Parse(binding.ChannelId, CultureInfo.InvariantCulture)) is IMessageChannel channel)
                    foreach (var part in RobotCommandDispatcher.SplitText(text, MessageLimit)) await channel.SendMessageAsync(part).ConfigureAwait(false);
            }
            catch (Exception ex) { Append($"[warn] Discord 事件发送失败 channel={binding.ChannelId}: {ex.Message}"); }
        }
    }

    private DiscordProfileBinding? ResolveBinding(SocketSlashCommand command)
    {
        var guild = command.GuildId?.ToString(CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(guild) ? null : DiscordIntegrationSettingsRules.FindBinding(_settings, guild, command.Channel.Id.ToString(CultureInfo.InvariantCulture));
    }

    private bool IsAdmin(SocketSlashCommand command) => DiscordIntegrationSettingsRules.IsAdministrator(_settings, command.User.Id.ToString(CultureInfo.InvariantCulture), command.User is SocketGuildUser user ? user.Roles.Select(role => role.Id.ToString(CultureInfo.InvariantCulture)) : []);
    private InstanceProfile? GetProfile(DiscordProfileBinding? binding) => binding is null ? null : _profileService.GetProfileById(binding.ProfileId);
    private InstanceProfile? ResolveProfile(DiscordProfileBinding? binding, string selector)
    {
        if (binding is null) return null;
        var profiles = _profileService.GetProfiles();
        return string.IsNullOrWhiteSpace(selector) ? profiles.FirstOrDefault(x => x.Id.Equals(binding.ProfileId, StringComparison.OrdinalIgnoreCase)) : profiles.FirstOrDefault(x => x.Id.Equals(selector, StringComparison.OrdinalIgnoreCase) || x.Name.Equals(selector, StringComparison.OrdinalIgnoreCase));
    }
    private static string GetOption(SocketSlashCommand command, string name) => command.Data.Options.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase))?.Value?.ToString()?.Trim() ?? string.Empty;
    private static string ReadString(JsonObject data, string key) => data[key]?.ToString()?.Trim() ?? string.Empty;
    private static string FormatJsonResult(string kind, JsonObject data) => $"{kind}\n" + string.Join('\n', data.Select(x => $"{x.Key}: {x.Value}"));
    private static string BuildHelpText(string language) =>
        $"**{DiscordBotText.Get(language, DiscordBotPhrase.ShowCommands)}**\n" +
        "`/help` · `/send` · `/server` · `/bind` · `/myinfo` · `/modslist` · `/modfile` · `/modfileall` · `/custom`";

    private async Task RespondTextAsync(SocketSlashCommand command, string text, bool ephemeral)
    {
        var parts = RobotCommandDispatcher.SplitText(text, MessageLimit);
        await command.RespondAsync(parts[0], ephemeral: ephemeral).ConfigureAwait(false);
        foreach (var part in parts.Skip(1)) await command.FollowupAsync(part, ephemeral: ephemeral).ConfigureAwait(false);
    }
    private static Task RespondWithFileAsync(SocketSlashCommand command, string path, string text) => command.RespondWithFileAsync(path, text: text, ephemeral: false);
    private static void TryDelete(string path) { try { if (File.Exists(path)) File.Delete(path); } catch { } }

    private async Task OnDisconnectedAsync(Exception exception)
    {
        var message = FormatConnectionError(exception);
        SetStatus(new DiscordRuntimeStatus { IsRunning = _status.IsRunning && !_stopping, IsConnected = false, StartedAtUtc = _status.StartedAtUtc, BotUserId = _status.BotUserId, LastError = message });
        Append($"[discord] {message}");
        _ = ResetSubscriptionsAsync();
        _readySource?.TrySetException(exception);
        await Task.CompletedTask;
    }

    private async Task ResetSubscriptionsAsync()
    {
        ServerBridgeSubscription[] subscriptions;
        lock (_subscriptions) { subscriptions = _subscriptions.ToArray(); _subscriptions.Clear(); }
        foreach (var subscription in subscriptions) { try { await subscription.DisposeAsync().ConfigureAwait(false); } catch { } }
    }
    private Task OnDiscordLogAsync(LogMessage message) { Append(message.Exception is null ? $"[discord] {message.Severity}: {message.Message}" : $"[discord] {message.Message}: {message.Exception.Message}"); return Task.CompletedTask; }
    private static string FormatConnectionError(Exception exception)
    {
        if (exception is TimeoutException)
        {
            return "连接 Discord 超时（discord.com:443）。请检查本机网络、防火墙或代理设置；Token 尚未完成鉴权。";
        }

        var text = exception.ToString();
        if (text.Contains("discord.com:443", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SocketError.TimedOut", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("SocketError.ConnectionRefused", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("连接方在一段时间后", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("目标计算机积极拒绝", StringComparison.OrdinalIgnoreCase))
        {
            return "无法连接 Discord（discord.com:443）。请检查本机网络、防火墙或代理设置；Token 尚未完成鉴权。";
        }

        return exception.Message;
    }
    private void SetStatus(DiscordRuntimeStatus status) { _status = status; StatusChanged?.Invoke(this, status); }
    private void Append(string message)
    {
        lock (_consoleGate)
        {
            _console.Add(message);
            while (_console.Count > 3000) _console.RemoveAt(0);
            try
            {
                Directory.CreateDirectory(WorkspacePathHelper.DiscordRoot);
                File.AppendAllText(WorkspacePathHelper.DiscordLogPath, $"[{DateTimeOffset.Now:O}] {message}{Environment.NewLine}");
            }
            catch { }
        }
        OutputReceived?.Invoke(this, message);
    }

    private void OnAutomationLogReceived(object? sender, string message)
    {
        if (!_status.IsConnected || _client is null || !message.Contains("自动化播报", StringComparison.OrdinalIgnoreCase)) return;
        var marker = "档案：";
        var start = message.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (start < 0) return;
        start += marker.Length;
        var end = message.IndexOf("）：", start, StringComparison.Ordinal);
        if (end < 0) end = message.Length;
        var profileName = message[start..end].Trim();
        var profile = _profileService.GetProfiles().FirstOrDefault(x => x.Name.Equals(profileName, StringComparison.OrdinalIgnoreCase));
        if (profile is null) return;
        _ = SendProfileMessageAsync(profile.Id, $"[自动化] {message}");
    }

    private async Task SendProfileMessageAsync(string profileId, string message)
    {
        if (_client is null) return;
        foreach (var binding in _settings.ProfileBindings.Where(x => x.ProfileId.Equals(profileId, StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (_client.GetChannel(ulong.Parse(binding.ChannelId, CultureInfo.InvariantCulture)) is IMessageChannel channel)
                    foreach (var part in RobotCommandDispatcher.SplitText(message, MessageLimit)) await channel.SendMessageAsync(part).ConfigureAwait(false);
            }
            catch (Exception ex) { Append($"[warn] Discord 自动化通知发送失败 channel={binding.ChannelId}: {ex.Message}"); }
        }
    }

    private async Task DisposeClientAsync()
    {
        _runCts?.Cancel(); _runCts?.Dispose(); _runCts = null;
        if (_client is not null) { try { await _client.LogoutAsync().ConfigureAwait(false); } catch { } try { await _client.DisposeAsync().ConfigureAwait(false); } catch { } _client = null; }
    }

    private void LoadPlayerBindings()
    {
        _playerBindings.Clear();
        try
        {
            if (!File.Exists(WorkspacePathHelper.DiscordPlayerBindingsPath)) return;
            foreach (var item in JsonSerializer.Deserialize<List<DiscordPlayerBinding>>(File.ReadAllText(WorkspacePathHelper.DiscordPlayerBindingsPath)) ?? []) if (!string.IsNullOrWhiteSpace(item.UserId)) _playerBindings[item.UserId] = item;
        }
        catch { }
    }
    private void SavePlayerBindings() { WorkspacePathHelper.EnsureWorkspace(); File.WriteAllText(WorkspacePathHelper.DiscordPlayerBindingsPath, JsonSerializer.Serialize(_playerBindings.Values, JsonOptions)); }
    public async ValueTask DisposeAsync()
    {
        _automationService.RuntimeLogReceived -= OnAutomationLogReceived;
        await StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
    }

    private sealed class DiscordPlayerBinding
    {
        public string UserId { get; set; } = string.Empty;
        public string ProfileId { get; set; } = string.Empty;
        public string PlayerUid { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
    }
}
