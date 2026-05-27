using System.Globalization;
using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Ui;
using LauncherGo.Ui.Views;

namespace LauncherGo.App;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"[UI UnhandledException] {eventArgs.Exception}");
            eventArgs.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var preferencesService = ServiceLocator.GetRequiredService<ILauncherPreferencesService>();
            var preferences = preferencesService.Load();

            ApplyCulture(preferences.Language);
            ApplyTheme(preferences.ThemeMode);

            desktop.MainWindow = ServiceLocator.GetRequiredService<FirstLaunchGuideWindow>();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void ApplyTheme(ThemeMode mode)
    {
        RequestedThemeVariant = mode switch
        {
            ThemeMode.Dark => ThemeVariant.Dark,
            ThemeMode.Light => ThemeVariant.Light,
            _ => ThemeVariant.Default
        };
    }

    private static void ApplyCulture(string languageCode)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageCode);
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
        }
        catch
        {
            // Ignore invalid culture name.
        }
    }
}
