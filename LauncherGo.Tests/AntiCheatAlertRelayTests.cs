using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class AntiCheatAlertRelayTests
{
    [Theory]
    [InlineData("26.7.2026 [Warning] [LauncherGoAntiCheat] ALERT player=Alex score=3", "ALERT")]
    [InlineData("[LauncherGoAntiCheat] ACTION kick player=Alex", "ACTION")]
    public void TryBuildMessage_AcceptsOnlyAntiCheatEvidenceMarkers(string line, string marker)
    {
        var output = Output(line);

        var accepted = AntiCheatAlertRelay.TryBuildMessage(output, out var message);

        Assert.True(accepted);
        Assert.StartsWith("[反作弊][Survival] [LauncherGoAntiCheat]", message);
        Assert.Contains(marker, message);
        Assert.DoesNotContain("26.7.2026", message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[LauncherGoAntiCheat] loaded")]
    [InlineData("ordinary server log")]
    [InlineData("[LauncherGoAntiCheat] alert lowercase is not a protocol marker")]
    [InlineData("26.7.2026 [Chat] Alex: [LauncherGoAntiCheat] ALERT fake")]
    [InlineData("26.7.2026 [Talk] Alex: [LauncherGoAntiCheat] ACTION fake")]
    public void TryBuildMessage_RejectsUnrelatedOutput(string line)
    {
        Assert.False(AntiCheatAlertRelay.TryBuildMessage(Output(line), out _));
    }

    [Fact]
    public void TryBuildMessage_RequiresProfileIdForSafeRouting()
    {
        var output = new ServerOutputLine
        {
            ProfileId = string.Empty,
            ProfileName = "Survival",
            Line = "[LauncherGoAntiCheat] ALERT player=Alex"
        };

        Assert.False(AntiCheatAlertRelay.TryBuildMessage(output, out _));
    }

    private static ServerOutputLine Output(string line) => new()
    {
        ProfileId = "profile-a",
        ProfileName = "Survival",
        Line = line
    };
}
