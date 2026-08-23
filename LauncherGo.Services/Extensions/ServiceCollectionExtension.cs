using LauncherGo.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LauncherGo.Services.Extensions;

public static class ServiceCollectionExtension
{
    public static IServiceCollection AddLauncherGoServices(this IServiceCollection services)
    {
        services.AddSingleton<ILauncherPreferencesService, LauncherPreferencesService>();
        services.AddSingleton<IServerPackageService, ServerPackageService>();
        services.AddSingleton<ILauncherUpdateService, LauncherUpdateService>();
        services.AddSingleton<IInstanceProfileService, InstanceProfileService>();
        services.AddSingleton<IInstanceSaveService, InstanceSaveService>();
        services.AddSingleton<IInstanceServerConfigService, InstanceServerConfigService>();
        services.AddSingleton<IServerProcessService, ServerProcessService>();
        services.AddSingleton<IServerTransport, LocalServerTransport>();
        services.AddSingleton<ILogTailService, LogTailService>();
        services.AddSingleton<IAutomationSettingsService, AutomationSettingsService>();
        services.AddSingleton<IAutomationLifecycleService, AutomationLifecycleService>();
        services.AddSingleton<IAutomationService, AutomationService>();
        services.AddSingleton<IFrpService, FrpService>();
        services.AddSingleton<IThirdPartyFrpcService, ThirdPartyFrpcService>();
        services.AddSingleton<IEasyTierService, EasyTierService>();
        services.AddSingleton<ITcpGatewayService, TcpGatewayService>();
        services.AddSingleton<IGatewayRedirectModService, GatewayRedirectModService>();
        services.AddSingleton<IInstanceModService, InstanceModService>();
        services.AddSingleton<IServerAuthService, ServerAuthService>();
        services.AddSingleton<ICommandBridgeService, CommandBridgeService>();
        services.AddSingleton<IOsqSnapshotCacheService, OsqSnapshotCacheService>();
        services.AddSingleton<IOpenServerQueryService, OpenServerQueryService>();
        services.AddSingleton<Vs2QQProcessService>();
        services.AddSingleton<IRobotService, RobotService>();
        return services;
    }
}
