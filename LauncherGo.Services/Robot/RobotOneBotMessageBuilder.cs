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
            _ => throw new ArgumentOutOfRangeException(nameof(command), normalized.MessageType, null)
        };
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
