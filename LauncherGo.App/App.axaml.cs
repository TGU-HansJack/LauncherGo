using System.Globalization;
using System;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using LauncherGo.Abstractions.Services;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Domains.Enums;
using LauncherGo.Ui;
using LauncherGo.Ui.Views;
using Serilog;

namespace LauncherGo.App;

public partial class App : Application
{
    public App()
    {
        ShowWindowCommand = new DelegateCommand(ShowMainWindowFromTray);
        ExitCommand = new DelegateCommand(ExitFromTray);
    }

    public ICommand ShowWindowCommand { get; }

    public ICommand ExitCommand { get; }

    public override void Initialize()
    {
        DataContext = this;
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        Dispatcher.UIThread.UnhandledException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "Unhandled UI dispatcher exception.");
            eventArgs.Handled = true;
        };

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var preferencesService = ServiceLocator.GetRequiredService<ILauncherPreferencesService>();
            var preferences = preferencesService.Load();

            var localizationService = ServiceLocator.GetRequiredService<ILocalizationService>();
            localizationService.SetLanguage(preferences.Language);

            ApplyTheme(preferences.ThemeMode);

            desktop.MainWindow = preferences.IsOnboardingCompleted
                ? ServiceLocator.GetRequiredService<LauncherMainWindow>()
                : ServiceLocator.GetRequiredService<FirstLaunchGuideWindow>();

            // Migration may touch a locked SQLite database. Run it after the
            // window is assigned so startup never blocks the desktop lifetime.
            _ = RunServerBridgeMigrationAsync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static async Task RunServerBridgeMigrationAsync()
    {
        try
        {
            await ServiceLocator.GetRequiredService<IServerBridgeMigrationService>()
                .MigrateAsync()
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Server bridge legacy migration failed.");
        }
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

    private void OnTrayIconClicked(object? sender, EventArgs e)
    {
        ShowMainWindowFromTray();
    }

    private void ExitFromTray()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        if (desktop.MainWindow is LauncherMainWindow launcherMainWindow)
        {
            launcherMainWindow.RequestExit();
            return;
        }

        desktop.Shutdown();
    }

    private void ShowMainWindowFromTray()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop || desktop.MainWindow is null)
        {
            return;
        }

        desktop.MainWindow.ShowInTaskbar = true;
        desktop.MainWindow.Show();
        desktop.MainWindow.WindowState = WindowState.Normal;
        desktop.MainWindow.Activate();
    }

    private sealed class DelegateCommand(Action execute) : ICommand
    {
        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => execute();

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
