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

    Task<string> CreateSaveAsync(
        InstanceProfile profile,
        string saveName,
        CancellationToken cancellationToken = default);

    Task<string> BackupActiveSaveAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default);

    Task<ManagedBackupResult> BackupManagedActiveSaveAsync(
        InstanceProfile profile,
        string backupFileName,
        int retentionCount,
        CancellationToken cancellationToken = default);

    Task<ManagedBackupResult> FinalizeManagedBackupAsync(
        InstanceProfile profile,
        string backupFilePath,
        int retentionCount,
        CancellationToken cancellationToken = default);

    Task<SaveCompressionResult?> CompressBackupAsync(
        InstanceProfile profile,
        string backupFilePath,
        CancellationToken cancellationToken = default);

    Task<int> CompressExistingBackupsAsync(
        CancellationToken cancellationToken = default);

    Task SetActiveSaveAsync(
        InstanceProfile profile,
        string saveFilePath,
        CancellationToken cancellationToken = default);

    Task<SavePathInspection> InspectSavePathAsync(
        InstanceProfile profile,
        string? candidateSaveFilePath = null,
        CancellationToken cancellationToken = default);

    Task<int> DeleteSavesAsync(
        IReadOnlyCollection<string> saveFilePaths,
        CancellationToken cancellationToken = default);

    Task<int> DeleteSavesAsync(
        InstanceProfile profile,
        IReadOnlyCollection<string> saveFilePaths,
        CancellationToken cancellationToken = default);
}
