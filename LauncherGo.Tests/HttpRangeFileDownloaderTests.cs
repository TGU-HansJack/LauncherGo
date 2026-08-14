using System.Net;
using System.Net.Http.Headers;
using LauncherGo.Services.Downloads;
using Xunit;

namespace LauncherGo.Tests;

public sealed class HttpRangeFileDownloaderTests
{
    [Fact]
    public async Task DownloadAsync_ReassemblesAllRanges()
    {
        var expected = Enumerable.Range(0, 4097).Select(value => (byte)(value % 251)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"LauncherGo-range-test-{Guid.NewGuid():N}.bin");
        using var client = new HttpClient(new RangeDownloadHandler(expected));
        var downloader = new HttpRangeFileDownloader(client);

        try
        {
            var result = await downloader.DownloadAsync(
                "https://example.test/update.bin",
                path,
                new HttpRangeFileDownloadOptions { SegmentCount = 4 });

            Assert.True(result.UsedRanges);
            Assert.Equal(expected.Length, result.BytesDownloaded);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_FallsBackToSingleConnectionWhenRangesAreUnsupported()
    {
        var expected = new byte[] { 1, 2, 3 };
        var path = Path.Combine(Path.GetTempPath(), $"LauncherGo-range-test-{Guid.NewGuid():N}.bin");
        using var client = new HttpClient(new NoRangeDownloadHandler(expected));
        var downloader = new HttpRangeFileDownloader(client);

        try
        {
            var result = await downloader.DownloadAsync(
                "https://example.test/update.bin",
                path,
                new HttpRangeFileDownloadOptions { SegmentCount = 4 });

            Assert.False(result.UsedRanges);
            Assert.Equal(expected.Length, result.BytesDownloaded);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_UsesSingleConnectionWhenRangesAreDisabled()
    {
        var expected = new byte[] { 1, 2, 3 };
        var path = Path.Combine(Path.GetTempPath(), $"LauncherGo-range-test-{Guid.NewGuid():N}.bin");
        var handler = new NoRangeDownloadHandler(expected);
        using var client = new HttpClient(handler);
        var downloader = new HttpRangeFileDownloader(client);

        try
        {
            var result = await downloader.DownloadAsync(
                "https://example.test/update.bin",
                path,
                new HttpRangeFileDownloadOptions
                {
                    EnableRangeRequests = false,
                    SegmentCount = 4
                });

            Assert.False(result.UsedRanges);
            Assert.Equal(0, handler.RangeRequestCount);
            Assert.Equal(0, handler.HeadRequestCount);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    [Fact]
    public async Task DownloadAsync_LimitsConcurrentRangeRequests()
    {
        var expected = Enumerable.Range(0, 8192).Select(value => (byte)(value % 251)).ToArray();
        var path = Path.Combine(Path.GetTempPath(), $"LauncherGo-range-test-{Guid.NewGuid():N}.bin");
        var handler = new DelayedRangeDownloadHandler(expected);
        using var client = new HttpClient(handler);
        var downloader = new HttpRangeFileDownloader(client);

        try
        {
            var result = await downloader.DownloadAsync(
                "https://example.test/update.bin",
                path,
                new HttpRangeFileDownloadOptions
                {
                    SegmentCount = 4,
                    MaxConcurrentDownloads = 2
                });

            Assert.True(result.UsedRanges);
            Assert.Equal(2, handler.MaximumConcurrentRangeRequestCount);
            Assert.Equal(expected, await File.ReadAllBytesAsync(path));
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private sealed class RangeDownloadHandler(byte[] content) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                response.Headers.AcceptRanges.Add("bytes");
                response.Content.Headers.ContentLength = content.Length;
                return Task.FromResult(response);
            }

            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = Assert.IsType<long>(range.From);
            var end = Assert.IsType<long>(range.To);
            var chunk = content[(int)start..((int)end + 1)];
            var responseContent = new ByteArrayContent(chunk);
            responseContent.Headers.ContentLength = chunk.Length;
            responseContent.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.Length);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                Content = responseContent
            });
        }
    }

    private sealed class NoRangeDownloadHandler(byte[] content) : HttpMessageHandler
    {
        public int HeadRequestCount { get; private set; }

        public int RangeRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                HeadRequestCount++;
            }

            if (request.Headers.Range is not null)
            {
                RangeRequestCount++;
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(content)
            };
            response.Content.Headers.ContentLength = content.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class DelayedRangeDownloadHandler(byte[] content) : HttpMessageHandler
    {
        private int _activeRangeRequestCount;
        private int _maximumConcurrentRangeRequestCount;

        public int MaximumConcurrentRangeRequestCount => Volatile.Read(ref _maximumConcurrentRangeRequestCount);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>())
                };
                response.Headers.AcceptRanges.Add("bytes");
                response.Content.Headers.ContentLength = content.Length;
                return response;
            }

            var range = Assert.Single(request.Headers.Range!.Ranges);
            var start = Assert.IsType<long>(range.From);
            var end = Assert.IsType<long>(range.To);
            var active = Interlocked.Increment(ref _activeRangeRequestCount);
            UpdateMaximum(active);
            try
            {
                await Task.Delay(20, cancellationToken);
                var chunk = content[(int)start..((int)end + 1)];
                var responseContent = new ByteArrayContent(chunk);
                responseContent.Headers.ContentLength = chunk.Length;
                responseContent.Headers.ContentRange = new ContentRangeHeaderValue(start, end, content.Length);
                return new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = responseContent
                };
            }
            finally
            {
                Interlocked.Decrement(ref _activeRangeRequestCount);
            }
        }

        private void UpdateMaximum(int current)
        {
            int observed;
            do
            {
                observed = Volatile.Read(ref _maximumConcurrentRangeRequestCount);
                if (observed >= current)
                {
                    return;
                }
            }
            while (Interlocked.CompareExchange(ref _maximumConcurrentRangeRequestCount, current, observed) != observed);
        }
    }
}
