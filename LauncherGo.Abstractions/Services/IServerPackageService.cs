using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IServerPackageService
{
    Task<IReadOnlyList<ServerDownloadEntry>> GetServerDownloadEntriesAsync(
        CancellationToken cancellationToken = default);

    Task DownloadByCdnAsync(
        string cdnUrl,
        string targetFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default);

    Task<int> ClearDownloadCacheAsync(
        string serverDirectory,
        CancellationToken cancellationToken = default);

    Task<string> ImportServerPackageAsync(
        string sourceFilePath,
        string targetDirectory,
        CancellationToken cancellationToken = default);
}
