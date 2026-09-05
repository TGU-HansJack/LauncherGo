using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerBridgePlayerSnapshotTests
{
    [Fact]
    public void ParseBridgePlayers_ReadsBaseAndExtendedFieldsAndSkipsOfflinePlayers()
    {
        var profile = new InstanceProfile { Id = "profile-1", Name = "生存服" };
        var data = new JsonObject
        {
            ["players"] = new JsonArray
            {
                new JsonObject
                {
                    ["uid"] = "uid-alice",
                    ["name"] = "Alice",
                    ["online"] = true,
                    ["connectionState"] = "Playing",
                    ["pingMs"] = 42,
                    ["joinedAtUtc"] = "2026-09-05T01:02:03Z",
                    ["lastActivityUtc"] = "2026-09-05T01:04:05Z",
                    ["gameMode"] = "Survival",
                    ["role"] = "suplayer",
                    ["dimension"] = 0,
                    ["x"] = 12.5,
                    ["y"] = 110.0,
                    ["z"] = -8.25
                },
                new JsonObject
                {
                    ["uid"] = "uid-bob",
                    ["name"] = "Bob",
                    ["online"] = false
                },
                new JsonObject
                {
                    ["uid"] = "uid-charlie",
                    ["name"] = "Charlie",
                    ["connectionState"] = "Offline"
                }
            }
        };

        var player = Assert.Single(ServerProcessService.ParseBridgePlayers(profile, data));

        Assert.Equal("uid-alice", player.PlayerUid);
        Assert.Equal("Alice", player.PlayerName);
        Assert.Equal("profile-1", player.ProfileId);
        Assert.Equal("生存服", player.ProfileName);
        Assert.Equal(42, player.PingMilliseconds);
        Assert.Equal("Playing", player.ConnectionState);
        Assert.Equal(DateTimeOffset.Parse("2026-09-05T01:02:03Z"), player.JoinedAtUtc);
        Assert.Equal("Survival", player.GameMode);
        Assert.Equal("suplayer", player.Role);
        Assert.Equal(0, player.Dimension);
        Assert.Equal(12.5, player.X);
        Assert.True(player.HasExtendedInfo);
    }

    [Fact]
    public void MergeBridgePlayers_DoesNotUseLogDerivedPlayerState()
    {
        var logDerivedStatus = new ServerRuntimeStatus
        {
            IsRunning = true,
            ProfileId = "profile-1",
            OnlinePlayers = 7,
            OnlinePlayerNames = ["FromLog"],
            PeakOnlinePlayers = 12
        };

        var merged = ServerProcessService.MergeBridgePlayers(logDerivedStatus, []);

        Assert.Equal(0, merged.OnlinePlayers);
        Assert.Empty(merged.OnlinePlayerNames);
        Assert.Equal(0, merged.PeakOnlinePlayers);
    }
}
