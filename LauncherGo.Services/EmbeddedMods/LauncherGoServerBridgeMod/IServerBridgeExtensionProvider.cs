using System.Text.Json.Nodes;

namespace LauncherGoServerBridge;

public interface IServerBridgeExtensionProvider
{
    string ProviderId { get; }
    IReadOnlyCollection<string> Capabilities { get; }

    ValueTask<JsonObject?> QueryAsync(
        string method,
        JsonObject? arguments,
        CancellationToken cancellationToken);

    ValueTask<IAsyncDisposable?> SubscribeAsync(
        IReadOnlyCollection<string> events,
        Func<ServerBridgeEvent, ValueTask> emit,
        CancellationToken cancellationToken);
}

public sealed class ServerBridgeEvent
{
    public string Event { get; init; } = string.Empty;
    public DateTimeOffset TimestampUtc { get; init; } = DateTimeOffset.UtcNow;
    public JsonObject Data { get; init; } = new();
}
