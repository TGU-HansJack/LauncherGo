using System.Globalization;
using System.Reflection;
using System.Resources;
using System.Collections;
using LauncherGo.Abstractions.Services.I18n;

namespace LauncherGo.Ui.Services.I18n;

public sealed class LocalizationService : ILocalizationService
{
    private static readonly CultureInfo EnglishCulture = CultureInfo.GetCultureInfo("en-US");
    private static readonly CultureInfo SimplifiedChineseCulture = CultureInfo.GetCultureInfo("zh-CN");
    private static readonly CultureInfo SimplifiedChineseResourceCulture = CultureInfo.GetCultureInfo("zh-Hans");

    private readonly ResourceManager _resourceManager = new(
        "LauncherGo.Ui.Assets.I18n.Resources",
        typeof(LocalizationService).GetTypeInfo().Assembly);

    private readonly object _sync = new();
    private readonly Lazy<IReadOnlyDictionary<string, string>> _legacyPairKeys;
    private CultureInfo _currentCulture = CultureInfo.InvariantCulture;

    public LocalizationService()
    {
        _legacyPairKeys = new Lazy<IReadOnlyDictionary<string, string>>(BuildLegacyPairKeys, true);
        SetCulture(CultureInfo.CurrentUICulture);
    }

    public event EventHandler<LanguageChangedEventArgs>? LanguageChanged;

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set => SetCulture(value);
    }

    public bool SetLanguage(string languageCode)
    {
        if (!TryGetCulture(languageCode, out var culture))
        {
            return false;
        }

        SetCulture(culture);
        return true;
    }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var resourceCulture = GetResourceCulture(_currentCulture);
            return _resourceManager.GetString(key, resourceCulture)
                   ?? _resourceManager.GetString(key, CultureInfo.InvariantCulture)
                   ?? key;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        return args.Length == 0 ? template : string.Format(_currentCulture, template, args);
    }

    public string Resolve(string simplifiedChinese, string english)
    {
        var pair = MakePairKey(simplifiedChinese, english);
        if (_legacyPairKeys.Value.TryGetValue(pair, out var key))
        {
            return this[key];
        }

        return _currentCulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? simplifiedChinese
            : english;
    }

    private void SetCulture(CultureInfo culture)
    {
        var normalized = NormalizeCulture(culture);
        LanguageChangedEventArgs? change = null;

        lock (_sync)
        {
            if (string.Equals(_currentCulture.Name, normalized.Name, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var previous = _currentCulture;
            _currentCulture = normalized;

            // Commit all culture values before notifying the UI. Subscribers can
            // therefore render a complete language without observing a partial state.
            CultureInfo.CurrentCulture = normalized;
            CultureInfo.CurrentUICulture = normalized;
            CultureInfo.DefaultThreadCurrentCulture = normalized;
            CultureInfo.DefaultThreadCurrentUICulture = normalized;
            change = new LanguageChangedEventArgs(previous, normalized);
        }

        LanguageChanged?.Invoke(this, change);
    }

    private static CultureInfo NormalizeCulture(CultureInfo culture)
    {
        if (culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            return SimplifiedChineseCulture;
        }

        if (culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return EnglishCulture;
        }

        return culture;
    }

    private static bool TryGetCulture(string? languageCode, out CultureInfo culture)
    {
        culture = EnglishCulture;
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return false;
        }

        try
        {
            culture = NormalizeCulture(CultureInfo.GetCultureInfo(languageCode.Trim()));
            // .NET accepts syntactically valid but uninstalled/custom tags such
            // as "not-a-culture". They cannot provide a stable resource lookup.
            return culture.LCID != 4096;
        }
        catch (CultureNotFoundException)
        {
            return false;
        }
    }

    private static CultureInfo GetResourceCulture(CultureInfo culture)
    {
        return culture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
            ? SimplifiedChineseResourceCulture
            : culture.Name.StartsWith("en", StringComparison.OrdinalIgnoreCase)
                ? EnglishCulture
                : culture;
    }

    private IReadOnlyDictionary<string, string> BuildLegacyPairKeys()
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var english = _resourceManager.GetResourceSet(EnglishCulture, true, true);
        var chinese = _resourceManager.GetResourceSet(SimplifiedChineseResourceCulture, true, true);
        if (english is null || chinese is null)
        {
            return result;
        }

        foreach (DictionaryEntry entry in english)
        {
            if (entry.Key is not string key || entry.Value is not string englishText)
            {
                continue;
            }

            var chineseText = chinese.GetString(key);
            if (!string.IsNullOrEmpty(chineseText))
            {
                result.TryAdd(MakePairKey(chineseText, englishText), key);
            }
        }

        return result;
    }

    private static string MakePairKey(string simplifiedChinese, string english) =>
        $"{simplifiedChinese}\u001f{english}";
}
