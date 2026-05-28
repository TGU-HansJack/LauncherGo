using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IInstanceServerConfigService
{
    Task<ServerCommonSettings> LoadServerSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<WorldSettings> LoadWorldSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerCommonSettings serverSettings,
        WorldSettings worldSettings,
        IReadOnlyList<WorldRuleValue> rules,
        CancellationToken cancellationToken = default);

    Task<string> LoadRawJsonAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task SaveRawJsonAsync(
        InstanceProfile profile,
        string json,
        CancellationToken cancellationToken = default);

    Task ImportRawJsonAsync(
        InstanceProfile profile,
        string jsonFilePath,
        CancellationToken cancellationToken = default);
}
