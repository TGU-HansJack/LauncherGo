using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LauncherGoCommandBridge;

/// <summary>
///     A local, authenticated console bridge. Commands are injected on the Vintage Story server thread.
/// </summary>
public sealed class LauncherGoCommandBridgeModSystem : ModSystem
{
    private const string ConfigurationFileName = "launchergocommandbridge.json";
    private const string BridgeVersion = "1.0.1";
    private const int MaximumRequestBytes = 32768;
    private ICoreServerAPI? _serverApi;
    private TcpListener? _listener;
    private CancellationTokenSource? _listenerCts;
    private Task? _acceptLoop;
    private CommandBridgeConfiguration _configuration = new();
    private readonly object _configurationLock = new();

    public override void StartServerSide(ICoreServerAPI api)
    {
        _serverApi = api;
        _configuration = LoadConfiguration(api);
        if (!_configuration.Enabled)
            return;

        try
        {
            _listenerCts = new CancellationTokenSource();
            _listener = new TcpListener(IPAddress.Loopback, _configuration.Port);
            _listener.Start();
            _acceptLoop = Task.Run(() => AcceptLoopAsync(_listenerCts.Token));
            api.Logger.Notification("LauncherGo Command Bridge is listening on 127.0.0.1:{0}.", _configuration.Port);
        }
        catch (Exception ex)
        {
            api.Logger.Error("LauncherGo Command Bridge failed to start: {0}", ex.Message);
            StopListener();
        }
    }

    public override void Dispose()
    {
        StopListener();
        _serverApi = null;
        base.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = Task.Run(() => HandleClientAsync(client, cancellationToken), CancellationToken.None);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(200, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken serverCancellationToken)
    {
        using (client)
        await using (var stream = client.GetStream())
        using (var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true })
        using (var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true))
        {
            using var requestCts = CancellationTokenSource.CreateLinkedTokenSource(serverCancellationToken);
            requestCts.CancelAfter(TimeSpan.FromSeconds(5));
            CommandBridgeResponse response;
            try
            {
                var requestJson = await reader.ReadLineAsync(requestCts.Token);
                if (string.IsNullOrWhiteSpace(requestJson))
                {
                    response = Failure("Empty command bridge request.");
                }
                else if (Encoding.UTF8.GetByteCount(requestJson) > MaximumRequestBytes)
                {
                    response = Failure("Command bridge request is too large.");
                }
                else
                {
                    var request = JsonSerializer.Deserialize<CommandBridgeRequest>(requestJson, JsonOptions);
                    response = await ProcessRequestAsync(request, requestCts.Token);
                }
            }
            catch (OperationCanceledException) when (serverCancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (OperationCanceledException)
            {
                response = Failure("Command bridge request timed out.");
            }
            catch (Exception ex)
            {
                response = Failure("Invalid command bridge request: " + ex.Message);
            }

            try
            {
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, JsonOptions).AsMemory(), serverCancellationToken);
            }
            catch
            {
                // A disconnected local LauncherGo client does not affect the server.
            }
        }
    }

    private async Task<CommandBridgeResponse> ProcessRequestAsync(
        CommandBridgeRequest? request,
        CancellationToken cancellationToken)
    {
        if (request is null)
            return Failure("Command bridge request could not be parsed.");
        if (!FixedTimeEquals(request.Token, _configuration.AccessToken))
            return Failure("Command bridge authentication failed.");
        if (request.Type.Equals("ping", StringComparison.OrdinalIgnoreCase))
        {
            return new CommandBridgeResponse { Success = true, BridgeVersion = BridgeVersion };
        }

        if (request.Type.Equals("rotate-token", StringComparison.OrdinalIgnoreCase))
        {
            var replacementToken = request.NewToken?.Trim().ToLowerInvariant() ?? string.Empty;
            if (!IsValidToken(replacementToken))
                return Failure("Replacement access token is invalid.");

            lock (_configurationLock)
            {
                // Re-check after locking so concurrent rotation requests cannot overwrite each other.
                if (!FixedTimeEquals(request.Token, _configuration.AccessToken))
                    return Failure("Command bridge authentication failed.");
                _configuration.AccessToken = replacementToken;
            }

            return new CommandBridgeResponse { Success = true, BridgeVersion = BridgeVersion };
        }

        if (!request.Type.Equals("command", StringComparison.OrdinalIgnoreCase))
            return Failure("Unsupported command bridge request.");

        var command = request.Command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(command))
            return Failure("Command is empty.");
        if (command.Length > _configuration.MaxCommandLength)
            return Failure("Command exceeds the configured maximum length.");
        if (!command.StartsWith('/'))
            command = "/" + command;

        var api = _serverApi;
        if (api is null)
            return Failure("Server API is unavailable.");

        var completion = new TaskCompletionSource<CommandBridgeResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            api.Event.EnqueueMainThreadTask(() =>
            {
                try
                {
                    api.InjectConsole(command);
                    completion.TrySetResult(new CommandBridgeResponse
                    {
                        Success = true,
                        BridgeVersion = BridgeVersion
                    });
                }
                catch (Exception ex)
                {
                    completion.TrySetResult(Failure("Server rejected command: " + ex.Message));
                }
            }, "launchergocommandbridge-command");
        }
        catch (Exception ex)
        {
            return Failure("Could not queue command on the server thread: " + ex.Message);
        }

        try
        {
            return await completion.Task.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return Failure("Command was not accepted by the server thread before the timeout.");
        }
    }

    private static CommandBridgeConfiguration LoadConfiguration(ICoreServerAPI api)
    {
        try
        {
            var configuration = api.LoadModConfig<CommandBridgeConfiguration>(ConfigurationFileName)
                                ?? new CommandBridgeConfiguration();
            return NormalizeConfiguration(configuration);
        }
        catch (Exception ex)
        {
            api.Logger.Warning("LauncherGo Command Bridge could not load configuration: {0}", ex.Message);
            return new CommandBridgeConfiguration();
        }
    }

    private static CommandBridgeConfiguration NormalizeConfiguration(CommandBridgeConfiguration configuration) => new()
    {
        Enabled = configuration.Enabled,
        Port = configuration.Port is >= 1024 and <= 65535 ? configuration.Port : 19090,
        AccessToken = configuration.AccessToken?.Trim() ?? string.Empty,
        MaxCommandLength = Math.Clamp(configuration.MaxCommandLength <= 0 ? 4096 : configuration.MaxCommandLength, 256, 16384)
    };

    private void StopListener()
    {
        try
        {
            _listenerCts?.Cancel();
        }
        catch
        {
            // Nothing to recover during server shutdown.
        }

        try
        {
            _listener?.Stop();
        }
        catch
        {
            // Nothing to recover during server shutdown.
        }

        _listenerCts?.Dispose();
        _listenerCts = null;
        _listener = null;
        _acceptLoop = null;
    }

    private static bool FixedTimeEquals(string? receivedToken, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(receivedToken) || string.IsNullOrWhiteSpace(expectedToken))
            return false;
        try
        {
            var left = Convert.FromHexString(receivedToken.Trim());
            var right = Convert.FromHexString(expectedToken.Trim());
            return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static bool IsValidToken(string value) =>
        value.Length == 64 && value.All(Uri.IsHexDigit);

    private static CommandBridgeResponse Failure(string error) => new()
    {
        Success = false,
        Error = error,
        BridgeVersion = BridgeVersion
    };

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public sealed class CommandBridgeConfiguration
    {
        public bool Enabled { get; set; }
        public int Port { get; set; } = 19090;
        public string AccessToken { get; set; } = string.Empty;
        public int MaxCommandLength { get; set; } = 4096;
    }

    private sealed class CommandBridgeRequest
    {
        public string Type { get; set; } = string.Empty;
        public string Token { get; set; } = string.Empty;
        public string NewToken { get; set; } = string.Empty;
        public string Id { get; set; } = string.Empty;
        public string Command { get; set; } = string.Empty;
    }

    private sealed class CommandBridgeResponse
    {
        public bool Success { get; set; }
        public string? Error { get; set; }
        public string? BridgeVersion { get; set; }
    }
}
