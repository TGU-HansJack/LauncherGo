using System.Globalization;

namespace LauncherGo.Abstractions.Services.I18n;

public interface ILocalizationService
{
    /// <summary>
    /// Raised after a culture has been committed and all resource lookups use it.
    /// </summary>
    event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    CultureInfo CurrentCulture { get; set; }

    /// <summary>
    /// Changes the UI language. The change is committed as one operation and
    /// raises <see cref="LanguageChanged" /> at most once.
    /// </summary>
    bool SetLanguage(string languageCode);

    string this[string key] { get; }

    string Format(string key, params object[] args);

    /// <summary>
    /// Resolves a legacy fallback pair through the resource catalog when the
    /// pair has a matching key. This keeps older views compatible while they
    /// are migrated to key-based lookups.
    /// </summary>
    string Resolve(string simplifiedChinese, string english);
}
