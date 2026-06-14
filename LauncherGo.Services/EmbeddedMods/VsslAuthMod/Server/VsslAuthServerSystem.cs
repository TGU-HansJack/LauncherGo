using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;
using Vintagestory.API.Util;
using VsslAuth.Network;

namespace VsslAuth.Server;

public sealed class VsslAuthServerSystem : ModSystem
{
    private const string SettingsFileName = "serverauth.json";
    private const string StoreRelativePath = "ServerAuth/players.json";
    private const string CharacterSelectionChannelName = "charselection";
    private const string DeferredCharacterSelectionModDataKey = "serverauth.deferCharacterSelection";
    private const int MinPasswordLength = 6;
    private const int MaxPasswordLength = 128;
    private const int DiscourseChallengeMinutes = 10;
    private const EnumChatType SystemChatType = (EnumChatType)4;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _storeLock = new();
    private readonly object _authLock = new();
    private readonly Dictionary<string, PendingAuthState> _pendingByUid = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DiscourseChallengeState> _discourseByNonce = new(StringComparer.Ordinal);

    private ICoreServerAPI? _api;
    private IServerNetworkChannel? _channel;
    private ServerAuthSettings _settings = ServerAuthSettings.Default();
    private PlayerStore _store = new();
    private HttpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _listenerTask;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide != EnumAppSide.Client;
    }

    public override double ExecuteOrder()
    {
        return 0.0011;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _channel = api.Network.GetChannel(VsslAuthModSystem.ChannelName);
        _settings = LoadSettings();
        _store = LoadStore();

        api.ChatCommands.Create("register")
            .WithDescription("Register a ServerAuth password")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(
                api.ChatCommands.Parsers.Word("密码"),
                api.ChatCommands.Parsers.Word("再确认密码"))
            .HandleWith(CmdRegister);

        api.ChatCommands.Create("login")
            .WithDescription("Login with a ServerAuth password")
            .RequiresPrivilege(Privilege.chat)
            .RequiresPlayer()
            .WithArgs(api.ChatCommands.Parsers.Word("密码"))
            .HandleWith(CmdLogin);

        api.ChatCommands.Create("serverauth")
            .WithDescription("ServerAuth admin commands")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(CmdServerAuthAdmin);

        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerChat += OnPlayerChat;
        api.Event.CanPlaceOrBreakBlock += OnCanPlaceOrBreakBlock;
        api.Event.CanUseBlock += OnCanUseBlock;
        api.Event.HandInteract += OnHandInteract;
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;
        api.Event.RegisterGameTickListener(OnAuthTick, 1000, 0);

        if (_settings.Enabled && _settings.Discourse.Enabled)
            StartDiscourseListener();
    }

    public override void Dispose()
    {
        StopDiscourseListener();
    }

    private TextCommandResult CmdRegister(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Only players can use this command.", "");

        if (!_settings.Enabled)
            return TextCommandResult.Success("服务器未启用 ServerAuth。", null);

        if (_settings.Discourse.Enabled)
            return TextCommandResult.Error("当前服务器使用社区认证，请在浏览器中完成认证。", "");

        var password = (args[0] as string ?? string.Empty).Trim();
        var confirmPassword = (args[1] as string ?? string.Empty).Trim();

        if (!ValidatePasswordLength(password, out var error))
            return TextCommandResult.Error(error, "");

        if (!password.Equals(confirmPassword, StringComparison.Ordinal))
            return TextCommandResult.Error("两次输入的密码不一致。", "");

        lock (_storeLock)
        {
            if (IsNicknameTakenByOtherUid(player))
                return TextCommandResult.Error("该昵称已经被其他 UUID 注册，请联系管理员。", "");

            var existing = FindPlayer(player.PlayerUID);
            if (existing is not null &&
                !string.IsNullOrWhiteSpace(existing.PasswordHash) &&
                !existing.PasswordResetRequired)
            {
                return TextCommandResult.Error("你已经注册过，请使用 /login <密码> 登录。", "");
            }

            var now = DateTimeOffset.UtcNow;
            var record = existing ?? new ServerAuthPlayerRecord
            {
                PlayerUid = player.PlayerUID,
                RegisteredAtUtc = now,
                RegisteredIp = player.IpAddress ?? string.Empty
            };

            if (existing is null)
                _store.Players.Add(record);

            record.PlayerName = player.PlayerName;
            record.NormalizedPlayerName = NormalizePlayerName(player.PlayerName);
            record.RegisteredIp = string.IsNullOrWhiteSpace(record.RegisteredIp)
                ? player.IpAddress ?? string.Empty
                : record.RegisteredIp;
            record.PasswordHash = PasswordHasher.Hash(password);
            record.PasswordResetRequired = false;
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;

            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        var finalMessage = Authenticate(
            player,
            "注册成功！现在可以移动了！",
            sendChatMessage: false,
            appendCharacterSelectionHint: false);
        return TextCommandResult.Success(finalMessage, null);
    }

    private TextCommandResult CmdLogin(TextCommandCallingArgs args)
    {
        if (args.Caller.Player is not IServerPlayer player)
            return TextCommandResult.Error("Only players can use this command.", "");

        if (!_settings.Enabled)
            return TextCommandResult.Success("服务器未启用 ServerAuth。", null);

        if (_settings.Discourse.Enabled)
            return TextCommandResult.Error("当前服务器使用社区认证，请在浏览器中完成认证。", "");

        var password = (args[0] as string ?? string.Empty).Trim();

        lock (_storeLock)
        {
            var record = FindPlayer(player.PlayerUID);
            if (record is null ||
                string.IsNullOrWhiteSpace(record.PasswordHash) ||
                record.PasswordResetRequired)
            {
                return TextCommandResult.Error("你还没有注册，请使用 /register <密码> <再确认密码>。", "");
            }

            if (!PasswordHasher.Verify(password, record.PasswordHash))
                return TextCommandResult.Error("密码错误。", "");

            var now = DateTimeOffset.UtcNow;
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;
            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        var finalMessage = Authenticate(player, "登录成功，已通过认证。", sendChatMessage: false);
        return TextCommandResult.Success(finalMessage, null);
    }

    private TextCommandResult CmdServerAuthAdmin(TextCommandCallingArgs args)
    {
        var raw = (args[0] as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = (args.LastArg as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = args.RawArgs?.PopAll() ?? string.Empty;

        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2 || !parts[0].Equals("admin", StringComparison.OrdinalIgnoreCase))
        {
            return TextCommandResult.Error(
                "Usage: /serverauth admin clearpassword <player> | setpassword <player> <password> | clearsessions",
                "");
        }

        var action = parts[1].ToLowerInvariant();
        lock (_storeLock)
        {
            switch (action)
            {
                case "clearpassword":
                {
                    if (parts.Length < 3)
                        return TextCommandResult.Error("Usage: /serverauth admin clearpassword <player>", "");

                    var record = FindPlayer(parts[2]);
                    if (record is null)
                        return TextCommandResult.Error("Player not found.", "");

                    record.PasswordHash = string.Empty;
                    record.PasswordResetRequired = true;
                    RemoveSessionsForPlayer(record.PlayerUid);
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Password cleared.", null);
                }
                case "setpassword":
                {
                    if (parts.Length < 4)
                        return TextCommandResult.Error("Usage: /serverauth admin setpassword <player> <password>", "");

                    if (!ValidatePasswordLength(parts[3], out var error))
                        return TextCommandResult.Error(error, "");

                    var record = FindPlayer(parts[2]);
                    if (record is null)
                        return TextCommandResult.Error("Player not found.", "");

                    record.PasswordHash = PasswordHasher.Hash(parts[3]);
                    record.PasswordResetRequired = false;
                    RemoveSessionsForPlayer(record.PlayerUid);
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Password updated.", null);
                }
                case "clearsessions":
                    _store.Sessions.Clear();
                    SaveStoreUnsafe();
                    return TextCommandResult.Success("Remembered sessions cleared.", null);
                default:
                    return TextCommandResult.Error("Unknown ServerAuth admin command.", "");
            }
        }
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        if (!_settings.Enabled)
        {
            if (HasDeferredCharacterSelection(player))
            {
                player.RemoveModdata(DeferredCharacterSelectionModDataKey);
                player.RemoveModdata("createCharacter");
            }

            return;
        }

        if (HasCompletedCharacterSelection(player) || HasDeferredCharacterSelection(player))
            return;

        player.SetModdata("createCharacter", SerializerUtil.Serialize(true));
        player.SetModdata(DeferredCharacterSelectionModDataKey, SerializerUtil.Serialize(true));
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        if (!_settings.Enabled)
            return;

        var now = DateTimeOffset.UtcNow;
        lock (_storeLock)
        {
            if (IsNicknameTakenByOtherUid(player))
            {
                player.Disconnect("该昵称已经被其他 UUID 注册，请联系管理员。");
                return;
            }

            if (HasRememberedSession(player, now))
            {
                var record = FindPlayer(player.PlayerUID);
                if (record is not null)
                {
                    record.LastIp = player.IpAddress ?? string.Empty;
                    record.LastLoginAtUtc = now;
                    SaveStoreUnsafe();
                }

                Authenticate(player, "已使用同 IP 记住会话自动认证。");
                return;
            }
        }

        BeginPending(player, now);

        if (_settings.Discourse.Enabled)
        {
            SendDiscourseChallenge(player, now);
            return;
        }

        var prompt = HasRegisteredPassword(player)
            ? "请在聊天栏输入 /login <密码> 完成登录。"
            : "请在聊天栏输入 /register <密码> <再确认密码> 完成注册。";
        player.SendMessage(GlobalConstants.GeneralChatGroup, prompt, SystemChatType, null);
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        lock (_authLock)
        {
            _pendingByUid.Remove(player.PlayerUID);
        }
    }

    private void OnPlayerChat(
        IServerPlayer byPlayer,
        int channelId,
        ref string message,
        ref string data,
        BoolRef consumed)
    {
        if (!_settings.Enabled || IsAuthenticated(byPlayer))
            return;

        if (message.StartsWith("/login", StringComparison.OrdinalIgnoreCase) ||
            message.StartsWith("/register", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        consumed.value = true;
        byPlayer.SendMessage(GlobalConstants.GeneralChatGroup, "请先完成服务器认证。", SystemChatType, null);
    }

    private bool OnCanPlaceOrBreakBlock(IServerPlayer byPlayer, BlockSelection blockSel, out string claimant)
    {
        claimant = "custommessage-serverauth";
        return !_settings.Enabled || IsAuthenticated(byPlayer);
    }

    private bool OnCanUseBlock(IServerPlayer byPlayer, BlockSelection blockSel)
    {
        if (!_settings.Enabled || IsAuthenticated(byPlayer))
            return true;

        byPlayer.SendIngameError("serverauth", "请先完成服务器认证。");
        return false;
    }

    private void OnHandInteract(
        IServerPlayer player,
        EnumHandInteractNw handInteract,
        float secondsPassed,
        ref EnumHandling handling)
    {
        if (_settings.Enabled && !IsAuthenticated(player))
            handling = EnumHandling.PreventDefault;
    }

    private void OnPlayerInteractEntity(
        Entity entity,
        IPlayer byPlayer,
        ItemSlot slot,
        Vec3d hitPosition,
        int mode,
        ref EnumHandling handling)
    {
        if (!_settings.Enabled || byPlayer is not IServerPlayer serverPlayer || IsAuthenticated(serverPlayer))
            return;

        handling = EnumHandling.PreventDefault;
    }

    private void OnAuthTick(float dt)
    {
        if (_api is null || !_settings.Enabled)
            return;

        var expired = new List<string>();
        var now = DateTimeOffset.UtcNow;

        lock (_authLock)
        {
            foreach (var state in _pendingByUid.Values)
            {
                var player = _api.World.PlayerByUid(state.PlayerUid) as IServerPlayer;
                if (player is null)
                    continue;

                if (now >= state.DeadlineUtc)
                {
                    expired.Add(state.PlayerUid);
                    player.Disconnect("认证超时，请重新进入服务器。");
                    continue;
                }

                RestrictPlayer(player, state);
            }

            foreach (var uid in expired)
                _pendingByUid.Remove(uid);

            var expiredNonce = _discourseByNonce
                .Where(pair => pair.Value.ExpiresAtUtc <= now)
                .Select(pair => pair.Key)
                .ToList();
            foreach (var nonce in expiredNonce)
                _discourseByNonce.Remove(nonce);
        }
    }

    private void BeginPending(IServerPlayer player, DateTimeOffset now)
    {
        lock (_authLock)
        {
            var originalMoveSpeed = player.WorldData.MoveSpeedMultiplier <= 0f
                ? 1f
                : player.WorldData.MoveSpeedMultiplier;

            var shouldDeferCharacterSelection = HasDeferredCharacterSelection(player);
            _pendingByUid[player.PlayerUID] = new PendingAuthState
            {
                PlayerUid = player.PlayerUID,
                DeadlineUtc = now.AddSeconds(_settings.LoginTimeoutSeconds),
                OriginalMoveSpeed = originalMoveSpeed,
                DeferredCharacterSelection = shouldDeferCharacterSelection
            };
            RestrictPlayer(player, _pendingByUid[player.PlayerUID]);
        }
    }

    private static void RestrictPlayer(IServerPlayer player, PendingAuthState state)
    {
        player.WorldData.MoveSpeedMultiplier = 0f;
        if (player.Entity?.Controls is not null)
            player.Entity.Controls.MovespeedMultiplier = 0f;
        player.BroadcastPlayerData(false);
    }

    private string Authenticate(
        IServerPlayer player,
        string message,
        bool sendChatMessage = true,
        bool appendCharacterSelectionHint = true)
    {
        PendingAuthState? pending = null;
        lock (_authLock)
        {
            if (_pendingByUid.Remove(player.PlayerUID, out var state))
                pending = state;
        }

        if (pending is not null)
        {
            player.WorldData.MoveSpeedMultiplier = pending.OriginalMoveSpeed <= 0f ? 1f : pending.OriginalMoveSpeed;
            if (player.Entity?.Controls is not null)
                player.Entity.Controls.MovespeedMultiplier = player.WorldData.MoveSpeedMultiplier;
            player.BroadcastPlayerData(false);
        }

        var openedCharacterSelection = false;
        if (pending?.DeferredCharacterSelection == true || HasDeferredCharacterSelection(player))
            openedCharacterSelection = ReleaseDeferredCharacterSelection(player);

        var finalMessage = ComposeAuthMessage(message, openedCharacterSelection, appendCharacterSelectionHint);
        if (sendChatMessage)
            player.SendMessage(GlobalConstants.GeneralChatGroup, finalMessage, SystemChatType, null);
        _channel?.SendPacket(new AuthStatePacket
        {
            IsAuthenticated = true,
            Message = finalMessage,
            OpenCharacterSelection = openedCharacterSelection
        }, player);

        return finalMessage;
    }

    private static string ComposeAuthMessage(
        string message,
        bool openedCharacterSelection,
        bool appendCharacterSelectionHint = true)
    {
        if (!openedCharacterSelection || !appendCharacterSelectionHint)
            return message;

        return message + " 现在可以选择职业了，请完成角色创建。";
    }

    private static bool HasDeferredCharacterSelection(IServerPlayer player)
    {
        return SerializerUtil.Deserialize<bool>(player.GetModdata(DeferredCharacterSelectionModDataKey), false);
    }

    private static bool HasCompletedCharacterSelection(IServerPlayer player)
    {
        return SerializerUtil.Deserialize<bool>(player.GetModdata("createCharacter"), false);
    }

    private bool ReleaseDeferredCharacterSelection(IServerPlayer player)
    {
        var deferred = SerializerUtil.Deserialize<bool>(player.GetModdata(DeferredCharacterSelectionModDataKey), false);
        player.RemoveModdata(DeferredCharacterSelectionModDataKey);
        if (!deferred || _api is null)
            return false;

        player.RemoveModdata("createCharacter");

        var channel = _api.Network.GetChannel(CharacterSelectionChannelName);
        channel.SendPacket(new CharacterSelectedState
        {
            DidSelect = false
        }, player);

        return true;
    }

    private bool IsAuthenticated(IServerPlayer player)
    {
        lock (_authLock)
        {
            return !_pendingByUid.ContainsKey(player.PlayerUID);
        }
    }

    private void SendDiscourseChallenge(IServerPlayer player, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(_settings.Discourse.BaseUrl) ||
            string.IsNullOrWhiteSpace(_settings.Discourse.SharedSecret) ||
            string.IsNullOrWhiteSpace(_settings.Discourse.PublicCallbackBaseUrl))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, "服务器社区认证配置不完整，请联系管理员。", SystemChatType, null);
            return;
        }

        var nonce = GenerateNonce();
        var callbackUrl = BuildCallbackUrl(_settings.Discourse.PublicCallbackBaseUrl);
        var authUrl = DiscourseConnect.BuildProviderUrl(
            _settings.Discourse.BaseUrl,
            _settings.Discourse.SharedSecret,
            callbackUrl,
            nonce);

        lock (_authLock)
        {
            _discourseByNonce[nonce] = new DiscourseChallengeState
            {
                Nonce = nonce,
                PlayerUid = player.PlayerUID,
                ExpiresAtUtc = now.AddMinutes(DiscourseChallengeMinutes)
            };
        }

        var message = "已打开社区认证页面，请在浏览器中完成登录。";
        player.SendMessage(GlobalConstants.GeneralChatGroup, message, SystemChatType, null);
        _channel?.SendPacket(new AuthChallengePacket
        {
            ChallengeId = nonce,
            AuthUrl = authUrl,
            Mode = "discourse",
            Message = message
        }, player);
    }

    private void StartDiscourseListener()
    {
        if (string.IsNullOrWhiteSpace(_settings.Discourse.ListenPrefix))
            return;

        StopDiscourseListener();

        try
        {
            _listenerCts = new CancellationTokenSource();
            _listener = new HttpListener();
            _listener.Prefixes.Add(NormalizeListenPrefix(_settings.Discourse.ListenPrefix));
            _listener.Start();
            _listenerTask = Task.Run(() => ListenLoopAsync(_listener, _listenerCts.Token), CancellationToken.None);

            _api?.Logger.Notification(
                "{0} Discourse callback listener started on {1}",
                VsslAuthModSystem.LogPrefix,
                _settings.Discourse.ListenPrefix);
        }
        catch (Exception ex)
        {
            _api?.Logger.Error(
                "{0} Failed to start Discourse callback listener: {1}",
                VsslAuthModSystem.LogPrefix,
                ex.Message);
        }
    }

    private void StopDiscourseListener()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _listener?.Close();
        }
        catch
        {
            // ignore
        }

        _listener = null;
        _listenerTask = null;
        _listenerCts?.Dispose();
        _listenerCts = null;
    }

    private async Task ListenLoopAsync(HttpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && listener.IsListening)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                    break;
            }

            if (context is null)
                continue;

            _ = Task.Run(() => HandleHttpCallbackAsync(context), CancellationToken.None);
        }
    }

    private async Task HandleHttpCallbackAsync(HttpListenerContext context)
    {
        try
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (!path.TrimEnd('/').EndsWith("/serverauth/discourse/callback", StringComparison.OrdinalIgnoreCase))
            {
                await WriteHttpResponseAsync(context, 404, "ServerAuth callback endpoint not found.");
                return;
            }

            var sso = context.Request.QueryString["sso"] ?? string.Empty;
            var sig = context.Request.QueryString["sig"] ?? string.Empty;
            if (!DiscourseConnect.VerifySignature(sso, sig, _settings.Discourse.SharedSecret))
            {
                await WriteHttpResponseAsync(context, 403, "Invalid Discourse signature.");
                return;
            }

            var payload = DiscourseConnect.DecodePayload(sso);
            if (!payload.TryGetValue("nonce", out var nonce) || string.IsNullOrWhiteSpace(nonce))
            {
                await WriteHttpResponseAsync(context, 400, "Missing nonce.");
                return;
            }

            DiscourseChallengeState? challenge;
            lock (_authLock)
            {
                _discourseByNonce.Remove(nonce, out challenge);
            }

            if (challenge is null || DateTimeOffset.UtcNow > challenge.ExpiresAtUtc)
            {
                await WriteHttpResponseAsync(context, 400, "Challenge expired. Please rejoin the server.");
                return;
            }

            if (_api is not null)
            {
                _api.Event.EnqueueMainThreadTask(
                    () => CompleteDiscourseAuth(challenge, payload),
                    "serverauth-discourse-complete");
            }

            await WriteAuthSuccessResponseAsync(context, "认证成功，已完成认证，请返回游戏。");
        }
        catch (Exception ex)
        {
            await WriteHttpResponseAsync(context, 500, "ServerAuth callback failed: " + ex.Message);
        }
    }

    private void CompleteDiscourseAuth(DiscourseChallengeState challenge, Dictionary<string, string> payload)
    {
        if (_api is null)
            return;

        var player = _api.World.PlayerByUid(challenge.PlayerUid) as IServerPlayer;
        if (player is null)
            return;

        var externalId = payload.TryGetValue("external_id", out var rawExternalId) ? rawExternalId : string.Empty;
        var discourseUsername = payload.TryGetValue("username", out var rawUserName) ? rawUserName : string.Empty;
        var discourseEmail = payload.TryGetValue("email", out var rawEmail) ? rawEmail : string.Empty;

        if (string.IsNullOrWhiteSpace(externalId))
        {
            player.SendMessage(GlobalConstants.GeneralChatGroup, "社区认证缺少 external_id，请联系管理员。", SystemChatType, null);
            return;
        }

        lock (_storeLock)
        {
            var conflictByExternalId = _store.Players.FirstOrDefault(record =>
                record.DiscourseExternalId.Equals(externalId, StringComparison.OrdinalIgnoreCase) &&
                !record.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase));
            if (conflictByExternalId is not null)
            {
                player.Disconnect("该社区账号已经绑定其他玩家。");
                return;
            }

            if (IsNicknameTakenByOtherUid(player))
            {
                player.Disconnect("该昵称已经被其他 UUID 注册，请联系管理员。");
                return;
            }

            var now = DateTimeOffset.UtcNow;
            var record = FindPlayer(player.PlayerUID) ?? new ServerAuthPlayerRecord
            {
                PlayerUid = player.PlayerUID,
                RegisteredAtUtc = now,
                RegisteredIp = player.IpAddress ?? string.Empty
            };
            if (!_store.Players.Contains(record))
                _store.Players.Add(record);

            record.PlayerName = player.PlayerName;
            record.NormalizedPlayerName = NormalizePlayerName(player.PlayerName);
            record.LastIp = player.IpAddress ?? string.Empty;
            record.LastLoginAtUtc = now;
            record.DiscourseExternalId = externalId;
            record.DiscourseUsername = discourseUsername;
            record.DiscourseEmail = discourseEmail;
            RememberSession(record.PlayerUid, record.LastIp, now);
            SaveStoreUnsafe();
        }

        Authenticate(player, "社区认证成功，已通过服务器认证。");
    }

    private ServerAuthSettings LoadSettings()
    {
        var path = Path.Combine(GamePaths.ModConfig, SettingsFileName);
        if (!File.Exists(path))
        {
            var defaults = ServerAuthSettings.Default();
            SaveSettings(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return ServerAuthSettings.Normalize(JsonSerializer.Deserialize<ServerAuthSettings>(json, JsonOptions));
        }
        catch
        {
            return ServerAuthSettings.Default();
        }
    }

    private static void SaveSettings(ServerAuthSettings settings)
    {
        Directory.CreateDirectory(GamePaths.ModConfig);
        var path = Path.Combine(GamePaths.ModConfig, SettingsFileName);
        var normalized = ServerAuthSettings.Normalize(settings);
        File.WriteAllText(path, JsonSerializer.Serialize(normalized, JsonOptions));
    }

    private PlayerStore LoadStore()
    {
        var path = GetStorePath();
        if (!File.Exists(path))
            return new PlayerStore();

        try
        {
            return JsonSerializer.Deserialize<PlayerStore>(File.ReadAllText(path), JsonOptions) ?? new PlayerStore();
        }
        catch
        {
            return new PlayerStore();
        }
    }

    private void SaveStoreUnsafe()
    {
        var path = GetStorePath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(_store, JsonOptions), Encoding.UTF8);
        File.Move(temp, path, true);
    }

    private static string GetStorePath()
    {
        return Path.Combine(GamePaths.DataPath, StoreRelativePath);
    }

    private ServerAuthPlayerRecord? FindPlayer(string playerNameOrUid)
    {
        var normalized = NormalizePlayerName(playerNameOrUid);
        return _store.Players.FirstOrDefault(player =>
            player.PlayerUid.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.PlayerName.Equals(playerNameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private bool HasRegisteredPassword(IServerPlayer player)
    {
        lock (_storeLock)
        {
            var record = FindPlayer(player.PlayerUID);
            return record is not null &&
                   !record.PasswordResetRequired &&
                   !string.IsNullOrWhiteSpace(record.PasswordHash);
        }
    }

    private bool HasRememberedSession(IServerPlayer player, DateTimeOffset now)
    {
        var ip = player.IpAddress ?? string.Empty;
        return _store.Sessions.Any(session =>
            session.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase) &&
            session.IpAddress.Equals(ip, StringComparison.OrdinalIgnoreCase) &&
            session.ExpiresAtUtc > now);
    }

    private void RememberSession(string playerUid, string ipAddress, DateTimeOffset now)
    {
        _store.Sessions = _store.Sessions
            .Where(session => session.ExpiresAtUtc > now)
            .Where(session =>
                !session.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase) ||
                !session.IpAddress.Equals(ipAddress, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (_settings.RememberSessionMinutes <= 0 || string.IsNullOrWhiteSpace(ipAddress))
            return;

        _store.Sessions.Add(new ServerAuthSessionRecord
        {
            PlayerUid = playerUid,
            IpAddress = ipAddress,
            CreatedAtUtc = now,
            ExpiresAtUtc = now.AddMinutes(_settings.RememberSessionMinutes)
        });
    }

    private bool IsNicknameTakenByOtherUid(IServerPlayer player)
    {
        var normalized = NormalizePlayerName(player.PlayerName);
        return _store.Players.Any(record =>
            record.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase) &&
            !record.PlayerUid.Equals(player.PlayerUID, StringComparison.OrdinalIgnoreCase));
    }

    private void RemoveSessionsForPlayer(string playerUid)
    {
        _store.Sessions = _store.Sessions
            .Where(session => !session.PlayerUid.Equals(playerUid, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static bool ValidatePasswordLength(string password, out string error)
    {
        error = string.Empty;
        if (password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
        {
            error = $"密码长度必须在 {MinPasswordLength} 到 {MaxPasswordLength} 个字符之间。";
            return false;
        }

        return true;
    }

    private static string NormalizePlayerName(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private static string GenerateNonce()
    {
        Span<byte> bytes = stackalloc byte[24];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static string BuildCallbackUrl(string publicCallbackBaseUrl)
    {
        var baseUrl = publicCallbackBaseUrl.Trim();
        if (baseUrl.Contains("/serverauth/discourse/callback", StringComparison.OrdinalIgnoreCase))
            return baseUrl;

        return baseUrl.TrimEnd('/') + "/serverauth/discourse/callback";
    }

    private static string NormalizeListenPrefix(string value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? "http://127.0.0.1:18092/" : value.Trim();
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static async Task WriteHttpResponseAsync(HttpListenerContext context, int statusCode, string message)
    {
        var html = "<!doctype html><html><head><meta charset=\"utf-8\"><title>ServerAuth</title></head><body>"
                   + WebUtility.HtmlEncode(message)
                   + "</body></html>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private static async Task WriteAuthSuccessResponseAsync(
        HttpListenerContext context,
        string message)
    {
        var html =
            "<!doctype html><html><head><meta charset=\"utf-8\"><title>ServerAuth</title></head><body>" +
            "<h3>" + WebUtility.HtmlEncode(message) + "</h3>" +
            "<p style=\"color:#666;\">认证已完成，请返回游戏客户端。</p>" +
            "</body></html>";

        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed class PendingAuthState
    {
        public string PlayerUid { get; init; } = string.Empty;
        public DateTimeOffset DeadlineUtc { get; init; }
        public float OriginalMoveSpeed { get; init; } = 1f;
        public bool DeferredCharacterSelection { get; init; }
    }

    private sealed class DiscourseChallengeState
    {
        public string Nonce { get; init; } = string.Empty;
        public string PlayerUid { get; init; } = string.Empty;
        public DateTimeOffset ExpiresAtUtc { get; init; }
    }

    private sealed class PlayerStore
    {
        public List<ServerAuthPlayerRecord> Players { get; set; } = [];
        public List<ServerAuthSessionRecord> Sessions { get; set; } = [];
    }

    private sealed class ServerAuthSettings
    {
        public bool Enabled { get; set; }
        public int LoginTimeoutSeconds { get; set; } = 60;
        public int RememberSessionMinutes { get; set; } = 30;
        public ServerAuthDiscourseSettings Discourse { get; set; } = new();

        public static ServerAuthSettings Default()
        {
            return new ServerAuthSettings
            {
                Enabled = false,
                LoginTimeoutSeconds = 60,
                RememberSessionMinutes = 30,
                Discourse = new ServerAuthDiscourseSettings()
            };
        }

        public static ServerAuthSettings Normalize(ServerAuthSettings? settings)
        {
            var normalized = settings ?? Default();
            normalized.LoginTimeoutSeconds = Math.Clamp(normalized.LoginTimeoutSeconds, 10, 600);
            normalized.RememberSessionMinutes = Math.Clamp(normalized.RememberSessionMinutes, 0, 1440);
            normalized.Discourse ??= new ServerAuthDiscourseSettings();
            normalized.Discourse.BaseUrl = NormalizeUrl(normalized.Discourse.BaseUrl);
            normalized.Discourse.SharedSecret = normalized.Discourse.SharedSecret?.Trim() ?? string.Empty;
            normalized.Discourse.PublicCallbackBaseUrl = NormalizeUrl(
                normalized.Discourse.PublicCallbackBaseUrl,
                "http://127.0.0.1:18092/");
            normalized.Discourse.ListenPrefix = NormalizeUrl(
                normalized.Discourse.ListenPrefix,
                "http://127.0.0.1:18092/");
            return normalized;
        }

        private static string NormalizeUrl(string? value, string fallback = "")
        {
            var candidate = value?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(candidate))
                return fallback;

            return candidate.EndsWith('/') ? candidate : candidate + "/";
        }
    }

    private sealed class ServerAuthDiscourseSettings
    {
        public bool Enabled { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string SharedSecret { get; set; } = string.Empty;
        public string PublicCallbackBaseUrl { get; set; } = "http://127.0.0.1:18092/";
        public string ListenPrefix { get; set; } = "http://127.0.0.1:18092/";
    }

    private sealed class ServerAuthPlayerRecord
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

    private sealed class ServerAuthSessionRecord
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }

    private static class PasswordHasher
    {
        private const string Prefix = "pbkdf2-sha256";
        private const int Iterations = 120000;

        public static string Hash(string password)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = Rfc2898DeriveBytes.Pbkdf2(
                password,
                salt,
                Iterations,
                HashAlgorithmName.SHA256,
                32);
            return string.Join(
                '$',
                Prefix,
                Iterations.ToString(),
                Convert.ToBase64String(salt),
                Convert.ToBase64String(derived));
        }

        public static bool Verify(string password, string encodedHash)
        {
            var parts = encodedHash.Split('$');
            if (parts.Length != 4 || !parts[0].Equals(Prefix, StringComparison.Ordinal))
                return false;
            if (!int.TryParse(parts[1], out var iterations))
                return false;

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

    private static class DiscourseConnect
    {
        public static string BuildProviderUrl(string baseUrl, string secret, string callbackUrl, string nonce)
        {
            var rawPayload = "nonce=" + Uri.EscapeDataString(nonce) + "&return_sso_url=" + Uri.EscapeDataString(callbackUrl);
            var sso = Convert.ToBase64String(Encoding.UTF8.GetBytes(rawPayload));
            var sig = ComputeSignature(sso, secret);
            return $"{baseUrl.TrimEnd('/')}/session/sso_provider?sso={Uri.EscapeDataString(sso)}&sig={sig}";
        }

        public static bool VerifySignature(string sso, string sig, string secret)
        {
            if (string.IsNullOrWhiteSpace(sso) || string.IsNullOrWhiteSpace(sig) || string.IsNullOrWhiteSpace(secret))
                return false;

            try
            {
                var expected = Encoding.ASCII.GetBytes(ComputeSignature(sso, secret));
                var given = Encoding.ASCII.GetBytes(sig.Trim());
                return given.Length == expected.Length && CryptographicOperations.FixedTimeEquals(given, expected);
            }
            catch
            {
                return false;
            }
        }

        public static Dictionary<string, string> DecodePayload(string sso)
        {
            var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(sso));
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var segment in decoded.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var index = segment.IndexOf('=');
                var key = index < 0 ? segment : segment[..index];
                var value = index < 0 ? string.Empty : segment[(index + 1)..];
                result[Uri.UnescapeDataString(key.Replace('+', ' '))] =
                    Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return result;
        }

        private static string ComputeSignature(string value, string secret)
        {
            using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
            return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        }
    }
}
