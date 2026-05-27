using Avalonia;
using System;
using System.Threading.Tasks;
using LauncherGo.Services.Extensions;
using LauncherGo.Ui;
using LauncherGo.Ui.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LauncherGo.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception ex)
            {
                Console.Error.WriteLine($"[AppDomain UnhandledException] {ex}");
            }
            else
            {
                Console.Error.WriteLine($"[AppDomain UnhandledException] {eventArgs.ExceptionObject}");
            }
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Console.Error.WriteLine($"[TaskScheduler UnobservedTaskException] {eventArgs.Exception}");
            eventArgs.SetObserved();
        };

        try
        {
            using var host = Host.CreateDefaultBuilder(args)
                .ConfigureServices((_, services) =>
                {
                    services.AddLauncherGoServices();
                    services.AddLauncherGoUi();
                })
                .Build();

            ServiceLocator.Host = host;
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[Program Fatal] {ex}");
            throw;
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            .LogToTrace();
    }
}
