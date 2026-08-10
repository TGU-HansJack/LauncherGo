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
            Command("/card", RobotCustomMessageType.JsonCard, "[]")
        ]);

        var command = Assert.Single(commands);
        Assert.Equal("/rules", command.Command);
        Assert.Equal("First", command.Content);
    }

    [Fact]
    public void JsonCard_RequiresObjectRoot()
    {
        Assert.True(RobotCustomCommandRules.IsValidJsonCard("{\"app\":\"com.tencent.structmsg\"}"));
        Assert.False(RobotCustomCommandRules.IsValidJsonCard("[1,2,3]"));
        Assert.False(RobotCustomCommandRules.IsValidJsonCard("not-json"));
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
    public void JsonCommand_BuildsOneBotJsonSegmentWithCompactPayload()
    {
        var message = RobotOneBotMessageBuilder.BuildCustomMessage(
            Command("/card", RobotCustomMessageType.JsonCard, "{ \"app\": \"demo\", \"meta\": { \"title\": \"Server\" } }"));

        var segment = Assert.IsType<JsonObject>(Assert.Single(message));
        Assert.Equal("json", segment["type"]?.GetValue<string>());
        var cardJson = segment["data"]?["data"]?.GetValue<string>();
        var card = Assert.IsType<JsonObject>(JsonNode.Parse(Assert.IsType<string>(cardJson)));
        Assert.Equal("demo", card["app"]?.GetValue<string>());
        Assert.Equal("Server", card["meta"]?["title"]?.GetValue<string>());
    }

    [Fact]
    public void NewsCard_UsesTencentStructuredMessageShape()
    {
        var message = RobotOneBotMessageBuilder.BuildNewsCardMessage(
            "Test Server - Online",
            "Online: 2/8",
            "[Server Status] Test Server 2/8",
            "LauncherGo | 12:00:00",
            1_700_000_000);

        var segment = Assert.IsType<JsonObject>(Assert.Single(message));
        var cardJson = Assert.IsType<string>(segment["data"]?["data"]?.GetValue<string>());
        var card = Assert.IsType<JsonObject>(JsonNode.Parse(cardJson));
        Assert.Equal("com.tencent.structmsg", card["app"]?.GetValue<string>());
        Assert.Equal("news", card["view"]?.GetValue<string>());
        Assert.Equal("Test Server - Online", card["meta"]?["news"]?["title"]?.GetValue<string>());
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
