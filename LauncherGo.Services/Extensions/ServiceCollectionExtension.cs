using LauncherGo.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LauncherGo.Services.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddLauncherGoServices(this IServiceCollection services)
    {
        services.AddSingleton<ILauncherPreferencesService, LauncherPreferencesService>();
        services.AddSingleton<IServerPackageService, ServerPackageService>();
        services.AddSingleton<IInstanceProfileService, InstanceProfileService>();
        services.AddSingleton<IInstanceSaveService, InstanceSaveService>();
        services.AddSingleton<IServerProcessService, ServerProcessService>();
        return services;
    }
}
