using System.Text.RegularExpressions;

namespace LauncherGo.Services;

internal static class ServerLogPrivacyFilter
{
    private static readonly Regex AngleUuidRegex =
        new(@"<\s*[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\s*>",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex AngleTokenRegex =
        new(@"<\s*[^>\r\n]{1,128}\s*>",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BareUuidRegex =
        new(@"^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BarePlayerUidTokenRegex =
        new(@"^[A-Za-z0-9_-]{17,64}$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RuntimeLogLevelRegex =
        new(@"\[(?:Notification|Event|Debug|Verbose|Warning|Error|Audit)\]",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ModScopedMessageRegex =
        new(@"^\[[a-z0-9][a-z0-9_-]{2,63}\]\s+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex BlockEntityInitializeNoiseRegex =
        new(@"\[(?:Notification|Debug|Verbose)\]\s+Initialize has been called on\s+[a-z0-9_-]+:[^\s]+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmbeddedUuidRegex =
        new(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex EmbeddedPlayerUidRegex =
        new(@"(?<![A-Za-z0-9_-])(?=[A-Za-z0-9_-]{17,64}(?![A-Za-z0-9_-]))(?=[A-Za-z0-9_-]*[A-Z])(?=[A-Za-z0-9_-]*[a-z])(?=[A-Za-z0-9_-]*\d)[A-Za-z0-9_-]{17,64}(?![A-Za-z0-9_-])",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool ShouldSuppressConsoleLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        if (BlockEntityInitializeNoiseRegex.IsMatch(line))
        {
            return true;
        }

        var normalized = line.Trim();
        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("[noofflinecontainerfoodspoil]", StringComparison.Ordinal))
        {
            return true;
        }

        return lower.Contains("container block placed at", StringComparison.Ordinal)
               && lower.Contains("owner set to", StringComparison.Ordinal);
    }

    public static bool ShouldSuppressRelayParts(params string?[] parts)
    {
        var joined = string.Join(' ', parts
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part!.Trim()));

        foreach (var part in parts)
        {
            if (IsBareUuid(part) || IsBarePlayerUidToken(part) || ShouldSuppressRelayText(part))
            {
                return true;
            }
        }

        return ShouldSuppressRelayText(joined);
    }

    private static bool ShouldSuppressRelayText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var normalized = text.Trim();
        var lower = normalized.ToLowerInvariant();

        if (lower.Contains("[noofflinecontainerfoodspoil]", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("[server-restriction-report]", StringComparison.Ordinal)
            || lower.Contains("server-restriction-report", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("external origins in load order", StringComparison.Ordinal)
            || (lower.Contains("in load order", StringComparison.Ordinal) && lower.Contains("modorigin@", StringComparison.Ordinal)))
        {
            return true;
        }

        if (lower.Contains("container block placed at", StringComparison.Ordinal)
            && lower.Contains("owner set to", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("owner set to", StringComparison.Ordinal) && AngleTokenRegex.IsMatch(normalized))
        {
            return true;
        }

        if (lower.Contains("owner set to", StringComparison.Ordinal) && AngleUuidRegex.IsMatch(normalized))
        {
            return true;
        }

        if (RuntimeLogLevelRegex.IsMatch(normalized)
            && !lower.Contains("[talk]", StringComparison.Ordinal)
            && !lower.Contains("[chat]", StringComparison.Ordinal)
            && AngleTokenRegex.IsMatch(normalized))
        {
            return true;
        }

        if (RuntimeLogLevelRegex.IsMatch(normalized) && AngleUuidRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ModScopedMessageRegex.IsMatch(normalized) && AngleUuidRegex.IsMatch(normalized))
        {
            return true;
        }

        if (EmbeddedUuidRegex.IsMatch(normalized))
        {
            return true;
        }

        foreach (var token in EmbeddedPlayerUidRegex.Matches(normalized).Cast<Match>().Select(m => m.Value))
        {
            if (IsBarePlayerUidToken(token))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsBareUuid(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && BareUuidRegex.IsMatch(text.Trim());
    }

    private static bool IsBarePlayerUidToken(string? text)
    {
        return !string.IsNullOrWhiteSpace(text) && BarePlayerUidTokenRegex.IsMatch(text.Trim());
    }
}
