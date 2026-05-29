using System.Globalization;

namespace LauncherGo.Abstractions.Services.I18n;

public interface ILocalizationService
{
    CultureInfo CurrentCulture { get; set; }

    string this[string key] { get; }

    string Format(string key, params object[] args);
}
