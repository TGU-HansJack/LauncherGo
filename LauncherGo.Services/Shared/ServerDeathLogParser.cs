namespace LauncherGo.Services;

internal static class ServerDeathLogParser
{
    public static bool TryParse(string value, out string playerName, out string deathMessage)
    {
        playerName = string.Empty;
        deathMessage = string.Empty;
        var text = value.Trim();
        var auditIndex = text.LastIndexOf("[Audit]", StringComparison.OrdinalIgnoreCase);
        if (auditIndex >= 0) text = text[(auditIndex + "[Audit]".Length)..].Trim();

        const string chineseMarker = "死亡消息";
        var messageIndex = text.IndexOf(chineseMarker, StringComparison.Ordinal);
        if (messageIndex >= 0)
        {
            var diedIndex = text.LastIndexOf("已死亡", messageIndex, StringComparison.Ordinal);
            var separatorIndex = text.IndexOfAny(['：', ':'], messageIndex + chineseMarker.Length);
            if (diedIndex > 0 && separatorIndex >= 0 && separatorIndex + 1 < text.Length)
            {
                playerName = text[..diedIndex].Trim();
                deathMessage = text[(separatorIndex + 1)..].Trim();
            }
        }
        else
        {
            const string englishMarker = " died. Death message:";
            var englishIndex = text.IndexOf(englishMarker, StringComparison.OrdinalIgnoreCase);
            if (englishIndex > 0)
            {
                playerName = text[..englishIndex].Trim();
                deathMessage = text[(englishIndex + englishMarker.Length)..].Trim();
            }
        }

        return !string.IsNullOrWhiteSpace(playerName) && !string.IsNullOrWhiteSpace(deathMessage);
    }
}
