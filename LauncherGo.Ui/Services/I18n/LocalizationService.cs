using System.Globalization;
using System.Reflection;
using System.Resources;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui.Views;

namespace LauncherGo.Ui.Services.I18n;

public sealed class LocalizationService : ILocalizationService
{
    private readonly ResourceManager _resourceManager = new(
        "LauncherGo.Ui.Assets.I18n.Resources",
        typeof(LauncherMainWindow).GetTypeInfo().Assembly);

    private CultureInfo _currentCulture = CultureInfo.CurrentUICulture;

    public CultureInfo CurrentCulture
    {
        get => _currentCulture;
        set
        {
            if (_currentCulture.Equals(value))
            {
                return;
            }

            _currentCulture = value;
            CultureInfo.CurrentCulture = value;
            CultureInfo.CurrentUICulture = value;
            CultureInfo.DefaultThreadCurrentCulture = value;
            CultureInfo.DefaultThreadCurrentUICulture = value;
        }
    }

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            return _resourceManager.GetString(key, _currentCulture) ?? key;
        }
    }

    public string Format(string key, params object[] args)
    {
        var template = this[key];
        return args.Length == 0 ? template : string.Format(_currentCulture, template, args);
    }
}
