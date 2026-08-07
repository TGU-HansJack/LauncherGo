using System.IO.Pipes;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerProcessRelayTests
{
    private static readonly ServerRelayTimeouts TestTimeouts = new(
        RequestRead: TimeSpan.FromMilliseconds(100),
        CommandForward: TimeSpan.FromMilliseconds(150),
        ResponseWrite: TimeSpan.FromMilliseconds(100));

    [Fact]
    public async Task SilentClient_DoesNotBlockFollowingPing()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var loopTask = StartRelayLoop(
            pipeName,
            (_, _) => Task.CompletedTask,
            testCts.Token);

        try
        {
            await using var silentClient = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous);
            await silentClient.ConnectAsync(testCts.Token);

            await Task.Delay(TimeSpan.FromMilliseconds(250), testCts.Token);

            var response = await ServerRelayClient.PingAsync(pipeName, testCts.Token);

            Assert.True(response.Success, response.Error);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task StalledCommandWrite_DoesNotBlockPing_AndRecoversWhenWriteCompletes()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var releaseWrite = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeAttempts = 0;

        Task WriteCommandAsync(string _, CancellationToken __)
        {
            Interlocked.Increment(ref writeAttempts);
            releaseWrite.Task.GetAwaiter().GetResult();
            return Task.CompletedTask;
        }

        var loopTask = StartRelayLoop(pipeName, WriteCommandAsync, testCts.Token);

        try
        {
            var timedOut = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "/announce first",
                testCts.Token);

            Assert.False(timedOut.Success);
            Assert.Equal("Relay command forwarding timed out.", timedOut.Error);

            var pingWhileWriteIsStalled = await ServerRelayClient.PingAsync(pipeName, testCts.Token);
            Assert.True(pingWhileWriteIsStalled.Success, pingWhileWriteIsStalled.Error);

            releaseWrite.SetResult();

            var recovered = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "/announce second",
                testCts.Token);

            Assert.True(recovered.Success, recovered.Error);
            Assert.Equal(2, Volatile.Read(ref writeAttempts));
        }
        finally
        {
            releaseWrite.TrySetResult();
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task Command_IsNormalizedBeforeItIsForwarded()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var forwardedCommand = string.Empty;
        var loopTask = StartRelayLoop(
            pipeName,
            (command, _) =>
            {
                forwardedCommand = command;
                return Task.CompletedTask;
            },
            testCts.Token);

        try
        {
            var response = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "announce hello",
                testCts.Token);

            Assert.True(response.Success, response.Error);
            Assert.Equal("/announce hello", forwardedCommand);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task RestartingRelay_PingSucceeds_ButCommandsAreRejected()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = new ServerRelayState
        {
            PipeName = pipeName,
            RelayProcessId = Environment.ProcessId,
            IsRestarting = true
        };
        var loopTask = ServerProcessRelay.RunPipeLoopAsync(
            pipeName,
            state,
            isProcessTerminated: () => true,
            getProcessId: () => null,
            writeConsoleCommand: (_, _) => Task.CompletedTask,
            TestTimeouts,
            testCts.Token);

        try
        {
            var ping = await ServerRelayClient.PingAsync(pipeName, testCts.Token);
            Assert.True(ping.Success, ping.Error);
            Assert.True(ping.State?.IsRestarting);

            var command = await ServerRelayClient.SendCommandAsync(pipeName, "/announce later", testCts.Token);
            Assert.False(command.Success);
            Assert.Equal("Server process is restarting.", command.Error);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task StopCommand_IsReportedToRelaySupervisor()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var observedCommand = string.Empty;
        var loopTask = ServerProcessRelay.RunPipeLoopAsync(
            pipeName,
            new ServerRelayState
            {
                PipeName = pipeName,
                RelayProcessId = Environment.ProcessId,
                ServerProcessId = Environment.ProcessId
            },
            isProcessTerminated: () => false,
            getProcessId: () => Environment.ProcessId,
            writeConsoleCommand: (_, _) => Task.CompletedTask,
            TestTimeouts,
            testCts.Token,
            commandObserved: command => observedCommand = command);

        try
        {
            var response = await ServerRelayClient.SendCommandAsync(pipeName, "stop", testCts.Token);

            Assert.True(response.Success, response.Error);
            Assert.Equal("/stop", observedCommand);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    private static Task StartRelayLoop(
        string pipeName,
        Func<string, CancellationToken, Task> writeCommand,
        CancellationToken cancellationToken)
    {
        return ServerProcessRelay.RunPipeLoopAsync(
            pipeName,
            new ServerRelayState
            {
                PipeName = pipeName,
                RelayProcessId = Environment.ProcessId,
                ServerProcessId = Environment.ProcessId
            },
            isProcessTerminated: () => false,
            getProcessId: () => Environment.ProcessId,
            writeCommand,
            TestTimeouts,
            cancellationToken);
    }

    private static async Task StopRelayLoopAsync(
        CancellationTokenSource cancellationTokenSource,
        Task loopTask)
    {
        await cancellationTokenSource.CancelAsync();
        await loopTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static string CreatePipeName()
    {
        return $"LauncherGo-relay-test-{Guid.NewGuid():N}";
    }
}
