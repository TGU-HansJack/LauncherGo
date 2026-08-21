using System.Globalization;

namespace LauncherGo.Abstractions.Services.I18n;

public sealed class LanguageChangedEventArgs(CultureInfo oldCulture, CultureInfo newCulture) : EventArgs
{
    public CultureInfo OldCulture { get; } = oldCulture;

    public CultureInfo NewCulture { get; } = newCulture;
}
