namespace LauncherGo.Abstractions.Services.I18n;

/// <summary>
/// Languages supported by the launcher UI. The selector uses each language's
/// native name, so users can identify their language regardless of the
/// currently active UI culture.
/// </summary>
public sealed record SupportedLanguageOption(
    string Code,
    string ChineseName,
    string NativeName);

public static class SupportedLanguages
{
    public static IReadOnlyList<SupportedLanguageOption> All { get; } =
    [
        new("zh-CN", "中文（简体）", "中文（简体）"),
        new("en-US", "英文", "English"),
        new("ru-RU", "俄语", "Русский"),
        new("de-DE", "德语", "Deutsch"),
        new("fr-FR", "法语", "Français"),
        new("es-ES", "西班牙语", "Español"),
        new("pl-PL", "波兰语", "Polski"),
        new("pt-BR", "葡萄牙语（巴西）", "Português (Brasil)")
    ];

    public static int FindIndex(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return 0;
        }

        var normalized = languageCode.Trim();
        for (var index = 0; index < All.Count; index++)
        {
            var language = All[index];
            if (string.Equals(language.Code, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(language.Code[..2], normalized, StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return 1;
    }
}
