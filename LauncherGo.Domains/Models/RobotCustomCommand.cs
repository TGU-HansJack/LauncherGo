using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace LauncherGo.Domains.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RobotCustomMessageType
{
    Text,
    Image,
    // Retained for deserializing older settings; legacy card commands are ignored.
    [Obsolete("JSON card messages are no longer supported.")]
    JsonCard
}

public sealed class RobotCustomCommand
{
    public string Command { get; set; } = string.Empty;

    public RobotCustomMessageType MessageType { get; set; }

    public string Content { get; set; } = string.Empty;
}

public static class RobotCustomCommandRules
{
    public const int MaxCommandLength = 64;

    public static readonly IReadOnlyList<string> ReservedCommands = ["/help", "/send", "/server"];

    private static readonly Regex CommandRegex = new(
        @"^/[\p{L}\p{N}_-]{1,63}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool TryNormalizeCommand(string? value, out string normalized)
    {
        normalized = (value ?? string.Empty).Trim();
        if (!normalized.StartsWith('/'))
        {
            normalized = "/" + normalized;
        }

        normalized = normalized.ToLowerInvariant();
        return normalized.Length <= MaxCommandLength &&
               CommandRegex.IsMatch(normalized) &&
               !HasReservedPrefixConflict(normalized);
    }

    public static bool HasReservedPrefixConflict(string? command)
    {
        var candidate = (command ?? string.Empty).Trim();
        if (candidate.Length <= 1)
        {
            return false;
        }

        return ReservedCommands.Any(reserved =>
            candidate.StartsWith(reserved, StringComparison.OrdinalIgnoreCase) ||
            reserved.StartsWith(candidate, StringComparison.OrdinalIgnoreCase));
    }

    public static bool TryNormalize(RobotCustomCommand? source, out RobotCustomCommand normalized)
    {
        normalized = new RobotCustomCommand();
        if (source is null ||
            !TryNormalizeCommand(source.Command, out var command) ||
            !Enum.IsDefined(source.MessageType) ||
            source.MessageType is not (RobotCustomMessageType.Text or RobotCustomMessageType.Image))
        {
            return false;
        }

        var content = source.Content?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(content))
        {
            return false;
        }

        normalized = new RobotCustomCommand
        {
            Command = command,
            MessageType = source.MessageType,
            Content = content
        };
        return true;
    }

    public static List<RobotCustomCommand> NormalizeMany(IEnumerable<RobotCustomCommand>? commands)
    {
        var result = new List<RobotCustomCommand>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var command in commands ?? [])
        {
            if (!TryNormalize(command, out var normalized) || !seen.Add(normalized.Command))
            {
                continue;
            }

            result.Add(normalized);
        }

        return result;
    }
}
