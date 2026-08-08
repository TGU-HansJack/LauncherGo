using System.Globalization;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Features;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed partial class ServerPackageService : IServerPackageService
{
    private static readonly TimeSpan CatalogRequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient HttpClient = new();
    private readonly ILauncherPreferencesService _preferencesService;

    public ServerPackageService(ILauncherPreferencesService preferencesService)
    {
        _preferencesService = preferencesService;
    }

    public async Task<IReadOnlyList<ServerDownloadEntry>> GetServerDownloadEntriesAsync(
        CancellationToken cancellationToken = default)
    {
        var preferences = _preferencesService.Load();
        var result = new List<ServerDownloadEntry>();
        var errors = new List<string>();

        var sources = new List<(ServerSourceKind SourceKind, string Url)>
        {
            (ServerSourceKind.Vanilla, preferences.ServerDownloadCatalogUrl)
        };
        if (ServerFeatureFlags.StratumServerSupportEnabled)
        {
            sources.Add((ServerSourceKind.Stratum, preferences.StratumServerDownloadCatalogUrl));
        }

        foreach (var source in sources)
        {
            if (string.IsNullOrWhiteSpace(source.Url))
            {
                continue;
            }

            try
            {
                result.AddRange(await LoadCatalogEntriesAsync(source.Url, source.SourceKind, cancellationToken));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                errors.Add($"{source.SourceKind}: {ex.Message}");
            }
        }

        if (result.Count > 0 || errors.Count == 0)
        {
            return result
                .OrderByDescending(entry => entry.Version, StringComparer.OrdinalIgnoreCase)
                .ThenBy(entry => entry.SourceKind)
                .ToList();
        }

        throw new InvalidOperationException($"服务端版本列表加载失败：{string.Join("; ", errors)}");
    }

    private static async Task<IReadOnlyList<ServerDownloadEntry>> LoadCatalogEntriesAsync(
        string catalogUrl,
        ServerSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CatalogRequestTimeout);

        await using var stream = await HttpClient.GetStreamAsync(catalogUrl, timeoutCts.Token);
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
                var fileSize = FormatCatalogFileSize(artifactObject["filesize"]?.GetValue<string>());
                var cdnUrl = artifactObject["urls"]?["cdn"]?.GetValue<string>();
                var baseVersion = artifactObject["baseVersion"]?.GetValue<string>() ?? string.Empty;

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
                    CdnUrl = cdnUrl,
                    SourceKind = sourceKind,
                    BaseVersion = string.IsNullOrWhiteSpace(baseVersion)
                        ? sourceKind == ServerSourceKind.Stratum
                            ? LauncherWorkspacePathHelper.TryExtractStratumBaseVersion(versionNode.Key) ?? string.Empty
                            : versionNode.Key
                        : baseVersion
                });
            }
        }

        return result;
    }

    internal static string FormatCatalogFileSize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var trimmed = value.Trim();
        if (!long.TryParse(trimmed, NumberStyles.Integer, CultureInfo.InvariantCulture, out var bytes) || bytes < 0)
        {
            return trimmed;
        }

        if (bytes >= 1024L * 1024 * 1024)
        {
            return $"{bytes / 1024d / 1024d / 1024d:F2} GB";
        }

        if (bytes >= 1024L * 1024)
        {
            return $"{bytes / 1024d / 1024d:F1} MB";
        }

        if (bytes >= 1024)
        {
            return $"{bytes / 1024d:F1} KB";
        }

        return $"{bytes} B";
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

        var preferences = _preferencesService.Load();
        if (preferences.EnableChunkedDownloads && preferences.DownloadChunkCount > 1)
        {
            try
            {
                var downloadedInChunks = await TryDownloadByRangesAsync(
                    cdnUrl,
                    fullFilePath,
                    preferences.DownloadChunkCount,
                    progress,
                    cancellationToken);
                if (downloadedInChunks)
                {
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                TryDeletePartialDownload(fullFilePath);
                progress?.Report(0d);
            }
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

    public Task<int> ClearDownloadCacheAsync(
        string serverDirectory,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(serverDirectory))
        {
            return Task.FromResult(0);
        }

        var root = Path.GetFullPath(serverDirectory.Trim());
        if (!Directory.Exists(root))
        {
            return Task.FromResult(0);
        }

        var deleted = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.zip", SearchOption.TopDirectoryOnly))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!IsServerZipFileName(Path.GetFileName(path)))
            {
                continue;
            }

            File.Delete(path);
            deleted++;
        }

        return Task.FromResult(deleted);
    }

    private static async Task<bool> TryDownloadByRangesAsync(
        string url,
        string targetFilePath,
        int requestedChunkCount,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var headRequest = new HttpRequestMessage(HttpMethod.Head, url);
        using var headResponse = await HttpClient.SendAsync(headRequest, cancellationToken);
        if (!headResponse.IsSuccessStatusCode)
        {
            return false;
        }

        var contentLength = headResponse.Content.Headers.ContentLength;
        var supportsRanges = headResponse.Headers.AcceptRanges.Any(x => x.Equals("bytes", StringComparison.OrdinalIgnoreCase));
        if (contentLength is not > 0 || !supportsRanges)
        {
            return false;
        }

        var chunkCount = Math.Clamp(requestedChunkCount, 2, 32);
        chunkCount = (int)Math.Min(chunkCount, contentLength.Value);

        await using (var file = new FileStream(targetFilePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            file.SetLength(contentLength.Value);
        }

        long totalRead = 0;
        var chunkSize = contentLength.Value / chunkCount;
        var tasks = new List<Task>(chunkCount);
        for (var index = 0; index < chunkCount; index++)
        {
            var start = index * chunkSize;
            var end = index == chunkCount - 1
                ? contentLength.Value - 1
                : start + chunkSize - 1;
            tasks.Add(DownloadRangeAsync(url, targetFilePath, start, end, contentLength.Value, value =>
            {
                var read = Interlocked.Add(ref totalRead, value);
                progress?.Report((double)read / contentLength.Value);
            }, cancellationToken));
        }

        await Task.WhenAll(tasks);
        progress?.Report(1d);
        return true;
    }

    private static async Task DownloadRangeAsync(
        string url,
        string targetFilePath,
        long start,
        long end,
        long contentLength,
        Action<long> reportBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start, end);
        using var response = await HttpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.PartialContent)
        {
            throw new InvalidOperationException("服务器不支持分片下载。");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var destination = new FileStream(targetFilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
        destination.Seek(start, SeekOrigin.Begin);

        var buffer = new byte[1024 * 128];
        long written = 0;
        var expectedLength = end - start + 1;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)) > 0)
        {
            await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            written += read;
            reportBytes(read);
            if (written > expectedLength || written > contentLength)
            {
                throw new InvalidOperationException("分片下载返回的数据长度异常。");
            }
        }
    }

    private static void TryDeletePartialDownload(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Fall back to overwriting the file with the normal downloader.
        }
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
            throw new InvalidOperationException(ServerFeatureFlags.StratumServerSupportEnabled
                ? "仅支持导入官方或 Stratum Windows 服务端压缩包（vs_server_win-x64_*.zip / stratum-*-win-x64.zip）。"
                : "仅支持导入官方 Windows 服务端压缩包（vs_server_win-x64_*.zip）。");
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

        var version = LauncherWorkspacePathHelper.TryExtractVersionFromPackageName(fileName);
        return !string.IsNullOrWhiteSpace(version) &&
               (ServerFeatureFlags.StratumServerSupportEnabled || LauncherWorkspacePathHelper.TryExtractStratumBaseVersion(version) is null);
    }
}
