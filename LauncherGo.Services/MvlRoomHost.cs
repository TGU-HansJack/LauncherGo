using System.Text.Json.Serialization;
using AsyncIO;
using Nerdbank.MessagePack;
using NetMQ;
using NetMQ.Sockets;
using PolyType;

namespace LauncherGo.Services;

internal sealed class MvlRoomHost : IDisposable
{
    private static readonly MessagePackSerializer PackSerializer = new();
    private readonly object _sync = new();
    private readonly ushort _controlPort;
    private readonly List<MvlRoomPlayerInfo> _players = [];
    private readonly Action<string, Exception?> _logError;
    private RouterSocket? _routerSocket;
    private NetMQPoller? _poller;
    private NetMQTimer? _heartbeatTimer;
    private bool _disposed;

    public MvlRoomHost(
        ushort controlPort,
        MvlRoomPlayerInfo hostPlayer,
        Action<string, Exception?> logError)
    {
        _controlPort = controlPort;
        _logError = logError;
        _players.Add(hostPlayer);
    }

    public event EventHandler<int>? GuestCountChanged;

    public void Start()
    {
        ThrowIfDisposed();
        ForceDotNet.Force();

        _routerSocket = new RouterSocket($"tcp://*:{_controlPort}");
        _routerSocket.ReceiveReady += OnReceiveReady;

        _heartbeatTimer = new NetMQTimer(TimeSpan.FromSeconds(6));
        _heartbeatTimer.Elapsed += OnHeartbeatTimerElapsed;
        _heartbeatTimer.Enable = true;

        _poller = new NetMQPoller { _routerSocket, _heartbeatTimer };
        _poller.RunAsync("LauncherGo-EasyTier-Room", true);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            NotifyHostShutdown();
        }
        catch (Exception ex)
        {
            _logError("Failed to notify EasyTier room guests about shutdown.", ex);
        }

        try
        {
            _heartbeatTimer?.Enable = false;
            _heartbeatTimer = null;
            _poller?.Stop();
            _poller?.Dispose();
            _poller = null;
            _routerSocket?.Close();
            _routerSocket?.Dispose();
            _routerSocket = null;
        }
        catch (Exception ex)
        {
            _logError("Failed to stop EasyTier room host.", ex);
        }
        finally
        {
            lock (_sync)
            {
                _players.Clear();
            }

            _disposed = true;
        }
    }

    private void OnReceiveReady(object? sender, NetMQSocketEventArgs eventArgs)
    {
        try
        {
            NetMQMessage? message = null;
            if (!eventArgs.Socket.TryReceiveMultipartMessage(ref message) || message is null || message.FrameCount < 2)
            {
                return;
            }

            var identity = message[0].Buffer;
            if (identity.Length < sizeof(uint))
            {
                return;
            }

            var clientIdentity = BitConverter.ToUInt32(identity, 0);
            var eventCode = (MvlRoomEvent)BitConverter.ToInt32(message[1].Buffer, 0);
            switch (eventCode)
            {
                case MvlRoomEvent.GuestJoined:
                    if (message.FrameCount >= 3)
                    {
                        HandleGuestJoined(identity, clientIdentity, message[2].Buffer);
                    }

                    break;
                case MvlRoomEvent.GuestLeft:
                    HandleGuestLeft(clientIdentity);
                    break;
                case MvlRoomEvent.Ping:
                    Send(identity, MvlRoomEvent.Pong);
                    TouchGuest(clientIdentity);
                    break;
            }
        }
        catch (Exception ex)
        {
            _logError("Failed to process EasyTier room message.", ex);
        }
    }

    private void HandleGuestJoined(byte[] routeIdentity, uint clientIdentity, byte[] payload)
    {
        var guest = PackSerializer.Deserialize<MvlRoomPlayerInfo>(payload);
        if (guest is null)
        {
            return;
        }

        guest.Identity = clientIdentity;
        guest.LastHeartbeat = DateTimeOffset.UtcNow;
        var added = false;
        lock (_sync)
        {
            var existingIndex = _players.FindIndex(player =>
                player.RoomType == MvlRoomType.Guest && player.Identity == clientIdentity);
            if (existingIndex >= 0)
            {
                _players[existingIndex] = guest;
            }
            else
            {
                _players.Add(guest);
                added = true;
            }
        }

        Send(routeIdentity, MvlRoomEvent.JoinAccepted, SerializePlayers());
        if (added)
        {
            Broadcast(MvlRoomEvent.AddGuest, PackSerializer.Serialize(guest), exceptIdentity: clientIdentity);
            RaiseGuestCountChanged();
        }
    }

    private void HandleGuestLeft(uint clientIdentity)
    {
        MvlRoomPlayerInfo? removed = null;
        lock (_sync)
        {
            var index = _players.FindIndex(player =>
                player.RoomType == MvlRoomType.Guest && player.Identity == clientIdentity);
            if (index >= 0)
            {
                removed = _players[index];
                _players.RemoveAt(index);
            }
        }

        if (removed is null)
        {
            return;
        }

        Broadcast(MvlRoomEvent.GuestLeft, PackSerializer.Serialize(removed));
        RaiseGuestCountChanged();
    }

    private void TouchGuest(uint clientIdentity)
    {
        lock (_sync)
        {
            var player = _players.FirstOrDefault(candidate =>
                candidate.RoomType == MvlRoomType.Guest && candidate.Identity == clientIdentity);
            if (player is not null)
            {
                player.LastHeartbeat = DateTimeOffset.UtcNow;
            }
        }
    }

    private void OnHeartbeatTimerElapsed(object? sender, NetMQTimerEventArgs eventArgs)
    {
        List<MvlRoomPlayerInfo>? removed = null;
        var threshold = DateTimeOffset.UtcNow.AddSeconds(-18);
        lock (_sync)
        {
            for (var index = _players.Count - 1; index >= 0; index--)
            {
                var player = _players[index];
                if (player.RoomType != MvlRoomType.Guest || player.LastHeartbeat >= threshold)
                {
                    continue;
                }

                removed ??= [];
                removed.Add(player);
                _players.RemoveAt(index);
            }
        }

        if (removed is null)
        {
            return;
        }

        foreach (var player in removed)
        {
            Broadcast(MvlRoomEvent.GuestLeft, PackSerializer.Serialize(player));
        }

        RaiseGuestCountChanged();
    }

    private void NotifyHostShutdown()
    {
        Broadcast(MvlRoomEvent.HostShutdown, null);
    }

    private void Broadcast(MvlRoomEvent eventCode, byte[]? payload, uint? exceptIdentity = null)
    {
        List<byte[]> identities;
        lock (_sync)
        {
            identities = _players
                .Where(player => player.RoomType == MvlRoomType.Guest && player.Identity != exceptIdentity)
                .Select(player => BitConverter.GetBytes(player.Identity))
                .ToList();
        }

        foreach (var identity in identities)
        {
            Send(identity, eventCode, payload);
        }
    }

    private void Send(byte[] routeIdentity, MvlRoomEvent eventCode, byte[]? payload = null)
    {
        var socket = _routerSocket;
        if (socket is null || _disposed)
        {
            return;
        }

        var message = new NetMQMessage();
        message.Append(routeIdentity);
        message.Append(BitConverter.GetBytes((int)eventCode));
        if (payload is not null)
        {
            message.Append(payload);
        }

        socket.TrySendMultipartMessage(message);
    }

    private byte[] SerializePlayers()
    {
        lock (_sync)
        {
            return PackSerializer.Serialize<List<MvlRoomPlayerInfo>, MvlRoomMessagePackContext>(_players.ToList());
        }
    }

    private void RaiseGuestCountChanged()
    {
        int count;
        lock (_sync)
        {
            count = _players.Count(static player => player.RoomType == MvlRoomType.Guest);
        }

        GuestCountChanged?.Invoke(this, count);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }
}

internal enum MvlRoomType
{
    Host,
    Guest
}

internal enum MvlRoomEvent
{
    GuestJoined,
    JoinAccepted,
    AddGuest,
    GuestLeft,
    HostShutdown,
    Ping,
    Pong,
    None = -1
}

[GenerateShape]
internal sealed partial record MvlRoomPlayerInfo(
    MvlRoomType RoomType,
    string Name,
    ushort Port,
    string Address,
    string Version)
{
    public uint Identity { get; set; }

    [JsonIgnore]
    [PropertyShape(Ignore = true)]
    public DateTimeOffset LastHeartbeat { get; set; }
}

[GenerateShapeFor<List<MvlRoomPlayerInfo>>]
internal partial class MvlRoomMessagePackContext;
