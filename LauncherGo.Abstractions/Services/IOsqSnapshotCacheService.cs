using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IOsqSnapshotCacheService
{
    event EventHandler<OsqSnapshotReceivedEventArgs>? SnapshotReceived;

    void AddSnapshot(string serverHost, JsonObject payload, DateTimeOffset receivedAtUtc);

    JsonObject? GetLatestPayload(string serverHost, int index = 1);

    IReadOnlyList<JsonObject> GetRecentPayloads(string serverHost, int count);
}

public sealed class OsqSnapshotReceivedEventArgs : EventArgs
{
    public required string ServerHost { get; init; }

    public required JsonObject Payload { get; init; }

    public required DateTimeOffset ReceivedAtUtc { get; init; }
}
