using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class RobotCustomCommandTests
{
    [Theory]
    [InlineData("/help")]
    [InlineData("/helpful")]
    [InlineData("/he")]
    [InlineData("/send_more")]
    [InlineData("/server2")]
    [InlineData("/bind-player")]
    [InlineData("/myinfo2")]
    [InlineData("/tp")]
    [InlineData("/tp-home")]
    public void NormalizeCommand_RejectsBuiltInPrefixConflicts(string command)
    {
        Assert.False(RobotCustomCommandRules.TryNormalizeCommand(command, out _));
    }

    [Fact]
    public void NormalizeCommand_AddsSlashAndNormalizesCase()
    {
        var success = RobotCustomCommandRules.TryNormalizeCommand("Rules_CN-2", out var command);

        Assert.True(success);
        Assert.Equal("/rules_cn-2", command);
    }

    [Fact]
    public void NormalizeMany_DropsInvalidAndDuplicateCommands()
    {
        var commands = RobotCustomCommandRules.NormalizeMany(
        [
            Command("/rules", RobotCustomMessageType.Text, "First"),
            Command("RULES", RobotCustomMessageType.Text, "Duplicate"),
            Command("/help", RobotCustomMessageType.Text, "Reserved"),
        ]);

        var command = Assert.Single(commands);
        Assert.Equal("/rules", command.Command);
        Assert.Equal("First", command.Content);
    }

    [Fact]
    public void ImageCommand_BuildsOneBotImageSegment()
    {
        var message = RobotOneBotMessageBuilder.BuildCustomMessage(
            Command("/map", RobotCustomMessageType.Image, "https://example.com/map.png"));

        var segment = Assert.IsType<JsonObject>(Assert.Single(message));
        Assert.Equal("image", segment["type"]?.GetValue<string>());
        Assert.Equal("https://example.com/map.png", segment["data"]?["file"]?.GetValue<string>());
    }

    [Fact]
    public void TextCommand_BuildsLiteralOneBotTextSegment()
    {
        var message = RobotOneBotMessageBuilder.BuildCustomMessage(
            Command("/rules", RobotCustomMessageType.Text, "Line 1\n[CQ:at,qq=12345]"));

        var segment = Assert.IsType<JsonObject>(Assert.Single(message));
        Assert.Equal("text", segment["type"]?.GetValue<string>());
        Assert.Equal("Line 1\n[CQ:at,qq=12345]", segment["data"]?["text"]?.GetValue<string>());
    }

    [Fact]
    public void LegacyJsonCardCommand_IsIgnored()
    {
        var legacyMessageType = Enum.Parse<RobotCustomMessageType>("JsonCard");
        Assert.False(RobotCustomCommandRules.TryNormalize(
            Command("/card", legacyMessageType, "{\"app\":\"demo\"}"), out _));
    }

    private static RobotCustomCommand Command(
        string command,
        RobotCustomMessageType messageType,
        string content)
    {
        return new RobotCustomCommand
        {
            Command = command,
            MessageType = messageType,
            Content = content
        };
    }
}
