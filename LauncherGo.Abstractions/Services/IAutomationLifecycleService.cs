using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     执行实例启动/停止过程中的自动化清理和脚本。
/// </summary>
public interface IAutomationLifecycleService
{
    Task ExecuteAsync(
        InstanceProfile profile,
        AutomationScriptTrigger trigger,
        CancellationToken cancellationToken = default);
}
