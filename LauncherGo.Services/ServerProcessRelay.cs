using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace LauncherGo.Services;

public static class ServerProcessRelay
{
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

        using var process = new Process
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

        if (!process.Start())
            throw new InvalidOperationException("Failed to start Vintage Story server process.");

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var state = new ServerRelayState
        {
            PipeName = options.PipeName,
            RelayProcessId = Environment.ProcessId,
            ServerProcessId = process.Id,
            ProfileId = options.ProfileId,
            ProfileName = options.ProfileName,
            Version = options.Version,
            DataPath = options.DataPath,
            ServerExecutablePath = options.ServerExecutablePath,
            StartedAtUtc = DateTimeOffset.UtcNow,
            UpdatedAtUtc = DateTimeOffset.UtcNow
        };
        if (!TryWriteState(options.StatePath, state))
        {
            TryKillProcess(process);
            throw new IOException("Failed to write server relay state file.");
        }

        using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        process.Exited += (_, _) => relayCts.Cancel();

        var pipeTask = RunPipeLoopAsync(process, options.StatePath, state, relayCts.Token);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
            return TryGetExitCode(process);
        }
        finally
        {
            await relayCts.CancelAsync();
            try
            {
                await pipeTask.WaitAsync(TimeSpan.FromSeconds(2));
            }
            catch
            {
                // The relay is shutting down; stale pipe waits are harmless here.
            }

            TryDeleteState(options.StatePath);
        }
    }

    private static async Task RunPipeLoopAsync(
        Process process,
        string statePath,
        ServerRelayState state,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await using var pipe = new NamedPipeServerStream(
                    state.PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(cancellationToken);
                await HandleClientAsync(pipe, process, statePath, state, cancellationToken);
            }
            catch (OperationCanceledException)
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

    private static async Task HandleClientAsync(
        Stream pipe,
        Process process,
        string statePath,
        ServerRelayState state,
        CancellationToken cancellationToken)
    {
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };

        var requestJson = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestJson))
        {
            await WriteResponseAsync(writer, new ServerRelayResponse
            {
                Success = false,
                Error = "Empty relay request."
            }, cancellationToken);
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
            await WriteResponseAsync(writer, new ServerRelayResponse
            {
                Success = false,
                Error = $"Invalid relay request: {ex.Message}"
            }, cancellationToken);
            return;
        }

        if (request is null)
        {
            await WriteResponseAsync(writer, new ServerRelayResponse
            {
                Success = false,
                Error = "Relay request could not be parsed."
            }, cancellationToken);
            return;
        }

        state.UpdatedAtUtc = DateTimeOffset.UtcNow;
        state.ServerProcessId = TryGetProcessId(process);
        TryWriteState(statePath, state);

        if (request.Type.Equals(ServerRelayProtocol.RequestTypePing, StringComparison.OrdinalIgnoreCase) ||
            request.Type.Equals(ServerRelayProtocol.RequestTypeStatus, StringComparison.OrdinalIgnoreCase))
        {
            await WriteResponseAsync(writer, new ServerRelayResponse
            {
                Success = !IsProcessTerminated(process),
                Error = IsProcessTerminated(process) ? "Server process has exited." : null,
                State = state
            }, cancellationToken);
            return;
        }

        if (request.Type.Equals(ServerRelayProtocol.RequestTypeCommand, StringComparison.OrdinalIgnoreCase))
        {
            var command = NormalizeCommand(request.Command);
            if (string.IsNullOrWhiteSpace(command))
            {
                await WriteResponseAsync(writer, new ServerRelayResponse
                {
                    Success = false,
                    Error = "Command is empty.",
                    State = state
                }, cancellationToken);
                return;
            }

            if (IsProcessTerminated(process))
            {
                await WriteResponseAsync(writer, new ServerRelayResponse
                {
                    Success = false,
                    Error = "Server process has exited.",
                    State = state
                }, cancellationToken);
                return;
            }

            try
            {
                await WriteConsoleCommandAsync(process, command, cancellationToken);
                state.UpdatedAtUtc = DateTimeOffset.UtcNow;
                TryWriteState(statePath, state);
                await WriteResponseAsync(writer, new ServerRelayResponse
                {
                    Success = true,
                    State = state
                }, cancellationToken);
                return;
            }
            catch (Exception ex)
            {
                await WriteResponseAsync(writer, new ServerRelayResponse
                {
                    Success = false,
                    Error = ex.Message,
                    State = state
                }, cancellationToken);
                return;
            }
        }

        await WriteResponseAsync(writer, new ServerRelayResponse
        {
            Success = false,
            Error = $"Unknown relay request type: {request.Type}",
            State = state
        }, cancellationToken);
    }

    private static async Task WriteResponseAsync(
        TextWriter writer,
        ServerRelayResponse response,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(response, ServerRelayProtocol.JsonOptions);
        await writer.WriteLineAsync(json.AsMemory(), cancellationToken);
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
                Version = values.GetValueOrDefault("--version") ?? string.Empty
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
    }
}

