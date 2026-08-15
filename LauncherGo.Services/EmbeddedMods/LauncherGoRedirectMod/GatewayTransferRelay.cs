using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace LauncherGoRedirect;

internal sealed class GatewayTransferRelay : IDisposable
{
    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("LGTR");
    private const byte ProtocolVersion = 1;
    private readonly TcpListener _listener;
    private readonly string _gatewayHost;
    private readonly int _gatewayPort;
    private readonly string _transferTicket;
    private readonly CancellationTokenSource _stopCts = new();

    private GatewayTransferRelay(string gatewayHost, int gatewayPort, string transferTicket)
    {
        _gatewayHost = gatewayHost;
        _gatewayPort = gatewayPort;
        _transferTicket = transferTicket;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start(1);
        LocalPort = ((IPEndPoint)_listener.LocalEndpoint).Port;
        LocalEndpoint = "127.0.0.1:" + LocalPort;
    }

    public string LocalEndpoint { get; }

    public int LocalPort { get; }

    public static GatewayTransferRelay Start(string gatewayHost, int gatewayPort, string transferTicket)
    {
        if (string.IsNullOrWhiteSpace(gatewayHost) || gatewayPort is < 1 or > ushort.MaxValue ||
            string.IsNullOrWhiteSpace(transferTicket))
        {
            throw new InvalidOperationException("The original Gateway endpoint or transfer credential is invalid.");
        }

        var relay = new GatewayTransferRelay(gatewayHost.Trim(), gatewayPort, transferTicket);
        _ = relay.RunAsync();
        return relay;
    }

    public void Dispose()
    {
        _stopCts.Cancel();
        _listener.Stop();
        _stopCts.Dispose();
    }

    private async Task RunAsync()
    {
        try
        {
            using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
            connectCts.CancelAfter(TimeSpan.FromSeconds(20));
            using var gameClient = await _listener.AcceptTcpClientAsync(connectCts.Token);
            gameClient.NoDelay = true;
            using var gatewayClient = new TcpClient { NoDelay = true };
            await gatewayClient.ConnectAsync(_gatewayHost, _gatewayPort, connectCts.Token);
            await WritePreambleAsync(gatewayClient.GetStream(), _transferTicket, connectCts.Token);
            await RelayAsync(gameClient, gatewayClient, _stopCts.Token);
        }
        catch
        {
            // The game client renders the regular connection failure after the local relay closes.
        }
        finally
        {
            _listener.Stop();
        }
    }

    private static async Task RelayAsync(TcpClient gameClient, TcpClient gatewayClient, CancellationToken cancellationToken)
    {
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var gameToGateway = CopyAsync(gameClient.GetStream(), gatewayClient.GetStream(), gatewayClient.Client, relayCts.Token);
        var gatewayToGame = CopyAsync(gatewayClient.GetStream(), gameClient.GetStream(), gameClient.Client, relayCts.Token);
        await Task.WhenAny(gameToGateway, gatewayToGame);
        relayCts.Cancel();
        try
        {
            await Task.WhenAll(gameToGateway, gatewayToGame);
        }
        catch (OperationCanceledException)
        {
            // Expected once either side closes.
        }
    }

    private static async Task CopyAsync(Stream source, Stream destination, Socket destinationSocket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        try
        {
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0) return;
                await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            }
        }
        finally
        {
            try
            {
                destinationSocket.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // The opposite relay direction may already have closed the socket.
            }
        }
    }

    private static async Task WritePreambleAsync(Stream stream, string ticket, CancellationToken cancellationToken)
    {
        var ticketBytes = Encoding.ASCII.GetBytes(ticket);
        if (ticketBytes.Length is 0 or > 2048)
        {
            throw new InvalidOperationException("Gateway transfer credential is invalid.");
        }

        var header = new byte[7];
        Magic.CopyTo(header, 0);
        header[4] = ProtocolVersion;
        header[5] = (byte)(ticketBytes.Length >> 8);
        header[6] = (byte)ticketBytes.Length;
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(ticketBytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }
}

internal static class GatewayTransferTicketIssuer
{
    public static string Create(string signingSecret, string sourceServerId, string targetServerId, string playerUid)
    {
        var payload = string.Join(
            '|',
            "1",
            Encode(sourceServerId.Trim()),
            Encode(targetServerId.Trim()),
            Encode(playerUid.Trim()),
            DateTimeOffset.UtcNow.AddSeconds(30).ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        return Encode(payloadBytes) + "." + Encode(hmac.ComputeHash(payloadBytes));
    }

    private static string Encode(string value) => Encode(Encoding.UTF8.GetBytes(value));

    private static string Encode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');
}
