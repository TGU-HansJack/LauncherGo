using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerBridgeNotificationTests
{
    [Theory]
    [InlineData("一场小型时空风暴正在接近。")]
    [InlineData("中型时空风暴即将来临。")]
    [InlineData("时空风暴似乎正在衰退。")]
    public void TemporalStormBroadcast_IsExtractedAndAllowedForRobotRelay(string message)
    {
        var line = $"Message to all in group 0: {message}";

        var parsed = ServerBroadcastLogParser.TryParse(line, out var content);

        Assert.True(parsed);
        Assert.Equal(message, content);
        Assert.False(ServerLogPrivacyFilter.ShouldSuppressRelayParts(content));
    }
}
