using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LauncherGo.Ui;

public static class ServiceLocator
{
    private static IHost? _host;

    public static IHost Host
    {
        get => _host ?? throw new InvalidOperationException("Host has not been initialized.");
        set => _host ??= value;
    }

    public static T GetRequiredService<T>() where T : class
    {
        return Host.Services.GetRequiredService<T>();
    }
}
