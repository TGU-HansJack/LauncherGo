using LauncherGo.Ui.Views;
using LauncherGo.Abstractions.Services.I18n;
using LauncherGo.Ui.Services.I18n;
using Microsoft.Extensions.DependencyInjection;

namespace LauncherGo.Ui.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddLauncherGoUi(this IServiceCollection services)
    {
        services.AddSingleton<ILocalizationService, LocalizationService>();
        services.AddSingleton<FirstLaunchGuideWindow>();
        services.AddSingleton<LauncherMainWindow>();
        return services;
    }
}
