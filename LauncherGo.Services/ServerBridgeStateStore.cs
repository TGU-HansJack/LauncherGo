using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>Short-lived authoritative bridge state and event history, partitioned by profile.</summary>
public sealed class ServerBridgeStateStore
{
    private static readonly TimeSpan StateTtl = TimeSpan.FromSeconds(10);
    private const int MaxEventsPerProfile = 500;
    private readonly object _gate = new();
    private readonly Dictionary<string, (DateTimeOffset At, JsonObject Data)> _states = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LinkedList<ServerBridgeEvent>> _events = new(StringComparer.OrdinalIgnoreCase);

    public void SetState(string profileId, JsonObject state, DateTimeOffset? now = null)
        => SetState(profileId, string.Empty, state, now);

    public void SetState(string profileId, string method, JsonObject state, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return;
        lock (_gate) _states[BuildStateKey(profileId, method)] = (now ?? DateTimeOffset.UtcNow, state);
    }

    public JsonObject? GetState(string profileId, DateTimeOffset? now = null)
        => GetState(profileId, string.Empty, now);

    public JsonObject? GetState(string profileId, string method, DateTimeOffset? now = null)
    {
        lock (_gate)
        {
            if (!_states.TryGetValue(BuildStateKey(profileId, method), out var value)) return null;
            if ((now ?? DateTimeOffset.UtcNow) - value.At > StateTtl) return null;
            return value.Data;
        }
    }

    private static string BuildStateKey(string profileId, string method) => $"{profileId.Trim()}\n{method.Trim()}";

    public bool AddEvent(string profileId, ServerBridgeEvent value)
    {
        if (string.IsNullOrWhiteSpace(profileId)) return false;
        lock (_gate)
        {
            var list = _events.TryGetValue(profileId.Trim(), out var existing) ? existing : (_events[profileId.Trim()] = new LinkedList<ServerBridgeEvent>());
            if (value.Sequence > 0 && list.Any(x => x.Sequence == value.Sequence)) return false;
            list.AddLast(value);
            while (list.Count > MaxEventsPerProfile) list.RemoveFirst();
            return true;
        }
    }

    public IReadOnlyList<ServerBridgeEvent> GetEvents(string profileId, long since = 0)
    {
        lock (_gate)
            return _events.TryGetValue(profileId.Trim(), out var list)
                ? list.Where(x => x.Sequence > since).ToList()
                : [];
    }
}
