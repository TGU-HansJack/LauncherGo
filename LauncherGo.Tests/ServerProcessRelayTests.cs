using System.IO.Pipes;
using System.Text.Json;
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

    [Fact]
    public async Task Version2Relay_RejectsUnauthenticatedCommand()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var forwarded = false;
        var loopTask = StartRelayLoop(
            pipeName,
            (_, _) =>
            {
                forwarded = true;
                return Task.CompletedTask;
            },
            testCts.Token,
            CreateAuthenticatedState(pipeName));

        try
        {
            var response = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "/announce blocked",
                testCts.Token);

            Assert.False(response.Success);
            Assert.Equal("Relay instance authentication failed.", response.Error);
            Assert.False(forwarded);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task Discover_ReturnsLiveIdentity_AndAuthenticatedCommandSucceeds()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = CreateAuthenticatedState(pipeName);
        var forwardedCommand = string.Empty;
        var loopTask = StartRelayLoop(
            pipeName,
            (command, _) =>
            {
                forwardedCommand = command;
                return Task.CompletedTask;
            },
            testCts.Token,
            state);

        try
        {
            var discovery = await ServerRelayClient.DiscoverAsync(pipeName, testCts.Token);
            Assert.True(discovery.Success, discovery.Error);
            Assert.Equal(state.InstanceId, discovery.State?.InstanceId);
            Assert.Equal(state.ControlToken, discovery.State?.ControlToken);

            var response = await ServerRelayClient.SendCommandAsync(
                discovery.State!,
                "/announce recovered",
                testCts.Token);

            Assert.True(response.Success, response.Error);
            Assert.Equal("/announce recovered", forwardedCommand);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task AuthenticatedPing_DoesNotDependOnCallersSynchronizationContext()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = CreateAuthenticatedState(pipeName);
        var loopTask = StartRelayLoop(
            pipeName,
            (_, _) => Task.CompletedTask,
            testCts.Token,
            state);
        var completed = new TaskCompletionSource<ServerRelayResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new NonPumpingSynchronizationContext());
            try
            {
                completed.TrySetResult(
                    ServerRelayClient.PingAsync(state, testCts.Token).GetAwaiter().GetResult());
            }
            catch (Exception ex)
            {
                completed.TrySetException(ex);
            }
        })
        {
            IsBackground = true
        };

        try
        {
            thread.Start();
            var response = await completed.Task.WaitAsync(TimeSpan.FromSeconds(3), testCts.Token);
            Assert.True(response.Success, response.Error);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task WrongToken_CannotExecuteCommand()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var state = CreateAuthenticatedState(pipeName);
        var forwarded = false;
        var loopTask = StartRelayLoop(
            pipeName,
            (_, _) =>
            {
                forwarded = true;
                return Task.CompletedTask;
            },
            testCts.Token,
            state);

        try
        {
            var invalidState = CreateAuthenticatedState(pipeName);
            invalidState.InstanceId = state.InstanceId;
            var response = await ServerRelayClient.SendCommandAsync(
                invalidState,
                "/announce blocked",
                testCts.Token);

            Assert.False(response.Success);
            Assert.False(forwarded);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task Version2Relay_WithMissingIdentity_RejectsCommand()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var forwarded = false;
        var state = new ServerRelayState
        {
            SchemaVersion = ServerRelayProtocol.CurrentSchemaVersion,
            PipeName = pipeName,
            RelayProcessId = Environment.ProcessId,
            ServerProcessId = Environment.ProcessId
        };
        var loopTask = StartRelayLoop(
            pipeName,
            (_, _) =>
            {
                forwarded = true;
                return Task.CompletedTask;
            },
            testCts.Token,
            state);

        try
        {
            var response = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "/announce blocked",
                testCts.Token);

            Assert.False(response.Success);
            Assert.Equal("Relay instance authentication failed.", response.Error);
            Assert.False(forwarded);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public async Task StateCacheWriteFailure_DoesNotBreakControlChannel()
    {
        var pipeName = CreatePipeName();
        using var testCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var forwardedCommand = string.Empty;
        var loopTask = ServerProcessRelay.RunPipeLoopAsync(
            pipeName,
            new ServerRelayState
            {
                SchemaVersion = 1,
                PipeName = pipeName,
                RelayProcessId = Environment.ProcessId,
                ServerProcessId = Environment.ProcessId
            },
            isProcessTerminated: () => false,
            getProcessId: () => Environment.ProcessId,
            writeConsoleCommand: (command, _) =>
            {
                forwardedCommand = command;
                return Task.CompletedTask;
            },
            TestTimeouts,
            testCts.Token,
            stateChanged: () => throw new IOException("Simulated unavailable state cache."));

        try
        {
            var ping = await ServerRelayClient.PingAsync(pipeName, testCts.Token);
            Assert.True(ping.Success, ping.Error);

            var command = await ServerRelayClient.SendCommandAsync(
                pipeName,
                "/announce cache-independent",
                testCts.Token);

            Assert.True(command.Success, command.Error);
            Assert.Equal("/announce cache-independent", forwardedCommand);
        }
        finally
        {
            await StopRelayLoopAsync(testCts, loopTask);
        }
    }

    [Fact]
    public void MissingSchemaVersion_DeserializesAsLegacyState()
    {
        var state = JsonSerializer.Deserialize<ServerRelayState>(
            "{}",
            ServerRelayProtocol.JsonOptions);

        Assert.NotNull(state);
        Assert.Equal(0, state.SchemaVersion);
    }

    private static Task StartRelayLoop(
        string pipeName,
        Func<string, CancellationToken, Task> writeCommand,
        CancellationToken cancellationToken,
        ServerRelayState? state = null)
    {
        return ServerProcessRelay.RunPipeLoopAsync(
            pipeName,
            state ?? new ServerRelayState
            {
                SchemaVersion = 1,
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

    private static ServerRelayState CreateAuthenticatedState(string pipeName)
    {
        return new ServerRelayState
        {
            SchemaVersion = ServerRelayProtocol.CurrentSchemaVersion,
            PipeName = pipeName,
            InstanceId = Guid.NewGuid().ToString("N"),
            ControlToken = Guid.NewGuid().ToString("N"),
            RelayProcessId = Environment.ProcessId,
            ServerProcessId = Environment.ProcessId,
            ProfileId = Guid.NewGuid().ToString("N")
        };
    }

    private sealed class NonPumpingSynchronizationContext : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state)
        {
        }
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
