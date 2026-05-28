using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IInstanceSaveService
{
    Task<IReadOnlyList<SaveFileEntry>> GetSavesAsync(
        InstanceProfile? profile = null,
        CancellationToken cancellationToken = default);

    Task<string> ImportSaveAsync(
        InstanceProfile profile,
        string sourceFilePath,
        CancellationToken cancellationToken = default);

    Task SetActiveSaveAsync(
        InstanceProfile profile,
        string saveFilePath,
        CancellationToken cancellationToken = default);

    Task<int> DeleteSavesAsync(
        IReadOnlyCollection<string> saveFilePaths,
        CancellationToken cancellationToken = default);
}
