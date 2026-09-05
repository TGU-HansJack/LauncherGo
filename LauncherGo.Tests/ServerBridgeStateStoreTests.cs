using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerBridgeStateStoreTests
{
    [Fact]
    public void StateExpiresAfterTenSeconds()
    {
        var store = new ServerBridgeStateStore();
        var now = DateTimeOffset.UtcNow;
        store.SetState("p", new JsonObject { ["status"] = "online" }, now);
        Assert.NotNull(store.GetState("p", now.AddSeconds(9)));
        Assert.Null(store.GetState("p", now.AddSeconds(11)));
    }

    [Fact]
    public void EventsAreDeduplicatedAndLimitedToFiveHundred()
    {
        var store = new ServerBridgeStateStore();
        for (var sequence = 1; sequence <= 501; sequence++)
            Assert.True(store.AddEvent("p", new ServerBridgeEvent { Sequence = sequence, Event = "chat" }));
        Assert.False(store.AddEvent("p", new ServerBridgeEvent { Sequence = 501, Event = "chat" }));
        var events = store.GetEvents("p");
        Assert.Equal(500, events.Count);
        Assert.Equal(2, events[0].Sequence);
    }
}
