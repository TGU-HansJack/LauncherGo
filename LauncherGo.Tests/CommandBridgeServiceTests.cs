using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class CommandBridgeServiceTests
{
    [Fact]
    public async Task RotateAccessTokenAsync_UpdatesTheRunningBridgeAndPersistsItsReplacement()
    {
        var directory = Directory.CreateTempSubdirectory("launchergo-command-bridge-");
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
            var service = new CommandBridgeService(new UnusedServerConfigService());
            await service.SaveSettingsAsync(profile, new CommandBridgeSettings
            {
                Enabled = true,
                Port = port,
                AccessToken = originalToken,
                CommandTimeoutMilliseconds = 5000,
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
