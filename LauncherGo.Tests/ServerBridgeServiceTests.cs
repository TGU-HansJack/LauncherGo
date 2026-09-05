using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerBridgeServiceTests
{
    [Fact]
    public async Task QueryAsync_WhenDisabled_ReturnsStructuredBridgeDisabledError()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-server-bridge-disabled-");
        try
        {
            var profile = new InstanceProfile { Id = "disabled-test", DirectoryPath = directory.FullName };
            var service = new ServerBridgeService(new UnusedServerConfigService());
            await service.SaveSettingsAsync(profile, new ServerBridgeSettings { Enabled = false }, CancellationToken.None);
            var result = await service.QueryAsync(profile, "server.status");
            Assert.False(result.Success);
            Assert.Equal("bridge-disabled", result.ErrorCode);
        }
        finally
        {
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task SubscribeAsync_SendsSinceAndDispatchesEvents()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-server-bridge-subscribe-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var received = new TaskCompletionSource<ServerBridgeEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                var line = await reader.ReadLineAsync(cts.Token);
                using var request = JsonDocument.Parse(line!);
                Assert.Equal("subscribe", request.RootElement.GetProperty("type").GetString());
                Assert.Equal(40, request.RootElement.GetProperty("since").GetInt64());
                await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":2,\"success\":true}\n"), cts.Token);
                await stream.WriteAsync(Encoding.UTF8.GetBytes("{\"version\":2,\"type\":\"event\",\"sequence\":41,\"event\":\"player.joined\",\"timestampUtc\":\"2026-01-01T00:00:00Z\",\"data\":{\"name\":\"Ada\"}}\n"), cts.Token);
                await received.Task.WaitAsync(cts.Token);
            }, cts.Token);
            var profile = new InstanceProfile { Id = "subscribe-test", DirectoryPath = directory.FullName };
            var service = new ServerBridgeService(new UnusedServerConfigService());
            await service.SaveSettingsAsync(profile, new ServerBridgeSettings { Enabled = true, Port = port, AccessToken = new string('c', 64) }, cts.Token);
            await using var subscription = await service.SubscribeAsync(profile,
                new ServerBridgeSubscriptionOptions { Events = ["player.joined"], Since = 40 },
                value => { received.TrySetResult(value); return Task.CompletedTask; }, cts.Token);
            var evt = await received.Task.WaitAsync(cts.Token);
            Assert.Equal(41, evt.Sequence);
            Assert.Equal("Ada", evt.Data["name"]?.GetValue<string>());
            await server;
        }
        finally
        {
            listener.Stop();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task QueryAsync_SendsProtocolV2AndReturnsStructuredData()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-server-bridge-query-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var server = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(cts.Token);
                await using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, leaveOpen: true);
                var line = await reader.ReadLineAsync(cts.Token);
                using var request = JsonDocument.Parse(line!);
                Assert.Equal(2, request.RootElement.GetProperty("version").GetInt32());
                Assert.Equal("query", request.RootElement.GetProperty("type").GetString());
                Assert.Equal("server.status", request.RootElement.GetProperty("method").GetString());
                var response = Encoding.UTF8.GetBytes("{\"version\":2,\"id\":\"reply\",\"success\":true,\"data\":{\"status\":\"online\"}}\n");
                await stream.WriteAsync(response, cts.Token);
            }, cts.Token);
            var profile = new InstanceProfile { Id = "query-test", DirectoryPath = directory.FullName };
            var service = new ServerBridgeService(new UnusedServerConfigService());
            await service.SaveSettingsAsync(profile, new ServerBridgeSettings { Enabled = true, Port = port, AccessToken = new string('b', 64) }, cts.Token);
            var result = await service.QueryAsync(profile, "server.status", cancellationToken: cts.Token);
            Assert.True(result.Success);
            Assert.Equal("online", result.Data?["status"]?.GetValue<string>());
            await server;
        }
        finally
        {
            listener.Stop();
            directory.Delete(true);
        }
    }

    [Fact]
    public async Task RotateAccessTokenAsync_UpdatesTheRunningBridgeAndPersistsItsReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-server-bridge-rotate-");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var originalToken = new string('a', 64);
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var releaseBridgeConnection = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var receivedRequest = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
            var bridgeTask = RespondToRotationAsync(listener, receivedRequest, releaseBridgeConnection.Task, testCts.Token);
            var profile = new InstanceProfile { Id = "bridge-test", DirectoryPath = directory.FullName };
            var service = new ServerBridgeService(new UnusedServerConfigService());
            await service.SaveSettingsAsync(profile, new ServerBridgeSettings
            {
                Enabled = true,
                Port = port,
                AccessToken = originalToken,
                QueryTimeoutMilliseconds = 5000,
                MaxCommandLength = 4096,
                AllowRelayFallback = true
            }, testCts.Token);

            try
            {
                await service.RotateAccessTokenAsync(profile, testCts.Token);
            }
            catch
            {
                releaseBridgeConnection.TrySetResult();
                await bridgeTask;
                throw;
            }

            releaseBridgeConnection.SetResult();
            await bridgeTask;

            using var request = await receivedRequest.Task.WaitAsync(testCts.Token);
            Assert.Equal("rotate-token", request.RootElement.GetProperty("type").GetString());
            Assert.Equal(originalToken, request.RootElement.GetProperty("token").GetString());
            var replacementToken = request.RootElement.GetProperty("newToken").GetString();
            Assert.NotNull(replacementToken);
            Assert.NotEqual(originalToken, replacementToken);
            Assert.Matches("^[0-9a-f]{64}$", replacementToken);

            var persisted = await service.LoadSettingsAsync(profile, testCts.Token);
            Assert.Equal(replacementToken, persisted.AccessToken);
        }
        finally
        {
            // A failed client read must not leave the fake bridge waiting for the test timeout.
            releaseBridgeConnection.TrySetResult();
            listener.Stop();
            directory.Delete(recursive: true);
        }
    }

    private static async Task RespondToRotationAsync(
        TcpListener listener,
        TaskCompletionSource<JsonDocument> receivedRequest,
        Task releaseConnection,
        CancellationToken cancellationToken)
    {
        using var client = await listener.AcceptTcpClientAsync(cancellationToken);
        await using var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);

        var requestJson = await reader.ReadLineAsync(cancellationToken);
        Assert.False(string.IsNullOrWhiteSpace(requestJson));
        receivedRequest.TrySetResult(JsonDocument.Parse(requestJson));
        var response = Encoding.UTF8.GetBytes("{\"success\":true,\"bridgeVersion\":\"1.0.1\"}\n");
        await stream.WriteAsync(response, cancellationToken);
        await stream.FlushAsync(cancellationToken);
        await releaseConnection.WaitAsync(cancellationToken);
    }

    private sealed class UnusedServerConfigService : IInstanceServerConfigService
    {
        public Task<ServerCommonSettings> LoadServerSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<WorldSettings> LoadWorldSettingsAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveSettingsAsync(
            InstanceProfile profile,
            ServerCommonSettings serverSettings,
            WorldSettings worldSettings,
            IReadOnlyList<WorldRuleValue> rules,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<string> LoadRawJsonAsync(InstanceProfile profile, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task SaveRawJsonAsync(InstanceProfile profile, string json, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task ImportRawJsonAsync(InstanceProfile profile, string jsonFilePath, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}
