using Avalonia;
using System;
using System.IO;
using System.Threading.Tasks;
using LauncherGo.Services.Extensions;
using LauncherGo.Services.Paths;
using LauncherGo.Ui;
using LauncherGo.Ui.Extensions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Serilog;
using Serilog.Events;

namespace LauncherGo.App;

internal static class Program
{
    private const string LogOutputTemplate =
        "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] [{ThreadName}-{ThreadId}] [{SourceContext}] {Message:lj}{NewLine}{Exception}";

    [STAThread]
    public static void Main(string[] args)
    {
        ConfigureBootstrapLogger();
        RegisterUnhandledExceptionLogging();

        try
        {
            using var host = Host.CreateDefaultBuilder(args)
                .ConfigureLogging(builder =>
                {
                    builder.ClearProviders();
                    builder.AddSerilog(Log.Logger, dispose: false);
                })
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
            Log.Fatal(ex, "LauncherGo terminated unexpectedly.");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
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

    private static void ConfigureBootstrapLogger()
    {
        Directory.CreateDirectory(LauncherPathHelper.LogDirectory);

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.WithThreadId()
            .Enrich.WithThreadName()
            .WriteTo.File(
                Path.Combine(LauncherPathHelper.LogDirectory, "LauncherGo-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 14,
                shared: true,
                restrictedToMinimumLevel: LogEventLevel.Debug,
                outputTemplate: LogOutputTemplate)
            .CreateLogger();

        Log.Information("LauncherGo bootstrap logger initialized. LogDirectory={LogDirectory}", LauncherPathHelper.LogDirectory);
    }

    private static void RegisterUnhandledExceptionLogging()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, eventArgs) =>
        {
            if (eventArgs.ExceptionObject is Exception exception)
            {
                Log.Fatal(exception, "Unhandled AppDomain exception. IsTerminating={IsTerminating}", eventArgs.IsTerminating);
                return;
            }

            Log.Fatal("Unhandled AppDomain exception object: {ExceptionObject}", eventArgs.ExceptionObject);
        };

        TaskScheduler.UnobservedTaskException += (_, eventArgs) =>
        {
            Log.Error(eventArgs.Exception, "Unobserved task exception.");
            eventArgs.SetObserved();
        };
    }
}
