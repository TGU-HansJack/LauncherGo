using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed partial class ServerPackageService : IServerPackageService
{
    private const string StableUnstableApiUrl = "https://api.vintagestory.at/stable-unstable.json";
    private static readonly TimeSpan CatalogRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();

    public async Task<IReadOnlyList<ServerDownloadEntry>> GetServerDownloadEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CatalogRequestTimeout);

        await using var stream = await HttpClient.GetStreamAsync(StableUnstableApiUrl, timeoutCts.Token);
        var rootNode = await JsonNode.ParseAsync(stream, cancellationToken: timeoutCts.Token);
        if (rootNode is not JsonObject rootObject)
        {
            return [];
        }

        var result = new List<ServerDownloadEntry>();
        foreach (var versionNode in rootObject)
        {
            if (versionNode.Value is not JsonObject versionObject)
            {
                continue;
            }

            foreach (var platformNode in versionObject)
            {
                var platformKey = platformNode.Key;
                if (!platformKey.Contains("windows", StringComparison.OrdinalIgnoreCase) ||
                    !platformKey.Contains("server", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (platformNode.Value is not JsonObject artifactObject)
                {
                    continue;
                }

                var fileName = artifactObject["filename"]?.GetValue<string>();
                var fileSize = artifactObject["filesize"]?.GetValue<string>() ?? string.Empty;
                var cdnUrl = artifactObject["urls"]?["cdn"]?.GetValue<string>();

                if (string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(cdnUrl))
                {
                    continue;
                }

                result.Add(new ServerDownloadEntry
                {
                    Version = versionNode.Key,
                    Platform = platformKey,
                    FileSize = fileSize,
                    FileName = fileName,
                    CdnUrl = cdnUrl
                });
            }
        }

        return result;
    }

    public async Task DownloadByCdnAsync(
        string cdnUrl,
        string targetFilePath,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(cdnUrl))
        {
            throw new ArgumentException("Download URL cannot be empty.", nameof(cdnUrl));
        }

        if (string.IsNullOrWhiteSpace(targetFilePath))
        {
            throw new ArgumentException("Target file path cannot be empty.", nameof(targetFilePath));
        }

        var fullFilePath = Path.GetFullPath(targetFilePath);
        var parent = Path.GetDirectoryName(fullFilePath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        using var response = await HttpClient.GetAsync(cdnUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var contentLength = response.Content.Headers.ContentLength;

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(fullFilePath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[1024 * 128];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            totalRead += read;

            if (contentLength is > 0)
            {
                progress?.Report((double)totalRead / contentLength.Value);
            }
        }

        progress?.Report(1d);
    }

    public async Task<string> ImportServerPackageAsync(
        string sourceFilePath,
        string targetDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new InvalidOperationException("导入文件路径不能为空。");
        }

        if (string.IsNullOrWhiteSpace(targetDirectory))
        {
            throw new InvalidOperationException("服务端目录不能为空。");
        }

        var sourceFullPath = Path.GetFullPath(sourceFilePath.Trim());
        if (!File.Exists(sourceFullPath))
        {
            throw new InvalidOperationException($"导入文件不存在：{sourceFullPath}");
        }

        var fileName = Path.GetFileName(sourceFullPath);
        if (!IsServerZipFileName(fileName))
        {
            throw new InvalidOperationException("仅支持导入服务端压缩包（vs_server_win-x64_*.zip）。");
        }

        var targetRoot = Path.GetFullPath(targetDirectory.Trim());
        Directory.CreateDirectory(targetRoot);

        var targetFilePath = Path.Combine(targetRoot, fileName);

        await using var source = new FileStream(sourceFullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var destination = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken);

        return targetFilePath;
    }

    private static bool IsServerZipFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        return ServerPackageFileNameRegex().IsMatch(fileName.Trim());
    }

    [GeneratedRegex(@"^vs_server_win-x64_.+\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex ServerPackageFileNameRegex();
}
