using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

internal static class RobotOneBotMessageBuilder
{
    public static JsonArray BuildCustomMessage(RobotCustomCommand command)
    {
        if (!RobotCustomCommandRules.TryNormalize(command, out var normalized))
        {
            throw new ArgumentException("Invalid robot custom command.", nameof(command));
        }

        return normalized.MessageType switch
        {
            RobotCustomMessageType.Text => BuildSegment("text", "text", normalized.Content),
            RobotCustomMessageType.Image => BuildSegment("image", "file", normalized.Content),
            RobotCustomMessageType.JsonCard => BuildJsonCardMessage(normalized.Content),
            _ => throw new ArgumentOutOfRangeException(nameof(command), normalized.MessageType, null)
        };
    }

    public static JsonArray BuildJsonCardMessage(string cardJson)
    {
        if (JsonNode.Parse(cardJson) is not JsonObject card)
        {
            throw new ArgumentException("A JSON card must use an object as its root.", nameof(cardJson));
        }

        return BuildSegment("json", "data", card.ToJsonString());
    }

    public static JsonArray BuildNewsCardMessage(
        string title,
        string description,
        string prompt,
        string tag,
        long timestamp)
    {
        var payload = new JsonObject
        {
            ["app"] = "com.tencent.structmsg",
            ["config"] = new JsonObject
            {
                ["autosize"] = true,
                ["ctime"] = timestamp,
                ["forward"] = true,
                ["type"] = "normal"
            },
            ["desc"] = "服务器状态",
            ["meta"] = new JsonObject
            {
                ["news"] = new JsonObject
                {
                    ["desc"] = description,
                    ["jumpUrl"] = string.Empty,
                    ["preview"] = string.Empty,
                    ["tag"] = tag,
                    ["tagIcon"] = string.Empty,
                    ["title"] = title
                }
            },
            ["prompt"] = prompt,
            ["ver"] = "0.0.0.1",
            ["view"] = "news"
        };

        return BuildJsonCardMessage(payload.ToJsonString());
    }

    private static JsonArray BuildSegment(string type, string dataKey, string value)
    {
        return
        [
            new JsonObject
            {
                ["type"] = type,
                ["data"] = new JsonObject
                {
                    [dataKey] = value
                }
            }
        ];
    }
}
