using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace LauncherGo.Services;

public static class ServerProcessRelay
{
    private const int MaxConsecutiveCrashRestarts = 3;
    private static readonly TimeSpan CrashRestartDelay = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StableRuntimeWindow = TimeSpan.FromSeconds(30);

    private static readonly JsonSerializerOptions StateJsonOptions = new(ServerRelayProtocol.JsonOptions)
    {
        WriteIndented = true
    };

    public static bool IsRelayInvocation(string[] args)
    {
        return args.Any(arg => arg.Equals(ServerRelayProtocol.LauncherArgument, StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
    {
        var options = RelayOptions.Parse(args);
        Directory.CreateDirectory(Path.GetDirectoryName(options.StatePath)!);
        var state = new ServerRelayState
        {
            PipeName = options.PipeName,
            RelayProcessId = Environment.ProcessId,
            ProfileId = options.ProfileId,
            ProfileName = options.ProfileName,
            Version = options.Version,
            DataPath = options.DataPath,
            ServerExecutablePath = options.ServerExecutablePath,
            RestartOnCrash = options.RestartOnCrash,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var stateGate = new object();
        var processGate = new object();
        var processes = new List<Process>();
        Process? currentProcess = null;
        var stopRequested = 0;
        var restartCount = 0;
        var lastExitCode = 0;
        DateTimeOffset? currentProcessStartedAt = null;
        Task? pipeTask = null;

        Process? GetCurrentProcess()
        {
            lock (processGate)
            {
                return currentProcess;
            }
        }

        bool PersistState()
        {
            lock (stateGate)
            {
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                return TryWriteState(options.StatePath, state);
            }
        }

        void ObserveCommand(string command)
        {
            if (IsStopCommand(command))
                Interlocked.Exchange(ref stopRequested, 1);
        }

        try
        {
            while (!relayCts.IsCancellationRequested && Volatile.Read(ref stopRequested) == 0)
            {
                var process = StartServerProcess(options);
                lock (processGate)
                {
                    currentProcess = process;
                    processes.Add(process);
                }

                currentProcessStartedAt = DateTimeOffset.UtcNow;
                state.ServerProcessId = process.Id;
                state.IsRestarting = false;
                state.LastError = null;
                state.StartedAtUtc = currentProcessStartedAt.Value;
                if (!PersistState())
                {
                    TryKillProcess(process);
                    throw new IOException("Failed to write server relay state file.");
                }

                pipeTask ??= RunPipeLoopAsync(
                    options.PipeName,
                    state,
                    () =>
                    {
                        var liveProcess = GetCurrentProcess();
                        return liveProcess is null || IsProcessTerminated(liveProcess);
                    },
                    () =>
                    {
                        var liveProcess = GetCurrentProcess();
                        return liveProcess is null ? null : TryGetProcessId(liveProcess);
                    },
                    (command, commandCancellationToken) =>
                    {
                        var liveProcess = GetCurrentProcess();
                        if (liveProcess is null || IsProcessTerminated(liveProcess))
                        {
                            throw new InvalidOperationException(
                                state.IsRestarting
                                    ? "Server process is restarting."
                                    : "Server process has exited.");
                        }

                        return WriteConsoleCommandAsync(liveProcess, command, commandCancellationToken);
                    },
                    ServerRelayProtocol.DefaultTimeouts,
                    relayCts.Token,
                    ObserveCommand,
                    () => _ = PersistState());

                try
                {
                    await process.WaitForExitAsync(relayCts.Token);
                }
                catch (OperationCanceledException) when (relayCts.IsCancellationRequested)
                {
                    break;
                }

                lastExitCode = TryGetExitCode(process);
                lock (processGate)
                {
                    if (ReferenceEquals(currentProcess, process))
                        currentProcess = null;
                }

                state.ServerProcessId = null;
                state.LastExitCode = lastExitCode;
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                PersistState();

                if (relayCts.IsCancellationRequested ||
                    Volatile.Read(ref stopRequested) != 0 ||
                    !options.RestartOnCrash)
                {
                    break;
                }

                if (currentProcessStartedAt.HasValue &&
                    DateTimeOffset.UtcNow - currentProcessStartedAt.Value >= StableRuntimeWindow)
                {
                    restartCount = 0;
                }

                if (restartCount >= MaxConsecutiveCrashRestarts)
                {
                    state.IsRestarting = false;
                    state.LastError =
                        $"Server exited repeatedly; automatic restart stopped after {MaxConsecutiveCrashRestarts} retries.";
                    PersistState();
                    break;
                }

                restartCount++;
                state.RestartCount = restartCount;
                state.IsRestarting = true;
                state.LastError =
                    $"Server exited (code {lastExitCode}); restarting in {CrashRestartDelay.TotalSeconds:0} seconds.";
                PersistState();

                try
                {
                    await Task.Delay(CrashRestartDelay, relayCts.Token);
                }
                catch (OperationCanceledException) when (relayCts.IsCancellationRequested)
                {
                    break;
                }
            }

            return lastExitCode;
        }
        finally
        {
            await relayCts.CancelAsync();
            var liveProcess = GetCurrentProcess();
            if (liveProcess is not null)
            {
                TryKillProcess(liveProcess);
            }

            if (pipeTask is not null)
            {
                try
                {
                    await pipeTask.WaitAsync(TimeSpan.FromSeconds(2));
                }
                catch
                {
                    // The relay is shutting down; stale pipe waits are harmless here.
                }
            }

            foreach (var process in processes.Distinct())
            {
                try
                {
                    process.Dispose();
                }
                catch
                {
                    // Best-effort disposal only.
                }
            }

            TryDeleteState(options.StatePath);
        }
    }

    private static Process StartServerProcess(RelayOptions options)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = options.ServerExecutablePath,
                WorkingDirectory = options.WorkingDirectory,
                Arguments = $"--dataPath \"{options.DataPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardInputEncoding = new UTF8Encoding(false),
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                CreateNoWindow = true,
                UseShellExecute = false
            },
            EnableRaisingEvents = true
        };

        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };

        try
        {
            if (!process.Start())
                throw new InvalidOperationException("Failed to start Vintage Story server process.");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return process;
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    internal static async Task RunPipeLoopAsync(
        string pipeName,
        ServerRelayState state,
        Func<bool> isProcessTerminated,
        Func<int?> getProcessId,
        Func<string, CancellationToken, Task> writeConsoleCommand,
        ServerRelayTimeouts timeouts,
        CancellationToken cancellationToken,
        Action<string>? commandObserved = null,
        Action? stateChanged = null)
    {
        var commandForwarder = new ServerRelayCommandForwarder(writeConsoleCommand);

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleClientAsync(
                    pipe,
                    state,
                    isProcessTerminated,
                    getProcessId,
                    commandForwarder,
                    timeouts,
                    cancellationToken,
                    commandObserved,
                    stateChanged);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(300, cancellationToken);
                }
                catch
                {
                    break;
                }
            }
        }
    }

    internal static async Task HandleClientAsync(
        Stream pipe,
        ServerRelayState state,
        Func<bool> isProcessTerminated,
        Func<int?> getProcessId,
        ServerRelayCommandForwarder commandForwarder,
        ServerRelayTimeouts timeouts,
        CancellationToken relayCancellationToken,
        Action<string>? commandObserved = null,
        Action? stateChanged = null)
    {
        string? requestJson;
        using (var requestCts = CreateTimeoutCts(relayCancellationToken, timeouts.RequestRead))
        {
            try
            {
                requestJson = await ReadRequestLineAsync(pipe, requestCts.Token);
            }
            catch (OperationCanceledException) when (relayCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException)
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = "Relay request read timed out."
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }
            catch (Exception ex)
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = $"Failed to read relay request: {ex.Message}"
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }
        }

        if (string.IsNullOrWhiteSpace(requestJson))
        {
            await TryWriteResponseAsync(
                pipe,
                new ServerRelayResponse
                {
                    Success = false,
                    Error = "Empty relay request."
                },
                timeouts.ResponseWrite,
                relayCancellationToken);
            return;
        }

        ServerRelayRequest? request;
        try
        {
            request = JsonSerializer.Deserialize<ServerRelayRequest>(
                requestJson,
                ServerRelayProtocol.JsonOptions);
        }
        catch (Exception ex)
        {
            await TryWriteResponseAsync(
                pipe,
                new ServerRelayResponse
                {
                    Success = false,
                    Error = $"Invalid relay request: {ex.Message}"
                },
                timeouts.ResponseWrite,
                relayCancellationToken);
            return;
        }

        if (request is null)
        {
            await TryWriteResponseAsync(
                pipe,
                new ServerRelayResponse
                {
                    Success = false,
                    Error = "Relay request could not be parsed."
                },
                timeouts.ResponseWrite,
                relayCancellationToken);
            return;
        }

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        state.ServerProcessId = getProcessId();
        TryInvokeStateChanged(stateChanged);

        if (request.Type.Equals(ServerRelayProtocol.RequestTypePing, StringComparison.OrdinalIgnoreCase) ||
            request.Type.Equals(ServerRelayProtocol.RequestTypeStatus, StringComparison.OrdinalIgnoreCase))
        {
            var processTerminated = isProcessTerminated();
            await TryWriteResponseAsync(
                pipe,
                new ServerRelayResponse
                {
                    Success = !processTerminated || state.IsRestarting,
                    Error = processTerminated
                        ? state.IsRestarting ? "Server process is restarting." : "Server process has exited."
                        : null,
                    State = state
                },
                timeouts.ResponseWrite,
                relayCancellationToken);
            return;
        }

        if (request.Type.Equals(ServerRelayProtocol.RequestTypeCommand, StringComparison.OrdinalIgnoreCase))
        {
            var command = NormalizeCommand(request.Command);
            if (string.IsNullOrWhiteSpace(command))
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = "Command is empty.",
                        State = state
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }

            TryInvokeCommandObserved(commandObserved, command);

            if (isProcessTerminated())
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = state.IsRestarting
                            ? "Server process is restarting."
                            : "Server process has exited.",
                        State = state
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }

            try
            {
                await commandForwarder.ForwardAsync(
                    command,
                    timeouts.CommandForward,
                    relayCancellationToken);
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                TryInvokeStateChanged(stateChanged);
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = true,
                        State = state
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }
            catch (OperationCanceledException) when (relayCancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (TimeoutException ex)
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        State = state
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }
            catch (Exception ex)
            {
                await TryWriteResponseAsync(
                    pipe,
                    new ServerRelayResponse
                    {
                        Success = false,
                        Error = ex.Message,
                        State = state
                    },
                    timeouts.ResponseWrite,
                    relayCancellationToken);
                return;
            }
        }

        await TryWriteResponseAsync(
            pipe,
            new ServerRelayResponse
            {
                Success = false,
                Error = $"Unknown relay request type: {request.Type}",
                State = state
            },
            timeouts.ResponseWrite,
            relayCancellationToken);
    }

    private static async Task<bool> TryWriteResponseAsync(
        Stream pipe,
        ServerRelayResponse response,
        TimeSpan timeout,
        CancellationToken relayCancellationToken)
    {
        using var responseCts = CreateTimeoutCts(relayCancellationToken, timeout);
        try
        {
            var json = JsonSerializer.Serialize(response, ServerRelayProtocol.JsonOptions);
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            await pipe.WriteAsync(bytes.AsMemory(), responseCts.Token);
            await pipe.FlushAsync(responseCts.Token);
            return true;
        }
        catch
        {
            // A disconnected or stalled client must never prevent the relay from
            // accepting the next request.
            return false;
        }
    }

    private static async Task<string?> ReadRequestLineAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        const int maxRequestBytes = 64 * 1024;
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];

        while (true)
        {
            var bytesRead = await pipe.ReadAsync(chunk.AsMemory(), cancellationToken);
            if (bytesRead == 0)
                break;

            var newlineIndex = Array.IndexOf(chunk, (byte)'\n', 0, bytesRead);
            var bytesToAppend = newlineIndex >= 0 ? newlineIndex : bytesRead;
            if (buffer.Length + bytesToAppend > maxRequestBytes)
                throw new InvalidDataException($"Relay request exceeds {maxRequestBytes} bytes.");

            buffer.Write(chunk, 0, bytesToAppend);
            if (newlineIndex >= 0)
                break;
        }

        if (buffer.Length == 0)
            return null;

        return Encoding.UTF8
            .GetString(buffer.GetBuffer(), 0, (int)buffer.Length)
            .TrimEnd('\r');
    }

    private static CancellationTokenSource CreateTimeoutCts(
        CancellationToken cancellationToken,
        TimeSpan timeout)
    {
        var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1));
        return timeoutCts;
    }

    private static async Task WriteConsoleCommandAsync(
        Process process,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(command + Environment.NewLine);
        await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    private static bool TryWriteState(string statePath, ServerRelayState state)
    {
        try
        {
            var tempPath = $"{statePath}.tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, StateJsonOptions), Encoding.UTF8);
            File.Move(tempPath, statePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDeleteState(string statePath)
    {
        try
        {
            File.Delete(statePath);
        }
        catch
        {
            // Stale state files are validated by ping on the next launcher start.
        }
    }

    private static string NormalizeCommand(string? command)
    {
        var normalized = string.IsNullOrWhiteSpace(command) ? string.Empty : command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            return string.Empty;
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static bool IsProcessTerminated(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private static int? TryGetProcessId(Process process)
    {
        try
        {
            return process.Id;
        }
        catch
        {
            return null;
        }
    }

    private static int TryGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return 0;
        }
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The caller is already failing startup; avoid masking the root error.
        }
    }

    private sealed class RelayOptions
    {
        public string PipeName { get; private init; } = string.Empty;

        public string StatePath { get; private init; } = string.Empty;

        public string ServerExecutablePath { get; private init; } = string.Empty;

        public string WorkingDirectory { get; private init; } = string.Empty;

        public string DataPath { get; private init; } = string.Empty;

        public string ProfileId { get; private init; } = string.Empty;

        public string ProfileName { get; private init; } = string.Empty;

        public string Version { get; private init; } = string.Empty;

        public bool RestartOnCrash { get; private init; }

        public static RelayOptions Parse(string[] args)
        {
            var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                if (!arg.StartsWith("--", StringComparison.Ordinal))
                    continue;
                if (arg.Equals(ServerRelayProtocol.LauncherArgument, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (i + 1 >= args.Length)
                    throw new ArgumentException($"Missing value for relay argument '{arg}'.");
                values[arg] = args[++i];
            }

            var options = new RelayOptions
            {
                PipeName = Require(values, "--pipe-name"),
                StatePath = Require(values, "--state-path"),
                ServerExecutablePath = Require(values, "--server-exe"),
                WorkingDirectory = Require(values, "--working-dir"),
                DataPath = Require(values, "--data-path"),
                ProfileId = Require(values, "--profile-id"),
                ProfileName = values.GetValueOrDefault("--profile-name") ?? string.Empty,
                Version = values.GetValueOrDefault("--version") ?? string.Empty,
                RestartOnCrash = ParseBoolean(values.GetValueOrDefault("--restart-on-crash"))
            };

            if (!File.Exists(options.ServerExecutablePath))
                throw new FileNotFoundException("Vintage Story server executable was not found.", options.ServerExecutablePath);

            return options;
        }

        private static string Require(Dictionary<string, string> values, string key)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
                return value;

            throw new ArgumentException($"Missing required relay argument '{key}'.");
        }

        private static bool ParseBoolean(string? value)
        {
            return bool.TryParse(value, out var parsed) && parsed;
        }
    }

    private static bool IsStopCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
            return false;

        var normalized = command.Trim().TrimStart('/');
        var separator = normalized.IndexOfAny([' ', '\t', '\r', '\n']);
        if (separator >= 0)
            normalized = normalized[..separator];

        return normalized.Equals("stop", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryInvokeCommandObserved(Action<string>? callback, string command)
    {
        try
        {
            callback?.Invoke(command);
        }
        catch
        {
            // Diagnostics callbacks must never break the relay command path.
        }
    }

    private static void TryInvokeStateChanged(Action? callback)
    {
        try
        {
            callback?.Invoke();
        }
        catch
        {
            // State persistence is best effort; the live pipe remains authoritative.
        }
    }
}

internal sealed class ServerRelayCommandForwarder
{
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Func<string, CancellationToken, Task> _writeConsoleCommand;

    public ServerRelayCommandForwarder(Func<string, CancellationToken, Task> writeConsoleCommand)
    {
        _writeConsoleCommand = writeConsoleCommand;
    }

    public async Task ForwardAsync(
        string command,
        TimeSpan timeout,
        CancellationToken relayCancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(relayCancellationToken);
        timeoutCts.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMilliseconds(1));

        var gateAcquired = false;
        try
        {
            await _writeGate.WaitAsync(timeoutCts.Token);
            gateAcquired = true;

            // The Process standard-input stream can be backed by a synchronous
            // Windows pipe. Run the write outside the request handler so even a
            // synchronously blocked WriteAsync call cannot freeze the relay loop.
            var writeTask = Task.Run(
                () => WriteAndReleaseGateAsync(command, relayCancellationToken),
                CancellationToken.None);
            gateAcquired = false;

            // Observe a later failure even if this caller reaches its deadline first.
            _ = writeTask.ContinueWith(
                static task => _ = task.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);

            await writeTask.WaitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (relayCancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException("Relay command forwarding timed out.");
        }
        finally
        {
            if (gateAcquired)
                _writeGate.Release();
        }
    }

    private async Task WriteAndReleaseGateAsync(
        string command,
        CancellationToken relayCancellationToken)
    {
        try
        {
            await _writeConsoleCommand(command, relayCancellationToken);
        }
        finally
        {
            _writeGate.Release();
        }
    }
}

