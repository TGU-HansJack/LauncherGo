using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;

namespace LauncherGo.Services;

public sealed class OsqSnapshotCacheService : IOsqSnapshotCacheService
{
    private const int MaxSnapshotsPerHost = 30;
    private readonly object _sync = new();
    private readonly Dictionary<string, LinkedList<JsonObject>> _snapshotsByHost = new(StringComparer.OrdinalIgnoreCase);

    public event EventHandler<OsqSnapshotReceivedEventArgs>? SnapshotReceived;

    public void AddSnapshot(string serverHost, JsonObject payload, DateTimeOffset receivedAtUtc)
    {
        var host = string.IsNullOrWhiteSpace(serverHost) ? "local" : serverHost.Trim();
        var snapshot = Clone(payload);
        snapshot["sourceHost"] = host;
        snapshot["receivedAtUtc"] = receivedAtUtc.ToString("O");
        snapshot["schemaVersion"] = ReadSchemaVersion(snapshot);
        snapshot["capabilities"] ??= BuildCapabilities(snapshot);

        lock (_sync)
        {
            if (!_snapshotsByHost.TryGetValue(host, out var list))
            {
                list = new LinkedList<JsonObject>();
                _snapshotsByHost[host] = list;
            }

            list.AddFirst(Clone(snapshot));
            while (list.Count > MaxSnapshotsPerHost)
            {
                list.RemoveLast();
            }
        }

        SnapshotReceived?.Invoke(this, new OsqSnapshotReceivedEventArgs
        {
            ServerHost = host,
            Payload = Clone(snapshot),
            ReceivedAtUtc = receivedAtUtc
        });
    }

    public JsonObject? GetLatestPayload(string serverHost, int index = 1)
    {
        if (index <= 0)
        {
            return null;
        }

        var host = string.IsNullOrWhiteSpace(serverHost) ? "local" : serverHost.Trim();
        lock (_sync)
        {
            if (!_snapshotsByHost.TryGetValue(host, out var list))
            {
                return null;
            }

            var current = list.First;
            for (var i = 1; current is not null && i < index; i++)
            {
                current = current.Next;
            }

            return current?.Value is null ? null : Clone(current.Value);
        }
    }

    public IReadOnlyList<JsonObject> GetRecentPayloads(string serverHost, int count)
    {
        if (count <= 0)
        {
            return [];
        }

        var host = string.IsNullOrWhiteSpace(serverHost) ? "local" : serverHost.Trim();
        lock (_sync)
        {
            if (!_snapshotsByHost.TryGetValue(host, out var list))
            {
                return [];
            }

            return list
                .Take(count)
                .Select(Clone)
                .ToList();
        }
    }

    private static JsonObject Clone(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())?.AsObject() ?? [];
    }

    private static int ReadSchemaVersion(JsonObject payload)
    {
        if (payload.TryGetPropertyValue("schemaVersion", out var node) &&
            node is JsonValue value &&
            value.TryGetValue<int>(out var version) &&
            version > 0)
        {
            return version;
        }

        return 2;
    }

    private static JsonArray BuildCapabilities(JsonObject payload)
    {
        var capabilities = new JsonArray("serverInfo");
        if (payload["players"] is JsonArray { Count: > 0 })
        {
            capabilities.Add("players");
        }

        if (payload["playerDetails"] is JsonArray { Count: > 0 })
        {
            capabilities.Add("playerDetails");
        }

        if (payload["recentChats"] is JsonArray { Count: > 0 })
        {
            capabilities.Add("chats");
        }

        return capabilities;
    }
}
