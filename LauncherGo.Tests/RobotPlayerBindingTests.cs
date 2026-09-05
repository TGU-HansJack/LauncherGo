using System.Text.Json.Nodes;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class RobotPlayerBindingTests
{
    [Fact]
    public void PendingBinding_RequiresMatchingProfilePlayerAndQqNumber()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"launchergo-binding-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new RobotPlayerBindingStore(databasePath))
            {
                var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
                store.CreatePending(123456789, 10001, "profile-1", "uid-alice", "Alice", now);

                Assert.Null(store.TryComplete("profile-1", "uid-bob", "Bob", "123456789", now.AddMinutes(1)));
                Assert.Null(store.TryComplete("profile-1", "uid-alice", "Alice", "987654321", now.AddMinutes(1)));

                var completed = store.TryComplete("profile-1", "uid-alice", "Alice", "123456789", now.AddMinutes(1));
                Assert.NotNull(completed);
                var binding = store.GetBinding(123456789);
                Assert.NotNull(binding);
                Assert.Equal("uid-alice", binding.PlayerUid);
                Assert.Equal("Alice", binding.PlayerName);
                Assert.Equal(10001, binding.GroupId);
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void PendingBinding_ExpiresAfterTenMinutes()
    {
        var databasePath = Path.Combine(Path.GetTempPath(), $"launchergo-binding-{Guid.NewGuid():N}.db");
        try
        {
            using (var store = new RobotPlayerBindingStore(databasePath))
            {
                var now = new DateTimeOffset(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
                store.CreatePending(123456789, 10001, "profile-1", "uid-alice", "Alice", now);

                var completed = store.TryComplete(
                    "profile-1",
                    "uid-alice",
                    "Alice",
                    "123456789",
                    now.AddMinutes(11));

                Assert.Null(completed);
                Assert.Null(store.GetBinding(123456789));
            }
        }
        finally
        {
            if (File.Exists(databasePath)) File.Delete(databasePath);
        }
    }

    [Fact]
    public void DeathNotification_UsesExactServerLogMessageWithoutDuplicatingPlayerName()
    {
        var data = new JsonObject
        {
            ["name"] = "HuiHuaFei",
            ["deathMessage"] = "HuiHuaFei认为他可以飞。",
            ["reason"] = "Gravity"
        };

        Assert.Equal("认为他可以飞。", Vs2QQProcessService.FormatDeathReason(data));
        Assert.Equal("HuiHuaFei认为他可以飞。", Vs2QQProcessService.FormatDeathNotification(data, "HuiHuaFei"));
    }

    [Theory]
    [InlineData(
        "5.9.2026 21:39:11 [Audit] HuiHuaFei已死亡。死亡消息：HuiHuaFei认为他可以飞。",
        "HuiHuaFei",
        "HuiHuaFei认为他可以飞。")]
    [InlineData(
        "HuiHuaFei died. Death message: HuiHuaFei thought they could fly.",
        "HuiHuaFei",
        "HuiHuaFei thought they could fly.")]
    public void DeathLogParser_ExtractsFinalServerMessage(string line, string expectedPlayer, string expectedMessage)
    {
        var success = ServerDeathLogParser.TryParse(line, out var playerName, out var deathMessage);

        Assert.True(success);
        Assert.Equal(expectedPlayer, playerName);
        Assert.Equal(expectedMessage, deathMessage);
    }

    [Fact]
    public void MyInfo_FormatsInventoryAndRelativePositionWithoutPermissionData()
    {
        var player = new JsonObject
        {
            ["name"] = "Alice",
            ["connectionState"] = "Playing",
            ["pingMs"] = 42,
            ["x"] = 12.5,
            ["y"] = 110,
            ["z"] = -8.25,
            ["dimension"] = 0,
            ["gameMode"] = "Survival",
            ["role"] = "admin",
            ["health"] = 13.5,
            ["maxHealth"] = 15,
            ["inventory"] = new JsonArray
            {
                new JsonObject { ["name"] = "黑麦面包", ["quantity"] = 3 }
            }
        };

        var text = string.Join('\n', Vs2QQProcessService.BuildMyInfoLines("生存服", player));

        Assert.Contains("玩家：Alice", text);
        Assert.Contains("坐标：X=12.5, Y=110.0, Z=-8.2（相对出生点）", text);
        Assert.Contains("生命值：13.5/15.0", text);
        Assert.Contains("- 黑麦面包 x3", text);
        Assert.DoesNotContain("权限", text);
        Assert.DoesNotContain("admin", text);
    }
}
