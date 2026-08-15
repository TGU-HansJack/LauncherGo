using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LauncherGoRedirect;

/// <summary>
///     通过一次性 Gateway 凭证将玩家定向到 ServerId，不向客户端公开后端地址。
/// </summary>
public sealed class LauncherGoRedirectModSystem : ModSystem
{
    private const string ChannelName = "launchergoredirect";
    private const string ConfigFileName = "launchergoredirect.json";
    private const string CommandName = "launchergateway";
    private const string Privilege = "controlserver";
    private static readonly object GatewayEndpointGate = new();
    private static GatewayEndpoint? _originalGatewayEndpoint;
    private static int? _activeRelayPort;

    private ICoreServerAPI? _serverApi;
    private IServerNetworkChannel? _serverChannel;
    private GatewayRedirectConfiguration _configuration = new();
    private ICoreClientAPI? _clientApi;
    private bool _redirectInProgress;

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        _configuration = LoadConfiguration(api);
        _serverChannel = api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<GatewayRedirectExecutePacket>();

        var parsers = api.ChatCommands.Parsers;
        api.ChatCommands.Create(CommandName)
            .WithDescription("LauncherGo gateway redirect controls")
            .RequiresPrivilege(Privilege)
            .BeginSubCommand("redirect")
                .WithDescription("Redirect one player to a gateway ServerId")
                .WithArgs(parsers.Word("player"), parsers.Word("serverid"))
                .HandleWith(RedirectPlayer)
            .EndSubCommand()
            .BeginSubCommand("evacuate")
                .WithDescription("Redirect all online players to a gateway ServerId")
                .WithArgs(parsers.Word("serverid"))
                .HandleWith(EvacuatePlayers);
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        _clientApi = api;
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<GatewayRedirectExecutePacket>()
            .SetMessageHandler<GatewayRedirectExecutePacket>(OnRedirectPacket);
    }

    public override void Dispose()
    {
        _serverApi = null;
        _serverChannel = null;
        _clientApi = null;
        base.Dispose();
    }

    private TextCommandResult RedirectPlayer(TextCommandCallingArgs args)
    {
        RefreshConfiguration();
        var playerToken = ((string)args[0]).Trim();
        var targetServerId = ((string)args[1]).Trim();
        var player = _serverApi?.World.AllOnlinePlayers.OfType<IServerPlayer>()
            .FirstOrDefault(item => item.PlayerName.Equals(playerToken, StringComparison.OrdinalIgnoreCase) ||
                                    item.PlayerUID.Equals(playerToken, StringComparison.OrdinalIgnoreCase));
        if (player is null)
        {
            return TextCommandResult.Error("Player is not online.");
        }

        return TryRedirect(player, targetServerId, out var error)
            ? TextCommandResult.Success($"Redirect requested for {player.PlayerName}.")
            : TextCommandResult.Error(error);
    }

    private TextCommandResult EvacuatePlayers(TextCommandCallingArgs args)
    {
        RefreshConfiguration();
        var targetServerId = ((string)args[0]).Trim();
        var players = _serverApi?.World.AllOnlinePlayers.OfType<IServerPlayer>().ToArray() ?? [];
        var redirected = 0;
        foreach (var player in players)
        {
            if (TryRedirect(player, targetServerId, out _)) redirected++;
        }

        return redirected > 0
            ? TextCommandResult.Success($"Redirect requested for {redirected} player(s).")
            : TextCommandResult.Error("No player could be redirected. Check the target ServerId and gateway route configuration.");
    }

    private bool TryRedirect(IServerPlayer player, string targetServerId, out string error)
    {
        if (_serverChannel is null)
        {
            error = "Redirect channel is not ready.";
            return false;
        }

        var route = _configuration.Routes.FirstOrDefault(item =>
            item.ServerId.Equals(targetServerId, StringComparison.OrdinalIgnoreCase));
        if (route is null)
        {
            error = "Target ServerId is not configured for this gateway.";
            return false;
        }

        if (targetServerId.Equals(_configuration.ServerId, StringComparison.OrdinalIgnoreCase))
        {
            error = "Target ServerId is the current server.";
            return false;
        }

        if (string.IsNullOrWhiteSpace(_configuration.TicketSecret))
        {
            error = "Gateway transfer credential is unavailable.";
            return false;
        }

        try
        {
            _serverChannel.SendPacket(new GatewayRedirectExecutePacket
            {
                ServerId = route.ServerId,
                TransferTicket = GatewayTransferTicketIssuer.Create(
                    _configuration.TicketSecret,
                    _configuration.ServerId,
                    route.ServerId,
                    player.PlayerUID),
                Name = string.IsNullOrWhiteSpace(route.Name) ? route.ServerId : route.Name
            }, player);
            error = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void OnRedirectPacket(GatewayRedirectExecutePacket packet)
    {
        if (_redirectInProgress || string.IsNullOrWhiteSpace(packet.TransferTicket)) return;
        _redirectInProgress = true;
        try
        {
            _clientApi?.Event.EnqueueMainThreadTask(
                () => SwitchClientToGateway(packet),
                "launchergoredirect-switch");
        }
        catch
        {
            _redirectInProgress = false;
        }
    }

    private void SwitchClientToGateway(GatewayRedirectExecutePacket packet)
    {
        try
        {
            var clientMain = _clientApi?.World ?? throw new InvalidOperationException("Client session is unavailable.");
            var clientType = clientMain.GetType();
            var endpoint = ResolveGatewayEndpoint(clientMain, clientType);
            var relay = GatewayTransferRelay.Start(endpoint.Host, endpoint.Port, packet.TransferTicket.Trim());
            SetActiveRelayPort(relay.LocalPort);
            var sendLeave = clientType.GetMethod("SendLeave", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var destroySession = clientType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                .FirstOrDefault(method => method.Name == "DestroyGameSession" &&
                                          method.GetParameters().Length is >= 1 and <= 2 &&
                                          method.GetParameters()[0].ParameterType == typeof(bool));
            var redirectField = clientType.GetField("RedirectTo", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var exitReasonField = clientType.GetField("exitReason", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            var exitToDisconnectScreenField = clientType.GetField("exitToDisconnectScreen", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (sendLeave is null || destroySession is null || redirectField is null)
            {
                relay.Dispose();
                throw new InvalidOperationException("The installed Vintage Story client does not expose redirect APIs.");
            }

            try
            {
                sendLeave.Invoke(clientMain, [0]);
            }
            catch
            {
                // A closing server channel can reject SendLeave; local teardown can still proceed.
            }

            exitReasonField?.SetValue(clientMain, "LauncherGo gateway redirect");
            exitToDisconnectScreenField?.SetValue(clientMain, false);
            var parameters = destroySession.GetParameters();
            destroySession.Invoke(clientMain, parameters.Length == 1
                ? [false]
                : [false, GetSoftExitValue(parameters[1].ParameterType)]);
            redirectField.SetValue(clientMain, new MultiplayerServerEntry
            {
                host = relay.LocalEndpoint,
                name = string.IsNullOrWhiteSpace(packet.Name) ? packet.ServerId : packet.Name.Trim()
            });
        }
        catch (Exception ex)
        {
            _clientApi?.ShowChatMessage("Gateway redirect failed: " + ex.GetBaseException().Message);
        }
        finally
        {
            _redirectInProgress = false;
        }
    }

    private static GatewayEndpoint ResolveGatewayEndpoint(object clientMain, Type clientType)
    {
        var currentEndpoint = GetGatewayEndpoint(clientMain, clientType);
        lock (GatewayEndpointGate)
        {
            if (_activeRelayPort == currentEndpoint.Port &&
                currentEndpoint.Host.Equals("127.0.0.1", StringComparison.OrdinalIgnoreCase))
            {
                return _originalGatewayEndpoint ?? throw new InvalidOperationException(
                    "The original Gateway address is unavailable for this redirect.");
            }

            // A direct connection starts a new redirect chain and replaces stale state.
            _originalGatewayEndpoint = currentEndpoint;
            _activeRelayPort = null;
            return currentEndpoint;
        }
    }

    private static void SetActiveRelayPort(int relayPort)
    {
        lock (GatewayEndpointGate)
        {
            _activeRelayPort = relayPort;
        }
    }

    private static GatewayEndpoint GetGatewayEndpoint(object clientMain, Type clientType)
    {
        var connectData = clientType.GetField("Connectdata", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                              ?.GetValue(clientMain)
                          ?? throw new InvalidOperationException("The current Gateway connection is unavailable.");
        var connectDataType = connectData.GetType();
        var host = connectDataType.GetField("Host", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(connectData) as string;
        var portValue = connectDataType.GetField("Port", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(connectData);
        if (string.IsNullOrWhiteSpace(host) || portValue is null ||
            !int.TryParse(portValue.ToString(), out var port) || port is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("The original Gateway address is unavailable.");
        }

        return new GatewayEndpoint(host, port);
    }

    private static object GetSoftExitValue(Type exitModeType) => exitModeType.IsEnum
        ? Enum.Parse(exitModeType, "SoftExit")
        : exitModeType.IsValueType ? Activator.CreateInstance(exitModeType)! : null!;

    private static GatewayRedirectConfiguration LoadConfiguration(ICoreServerAPI api)
    {
        try
        {
            return api.LoadModConfig<GatewayRedirectConfiguration>(ConfigFileName) ?? new GatewayRedirectConfiguration();
        }
        catch (Exception ex)
        {
            api.Logger.Warning("LauncherGo redirect could not load {0}: {1}", ConfigFileName, ex.Message);
            return new GatewayRedirectConfiguration();
        }
    }

    private void RefreshConfiguration()
    {
        if (_serverApi is not null)
        {
            _configuration = LoadConfiguration(_serverApi);
        }
    }

    private sealed class GatewayRedirectConfiguration
    {
        public string ServerId { get; set; } = string.Empty;

        public string TicketSecret { get; set; } = string.Empty;

        public List<GatewayRedirectRoute> Routes { get; set; } = [];
    }

    private sealed class GatewayRedirectRoute
    {
        public string ServerId { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;
    }

    private sealed record GatewayEndpoint(string Host, int Port);
}
