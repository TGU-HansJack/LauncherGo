using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     自动化设置服务
/// </summary>
public interface IAutomationSettingsService
{
    Task<AutomationSettings> LoadAsync(CancellationToken cancellationToken = default);

    Task<AutomationSettings> LoadAsync(InstanceProfile profile, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AutomationSettings>> LoadAllAsync(
        IReadOnlyList<InstanceProfile> profiles,
        CancellationToken cancellationToken = default);

    Task SaveAsync(AutomationSettings settings, CancellationToken cancellationToken = default);

    Task SaveAsync(InstanceProfile profile, AutomationSettings settings, CancellationToken cancellationToken = default);

    string GetSettingsPath(InstanceProfile profile);
}

