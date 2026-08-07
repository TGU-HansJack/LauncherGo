using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using LauncherGo.Services;
using Nerdbank.MessagePack;
using NetMQ;
using NetMQ.Sockets;
using PolyType;
using Xunit;

namespace LauncherGo.Tests;

public sealed class MvlRoomProtocolTests
{
    private static readonly MessagePackSerializer Serializer = new();

    [Fact]
    public void PlayerPayload_IncludesOfflineForCurrentMvlClients()
    {
        var player = new MvlRoomPlayerInfo(
            MvlRoomType.Host,
            "MVL-vs-server-test",
            42420,
            "10.144.144.1",
            "2.6.4")
        {
            Identity = 123,
            Offline = false,
            Latency = TimeSpan.FromMilliseconds(12)
        };

        var payload = Serializer.Serialize<List<MvlRoomPlayerInfo>, MvlRoomMessagePackContext>([player]);
        var currentMvlPlayers = Serializer.Deserialize<List<CurrentMvlRoomPlayerInfo>, CurrentMvlRoomMessagePackContext>(payload);
        Assert.NotNull(currentMvlPlayers);
        var currentMvlPlayer = Assert.Single(currentMvlPlayers);

        Assert.False(currentMvlPlayer.Offline);
        Assert.Equal(player.Identity, currentMvlPlayer.Identity);
        Assert.Equal(player.Latency, currentMvlPlayer.Latency);
    }

    [Fact]
    public void PlayerPayload_MissingOfflineFromLegacyClientDefaultsToFalse()
    {
        var legacyPlayer = new LegacyMvlRoomPlayerInfo(
            MvlRoomType.Guest,
            "legacy-player",
            42420,
            "10.144.144.2",
            "2.5.0")
        {
            Identity = 456
        };

        var payload = Serializer.Serialize(legacyPlayer);
        var player = Serializer.Deserialize<MvlRoomPlayerInfo>(payload);

        Assert.NotNull(player);
        Assert.False(player.Offline);
        Assert.Equal(TimeSpan.Zero, player.Latency);
        Assert.Equal(legacyPlayer.Identity, player.Identity);
    }

    [Fact]
    public void EventValues_MatchCurrentMvlProtocol()
    {
        Assert.Equal(0, (int)MvlRoomEvent.GuestJoined);
        Assert.Equal(1, (int)MvlRoomEvent.JoinAccepted);
        Assert.Equal(2, (int)MvlRoomEvent.AddGuest);
        Assert.Equal(3, (int)MvlRoomEvent.GuestLeft);
        Assert.Equal(4, (int)MvlRoomEvent.HostShutdown);
        Assert.Equal(5, (int)MvlRoomEvent.Heartbeat);
        Assert.Equal(6, (int)MvlRoomEvent.HeartbeatAck);
        Assert.Equal(7, (int)MvlRoomEvent.PlayerUpdate);
        Assert.Equal(-1, (int)MvlRoomEvent.None);
    }

    [Fact]
    public void Host_UsesCurrentMvlHeartbeatHandshake()
    {
        var hostPlayer = new MvlRoomPlayerInfo(
            MvlRoomType.Host,
            "MVL-vs-server-test",
            42420,
            "10.144.144.1",
            "2.6.4")
        {
            Identity = 123
        };
        var controlPort = GetAvailablePort();
        var errors = new ConcurrentQueue<Exception?>();
        using var host = new MvlRoomHost(
            controlPort,
            hostPlayer,
            (_, error) => errors.Enqueue(error),
            TimeSpan.FromMilliseconds(100),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromMilliseconds(100));
        host.Start();

        using var guest = new DealerSocket();
        const uint guestIdentity = 456;
        guest.Options.Identity = BitConverter.GetBytes(guestIdentity);
        guest.Options.Linger = TimeSpan.Zero;
        guest.Connect($"tcp://{IPAddress.Loopback}:{controlPort}");

        var guestPlayer = new CurrentMvlRoomPlayerInfo(
            MvlRoomType.Guest,
            "current-mvl-player",
            0,
            "10.144.144.2",
            "2.6.4",
            false)
        {
            Identity = guestIdentity
        };
        var joinRequest = new NetMQMessage();
        joinRequest.Append(BitConverter.GetBytes((int)MvlRoomEvent.GuestJoined));
        joinRequest.Append(Serializer.Serialize(guestPlayer));
        Assert.True(guest.TrySendMultipartMessage(TimeSpan.FromSeconds(2), joinRequest));

        var joinAccepted = ReceiveEvent(guest, MvlRoomEvent.JoinAccepted, TimeSpan.FromSeconds(2));
        var players = Serializer.Deserialize<List<CurrentMvlRoomPlayerInfo>, CurrentMvlRoomMessagePackContext>(
            joinAccepted[1].Buffer);
        Assert.NotNull(players);
        Assert.Equal(2, players.Count);

        var heartbeat = ReceiveEvent(guest, MvlRoomEvent.Heartbeat, TimeSpan.FromSeconds(2));
        Assert.Equal(sizeof(long), heartbeat[1].Buffer.Length);

        var heartbeatAck = new NetMQMessage();
        heartbeatAck.Append(BitConverter.GetBytes((int)MvlRoomEvent.HeartbeatAck));
        heartbeatAck.Append(heartbeat[1].Buffer);
        Assert.True(guest.TrySendMultipartMessage(TimeSpan.FromSeconds(2), heartbeatAck));

        var playerUpdate = ReceiveEvent(guest, MvlRoomEvent.PlayerUpdate, TimeSpan.FromSeconds(2));
        var updatedPlayer = Serializer.Deserialize<CurrentMvlRoomPlayerInfo>(playerUpdate[1].Buffer);
        Assert.NotNull(updatedPlayer);
        Assert.Equal(guestIdentity, updatedPlayer.Identity);
        Assert.True(updatedPlayer.Latency >= TimeSpan.Zero);
        Assert.DoesNotContain(errors, static error => error is not null);
    }

    [Fact]
    public void Host_RemovesGuestThatDoesNotAcknowledgeHeartbeats()
    {
        var controlPort = GetAvailablePort();
        var hostPlayer = new MvlRoomPlayerInfo(
            MvlRoomType.Host,
            "MVL-vs-server-test",
            42420,
            "10.144.144.1",
            "2.6.4")
        {
            Identity = 123
        };
        using var guestJoined = new ManualResetEventSlim();
        using var guestTimedOut = new ManualResetEventSlim();
        using var host = new MvlRoomHost(
            controlPort,
            hostPlayer,
            (_, _) => { },
            TimeSpan.FromMilliseconds(50),
            TimeSpan.FromMilliseconds(250),
            TimeSpan.FromMilliseconds(50));
        host.GuestCountChanged += (_, count) =>
        {
            if (count == 1)
            {
                guestJoined.Set();
            }
            else if (count == 0)
            {
                guestTimedOut.Set();
            }
        };
        host.Start();

        using var guest = new DealerSocket();
        const uint guestIdentity = 789;
        guest.Options.Identity = BitConverter.GetBytes(guestIdentity);
        guest.Options.Linger = TimeSpan.Zero;
        guest.Connect($"tcp://{IPAddress.Loopback}:{controlPort}");

        var guestPlayer = new CurrentMvlRoomPlayerInfo(
            MvlRoomType.Guest,
            "unresponsive-player",
            0,
            "10.144.144.3",
            "2.6.4",
            false)
        {
            Identity = guestIdentity
        };
        var joinRequest = new NetMQMessage();
        joinRequest.Append(BitConverter.GetBytes((int)MvlRoomEvent.GuestJoined));
        joinRequest.Append(Serializer.Serialize(guestPlayer));
        Assert.True(guest.TrySendMultipartMessage(TimeSpan.FromSeconds(2), joinRequest));
        ReceiveEvent(guest, MvlRoomEvent.JoinAccepted, TimeSpan.FromSeconds(2));

        Assert.True(guestJoined.Wait(TimeSpan.FromSeconds(2)));
        Assert.True(guestTimedOut.Wait(TimeSpan.FromSeconds(2)));
    }

    private static NetMQMessage ReceiveEvent(
        DealerSocket socket,
        MvlRoomEvent expectedEvent,
        TimeSpan timeout)
    {
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        while (DateTimeOffset.UtcNow < deadline)
        {
            NetMQMessage? message = null;
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (!socket.TryReceiveMultipartMessage(remaining, ref message) || message is null)
            {
                break;
            }

            if (message.FrameCount >= 1 &&
                message[0].Buffer.Length >= sizeof(int) &&
                (MvlRoomEvent)BitConverter.ToInt32(message[0].Buffer, 0) == expectedEvent)
            {
                return message;
            }
        }

        throw new TimeoutException($"Did not receive MVL event {expectedEvent} within {timeout}.");
    }

    private static ushort GetAvailablePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return checked((ushort)((IPEndPoint)listener.LocalEndpoint).Port);
    }
}

[GenerateShape]
internal sealed partial record CurrentMvlRoomPlayerInfo(
    MvlRoomType RoomType,
    string Name,
    ushort Port,
    string Address,
    string Version,
    bool Offline)
{
    public uint Identity { get; init; }

    public TimeSpan Latency { get; init; }
}

[GenerateShape]
internal sealed partial record LegacyMvlRoomPlayerInfo(
    MvlRoomType RoomType,
    string Name,
    ushort Port,
    string Address,
    string Version)
{
    public uint Identity { get; init; }
}

[GenerateShapeFor<List<CurrentMvlRoomPlayerInfo>>]
internal partial class CurrentMvlRoomMessagePackContext;
