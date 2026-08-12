using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LauncherGo.Services;

internal static class ServerRelayClient
{
    public static Task<ServerRelayResponse> PingAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            pipeName,
            new ServerRelayRequest { Type = ServerRelayProtocol.RequestTypePing },
            TimeSpan.FromSeconds(2),
            cancellationToken);
    }

    public static Task<ServerRelayResponse> DiscoverAsync(
        string pipeName,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            pipeName,
            new ServerRelayRequest { Type = ServerRelayProtocol.RequestTypeDiscover },
            TimeSpan.FromMilliseconds(500),
            cancellationToken);
    }

    public static Task<ServerRelayResponse> PingAsync(
        ServerRelayState state,
        CancellationToken cancellationToken = default)
    {
        return SendAuthenticatedAsync(
            state,
            new ServerRelayRequest { Type = ServerRelayProtocol.RequestTypePing },
            TimeSpan.FromSeconds(2),
            cancellationToken);
    }

    public static Task<ServerRelayResponse> SendCommandAsync(
        string pipeName,
        string command,
        CancellationToken cancellationToken = default)
    {
        return SendAsync(
            pipeName,
            new ServerRelayRequest
            {
                Type = ServerRelayProtocol.RequestTypeCommand,
                Command = command
            },
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    public static Task<ServerRelayResponse> SendCommandAsync(
        ServerRelayState state,
        string command,
        CancellationToken cancellationToken = default)
    {
        return SendAuthenticatedAsync(
            state,
            new ServerRelayRequest
            {
                Type = ServerRelayProtocol.RequestTypeCommand,
                Command = command
            },
            TimeSpan.FromSeconds(5),
            cancellationToken);
    }

    private static async Task<ServerRelayResponse> SendAuthenticatedAsync(
        ServerRelayState state,
        ServerRelayRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        request.InstanceId = state.InstanceId;
        request.ControlToken = state.ControlToken;
        var response = await SendAsync(state.PipeName, request, timeout, cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success || state.SchemaVersion < ServerRelayProtocol.CurrentSchemaVersion)
        {
            return response;
        }

        if (response.State is null ||
            !string.Equals(response.State.InstanceId, state.InstanceId, StringComparison.Ordinal) ||
            !FixedTimeEquals(response.State.ControlToken, state.ControlToken))
        {
            return new ServerRelayResponse
            {
                Success = false,
                Error = "Relay response identity did not match the expected instance."
            };
        }

        return response;
    }

    private static bool FixedTimeEquals(string left, string right)
    {
        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(left),
            Encoding.UTF8.GetBytes(right));
    }

    private static async Task<ServerRelayResponse> SendAsync(
        string pipeName,
        ServerRelayRequest request,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(pipeName))
            return new ServerRelayResponse { Success = false, Error = "Relay pipe name is empty." };

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        try
        {
            await using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await pipe.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);

            using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
            await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
            {
                AutoFlush = true
            };

            var requestJson = JsonSerializer.Serialize(request, ServerRelayProtocol.JsonOptions);
            await writer.WriteLineAsync(requestJson.AsMemory(), timeoutCts.Token).ConfigureAwait(false);

            var responseJson = await reader.ReadLineAsync(timeoutCts.Token).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(responseJson))
                return new ServerRelayResponse { Success = false, Error = "Relay returned an empty response." };

            return JsonSerializer.Deserialize<ServerRelayResponse>(
                       responseJson,
                       ServerRelayProtocol.JsonOptions)
                   ?? new ServerRelayResponse { Success = false, Error = "Relay response could not be parsed." };
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new ServerRelayResponse { Success = false, Error = "Relay request timed out." };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new ServerRelayResponse { Success = false, Error = ex.Message };
        }
    }
}

