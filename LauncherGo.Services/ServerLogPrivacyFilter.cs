using System.Text.RegularExpressions;

namespace LauncherGo.Services;

internal static class ServerLogPrivacyFilter
{
    private const string LauncherLogPrefix = "[log]";

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

    private static readonly Regex ConsoleAuditInventoryNoiseRegex =
        new(@"\[audit\].*(?:shift clicked slot|left clicked slot|right clicked slot|middle clicked slot|slot\s+\d+\s+in\s+|before:\s*\(|after:\s*\(|moved\s+.+\s+from\s+.+\s+to\s+.+|harvestablecontents-|backpack-|hotbar-|ground-|mouse-)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleChatRegex =
        new(@"\[(?:Talk|Chat)\]|<[^>]+>\s*.+",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleJoinLeaveRegex =
        new(@"(?:joins\.|joined\.|left\.|leaves\.|加入了服务器|离开了服务器|进入服务器|离开服务器|加入游戏|离开游戏)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleDeathRegex =
        new(@"(?:\bdied\b|has died|death message|death reason|fell from a high place|fell to (?:his|her|their) death|fell off .+|plummeted .+|已死亡|死亡消息|死因|摔死|从高处坠落而亡|坠落身亡)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleNotificationRegex =
        new(@"\[(?:Server\s+)?Notification\]|服务器通知|message to all in group",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleLifecycleRegex =
        new(@"(?:\bstart(?:ing|ed)?\b|\bstop(?:ping|ped)?\b|\bshut(?:ting)?\s*down\b|\bshutdown\b|\bcrash(?:ed)?\b|\bsav(?:e|ed|ing)\b|\bbackup\b|正在保存|保存完成|备份完成|备份失败)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleAdminRegex =
        new(@"(?:\bkick(?:ed|ing)?\b|\bban(?:ned|ning)?\b|\bwhitelist\b|\bauth(?:entication)?\b.*\b(?:failed|failure|required|denied)\b|\blogin\b.*\bfailed\b|\brejected\b|\bdenied\b|白名单|认证失败|登录失败|踢出|封禁)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex ConsoleSpecialEventRegex =
        new(@"(?:temporal|rift|storm|boss|特殊事件|时空|裂隙|风暴|首领)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static bool ShouldSuppressConsoleLogLine(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var normalized = StripLauncherLogPrefix(line.Trim());
        if (normalized.Length == 0)
        {
            return false;
        }

        if (BlockEntityInitializeNoiseRegex.IsMatch(normalized))
        {
            return true;
        }

        var lower = normalized.ToLowerInvariant();
        if (lower.Contains("[noofflinecontainerfoodspoil]", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("container block placed at", StringComparison.Ordinal)
            && lower.Contains("owner set to", StringComparison.Ordinal))
        {
            return true;
        }

        if (ConsoleAuditInventoryNoiseRegex.IsMatch(normalized))
        {
            return true;
        }

        if (lower.Contains("[audit]", StringComparison.Ordinal) && lower.Contains(" killed game:", StringComparison.Ordinal))
        {
            return true;
        }

        return !IsDefaultConsoleLine(normalized, lower);
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

    private static bool IsDefaultConsoleLine(string normalized, string lower)
    {
        if (ConsoleChatRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ConsoleNotificationRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ConsoleJoinLeaveRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ConsoleDeathRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ConsoleAdminRegex.IsMatch(normalized))
        {
            return true;
        }

        if (ConsoleLifecycleRegex.IsMatch(normalized))
        {
            return true;
        }

        if (lower.Contains("[warning]", StringComparison.Ordinal)
            || lower.Contains("[error]", StringComparison.Ordinal)
            || lower.Contains("exception", StringComparison.Ordinal)
            || lower.Contains("fatal", StringComparison.Ordinal)
            || lower.Contains("unhandled", StringComparison.Ordinal)
            || lower.Contains("stack trace", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains(" killed ", StringComparison.Ordinal)
            && !lower.Contains(" killed game:", StringComparison.Ordinal))
        {
            return true;
        }

        return ConsoleSpecialEventRegex.IsMatch(normalized);
    }

    private static string StripLauncherLogPrefix(string text)
    {
        return text.StartsWith(LauncherLogPrefix, StringComparison.OrdinalIgnoreCase)
            ? text[LauncherLogPrefix.Length..].TrimStart()
            : text;
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
