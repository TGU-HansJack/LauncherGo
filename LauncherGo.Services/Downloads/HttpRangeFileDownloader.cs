using System.Net;
using System.Net.Http.Headers;
using Microsoft.Win32.SafeHandles;

namespace LauncherGo.Services.Downloads;

public sealed class HttpRangeFileDownloader
{
    private const int BufferSize = 128 * 1024;
    private readonly HttpClient _client;

    public HttpRangeFileDownloader(HttpClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async Task<HttpFileDownloadResult> DownloadAsync(
        string url,
        string targetPath,
        HttpRangeFileDownloadOptions? options = null,
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("Download URL must be absolute.", nameof(url));
        }

        if (string.IsNullOrWhiteSpace(targetPath))
        {
            throw new ArgumentException("Target file path cannot be empty.", nameof(targetPath));
        }

        var fullPath = Path.GetFullPath(targetPath);
        var parent = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            Directory.CreateDirectory(parent);
        }

        var resolvedOptions = options ?? new HttpRangeFileDownloadOptions();
        var segmentCount = Math.Clamp(resolvedOptions.SegmentCount, 2, 32);
        var maxConcurrentDownloads = Math.Clamp(resolvedOptions.MaxConcurrentDownloads, 1, 32);
        if (resolvedOptions.EnableRangeRequests)
        {
            try
            {
                var contentLength = await TryGetRangeContentLengthAsync(uri, cancellationToken).ConfigureAwait(false);
                if (contentLength is > 0)
                {
                    await DownloadByRangesAsync(
                        uri,
                        fullPath,
                        contentLength.Value,
                        segmentCount,
                        maxConcurrentDownloads,
                        progress,
                        cancellationToken)
                        .ConfigureAwait(false);
                    return new HttpFileDownloadResult(contentLength.Value, UsedRanges: true);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception)
            {
                TryDeletePartialDownload(fullPath);
                progress?.Report(0d);
            }
        }

        var bytesDownloaded = await DownloadSingleAsync(uri, fullPath, progress, cancellationToken).ConfigureAwait(false);
        return new HttpFileDownloadResult(bytesDownloaded, UsedRanges: false);
    }

    private async Task<long?> TryGetRangeContentLengthAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
            var supportsRanges = response.Headers.AcceptRanges.Any(value =>
                value.Equals("bytes", StringComparison.OrdinalIgnoreCase));
            var contentLength = response.Content.Headers.ContentLength;
            if (response.IsSuccessStatusCode && supportsRanges && contentLength is > 0)
            {
                return contentLength.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Some download sources and proxies reject HEAD requests.
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, uri);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.PartialContent &&
                response.Content.Headers.ContentRange?.From == 0 &&
                response.Content.Headers.ContentRange?.To == 0 &&
                response.Content.Headers.ContentRange.Length is > 0)
            {
                return response.Content.Headers.ContentRange.Length.Value;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // A normal single-connection request is the compatible fallback.
        }

        return null;
    }

    private async Task DownloadByRangesAsync(
        Uri uri,
        string targetPath,
        long contentLength,
        int requestedSegmentCount,
        int requestedMaxConcurrentDownloads,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        var segmentCount = (int)Math.Min(requestedSegmentCount, contentLength);
        var maxConcurrentDownloads = Math.Min(requestedMaxConcurrentDownloads, segmentCount);
        var segmentSize = contentLength / segmentCount;
        using var fileHandle = File.OpenHandle(
            targetPath,
            FileMode.Create,
            FileAccess.ReadWrite,
            FileShare.None,
            FileOptions.Asynchronous | FileOptions.RandomAccess,
            preallocationSize: contentLength);
        RandomAccess.SetLength(fileHandle, contentLength);

        long totalRead = 0;
        await Parallel.ForEachAsync(
            Enumerable.Range(0, segmentCount),
            new ParallelOptions
            {
                MaxDegreeOfParallelism = maxConcurrentDownloads,
                CancellationToken = cancellationToken
            },
            async (index, token) =>
            {
                var start = index * segmentSize;
                var end = index == segmentCount - 1
                    ? contentLength - 1
                    : start + segmentSize - 1;
                await DownloadRangeAsync(uri, fileHandle, start, end, contentLength, read =>
                {
                    var completed = Interlocked.Add(ref totalRead, read);
                    progress?.Report((double)completed / contentLength);
                }, token).ConfigureAwait(false);
            }).ConfigureAwait(false);

        progress?.Report(1d);
    }

    private async Task DownloadRangeAsync(
        Uri uri,
        SafeFileHandle fileHandle,
        long start,
        long end,
        long contentLength,
        Action<int> reportBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await _client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.PartialContent ||
            response.Content.Headers.ContentRange is not { From: var actualStart, To: var actualEnd, Length: var actualLength } ||
            actualStart != start ||
            actualEnd != end ||
            actualLength != contentLength)
        {
            throw new InvalidOperationException("The download source does not support reliable range requests.");
        }

        var expectedLength = end - start + 1;
        var written = 0L;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[BufferSize];
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (written + read > expectedLength)
            {
                throw new InvalidDataException("A download range returned too much data.");
            }

            await RandomAccess.WriteAsync(fileHandle, buffer.AsMemory(0, read), start + written, cancellationToken)
                .ConfigureAwait(false);
            written += read;
            reportBytes(read);
        }

        if (written != expectedLength)
        {
            throw new InvalidDataException("A download range ended before all bytes were received.");
        }
    }

    private async Task<long> DownloadSingleAsync(
        Uri uri,
        string targetPath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var contentLength = response.Content.Headers.ContentLength;
        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(targetPath, FileMode.Create, FileAccess.Write, FileShare.None);

        var buffer = new byte[BufferSize];
        long totalRead = 0;
        int read;
        while ((read = await source.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            totalRead += read;
            if (contentLength is > 0)
            {
                progress?.Report((double)totalRead / contentLength.Value);
            }
        }

        progress?.Report(1d);
        return totalRead;
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
        catch (Exception)
        {
            // The fallback request reports a useful error if it cannot replace the partial file.
        }
    }
}

public sealed class HttpRangeFileDownloadOptions
{
    public bool EnableRangeRequests { get; init; } = true;

    public int SegmentCount { get; init; } = 4;

    public int MaxConcurrentDownloads { get; init; } = 4;
}

public readonly record struct HttpFileDownloadResult(long BytesDownloaded, bool UsedRanges);
