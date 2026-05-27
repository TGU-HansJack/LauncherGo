using LauncherGo.Ui.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LauncherGo.Ui.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddLauncherGoUi(this IServiceCollection services)
    {
        services.AddSingleton<FirstLaunchGuideWindow>();
        return services;
    }
}
