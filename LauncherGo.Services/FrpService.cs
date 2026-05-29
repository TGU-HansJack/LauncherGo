using System.Diagnostics;
using System.Management;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     FRP 服务默认实现
/// </summary>
public class FrpService : IFrpService
{
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly ILauncherPreferencesService? _launcherPreferencesService;
    private readonly ILogger<FrpService> _logger;
    private Process? _process;
    private FrpRuntimeStatus _currentStatus = new();

    public string ConfigPath => WorkspacePathHelper.FrpConfigPath;

    public event EventHandler<FrpRuntimeStatus>? StatusChanged;

    public FrpService()
        : this(null, NullLogger<FrpService>.Instance)
    {
    }

    public FrpService(ILogger<FrpService>? logger = null)
        : this(null, logger)
    {
    }

    public FrpService(ILauncherPreferencesService? launcherPreferencesService, ILogger<FrpService>? logger = null)
    {
        _launcherPreferencesService = launcherPreferencesService;
        _logger = logger ?? NullLogger<FrpService>.Instance;
    }

    /// <inheritdoc />
    public FrpRuntimeStatus GetCurrentStatus()
    {
        if (!_processGate.Wait(0))
            return CloneStatus(_currentStatus);

        try
        {
            ClearTrackedProcessIfTerminated();

            if (_process is null)
                TryAttachToExistingProcess();

            return CloneStatus(_currentStatus);
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task<string> LoadConfigAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.FrpRoot);

        if (!File.Exists(WorkspacePathHelper.FrpConfigPath))
        {
            var defaults = BuildDefaultConfig();
            await File.WriteAllTextAsync(
                    WorkspacePathHelper.FrpConfigPath,
                    defaults,
                    new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            return defaults;
        }

        return await File.ReadAllTextAsync(WorkspacePathHelper.FrpConfigPath, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task SaveConfigAsync(string configText, CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        Directory.CreateDirectory(WorkspacePathHelper.FrpRoot);

        var normalized = configText ?? string.Empty;
        await File.WriteAllTextAsync(
                WorkspacePathHelper.FrpConfigPath,
                normalized,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task ImportExecutableAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            WorkspacePathHelper.EnsureWorkspace();
            Directory.CreateDirectory(WorkspacePathHelper.FrpRoot);

            var normalizedSource = NormalizePath(sourcePath);
            if (string.IsNullOrWhiteSpace(normalizedSource) || !File.Exists(normalizedSource))
                throw new FileNotFoundException("未找到 FRP 可执行文件。", sourcePath);

            if (_process is not null && !IsProcessTerminated(_process))
                throw new InvalidOperationException("请先关闭内网穿透后再导入可执行文件。");

            var targetExecutable = NormalizePath(WorkspacePathHelper.FrpExecutablePath);
            if (normalizedSource.Equals(targetExecutable, StringComparison.OrdinalIgnoreCase))
                return;

            File.Copy(normalizedSource, targetExecutable, overwrite: true);
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearTrackedProcessIfTerminated();
            if (_process is not null && !IsProcessTerminated(_process))
                return;

            WorkspacePathHelper.EnsureWorkspace();
            Directory.CreateDirectory(WorkspacePathHelper.FrpRoot);

            if (TryAttachToExistingProcess())
                return;

            var launch = BuildLaunchCommand(GetCurrentFrpCommand());
            await LoadConfigAsync(cancellationToken).ConfigureAwait(false);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = launch.ExecutablePath,
                    WorkingDirectory = WorkspacePathHelper.FrpRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                },
                EnableRaisingEvents = true
            };
            foreach (var argument in launch.Arguments)
                process.StartInfo.ArgumentList.Add(argument);

            process.OutputDataReceived += OnOutputDataReceived;
            process.ErrorDataReceived += OnOutputDataReceived;
            process.Exited += OnProcessExited;

            if (!process.Start())
                throw new InvalidOperationException("未能启动内网穿透进程。");

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            _process = process;
            _currentStatus = new FrpRuntimeStatus
            {
                IsRunning = true,
                ProcessId = process.Id,
                StartedAtUtc = DateTimeOffset.UtcNow
            };
            NotifyStatusChanged();

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            try
            {
                if (process.HasExited)
                    throw new InvalidOperationException("内网穿透启动后立即退出，请检查 frpc.toml 配置。");
            }
            catch (ObjectDisposedException)
            {
                throw new InvalidOperationException("内网穿透启动后立即退出，请检查 frpc.toml 配置。");
            }

            _logger.LogInformation(
                "FRP started. ProcessId={ProcessId}, ConfigPath={ConfigPath}, ExecutablePath={ExecutablePath}.",
                process.Id,
                WorkspacePathHelper.FrpConfigPath,
                launch.ExecutablePath);
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearTrackedProcessIfTerminated();

            var process = _process;
            if (process is null || IsProcessTerminated(process))
            {
                if (!TryAttachToExistingProcess())
                    return;

                process = _process;
                if (process is null || IsProcessTerminated(process))
                    return;
            }

            var skipWait = false;
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Already exited.
                skipWait = true;
                OnProcessExited(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to stop FRP process {ProcessId}.", TryGetProcessId(process));
            }

            if (skipWait)
                return;

            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCts.CancelAfter(gracefulTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(2) : gracefulTimeout);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Timeout is best-effort.
            }
            catch (ObjectDisposedException)
            {
                // Process was already cleaned up by the exit event.
            }
            catch (InvalidOperationException)
            {
                // Process was already detached or disposed by the exit callback.
            }
        }
        finally
        {
            _processGate.Release();
        }
    }

    private static string BuildDefaultConfig()
    {
        return
            "# frpc.toml\n" +
            "# 这里填写 FRP 客户端配置。\n" +
            "# 文档：https://github.com/fatedier/frp\n\n" +
            "# 如果需要多个配置文件，可以用 includes 继续引用其他 toml。\n" +
            "# 例如：includes = [\"./confd/*.toml\"]\n\n" +
            "serverAddr = \"127.0.0.1\"\n" +
            "serverPort = 7000\n\n" +
            "# [[proxies]]\n" +
            "# name = \"vintagestory\"\n" +
            "# type = \"tcp\"\n" +
            "# localIP = \"127.0.0.1\"\n" +
            "# localPort = 42420\n" +
            "# remotePort = 42420\n";
    }

    private string GetCurrentFrpCommand()
    {
        try
        {
            var preferences = _launcherPreferencesService?.Load();
            return NormalizeCommandText(preferences?.Frp?.FrpCommand);
        }
        catch
        {
            return FrpIntegrationSettings.DefaultFrpCommand;
        }
    }

    private static CommandLaunchSpec BuildLaunchCommand(string commandText)
    {
        var tokens = SplitCommandLine(commandText);
        if (tokens.Count == 0)
            throw new InvalidOperationException("未配置内网穿透命令。");

        var executablePath = ResolveExecutablePath(tokens[0]);
        if (!File.Exists(executablePath))
            throw new InvalidOperationException("未找到 FRP 可执行文件，请先导入 FRP 可执行文件。");

        return new CommandLaunchSpec(
            executablePath,
            tokens.Skip(1).ToArray());
    }

    private bool TryAttachToExistingProcess()
    {
        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WorkspacePathHelper.FrpExecutablePath));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate FRP processes.");
            return false;
        }

        var runtimeExecutable = NormalizePath(WorkspacePathHelper.FrpExecutablePath);
        var runtimeConfigPath = NormalizePath(WorkspacePathHelper.FrpConfigPath);

        foreach (var candidate in candidates)
        {
            try
            {
                var executablePath = NormalizePath(candidate.MainModule?.FileName);
                if (string.IsNullOrWhiteSpace(executablePath) ||
                    !executablePath.Equals(runtimeExecutable, StringComparison.OrdinalIgnoreCase))
                {
                    candidate.Dispose();
                    continue;
                }

                var commandLine = TryReadCommandLine(candidate.Id);
                if (!MatchesRuntimeCommandLine(commandLine, runtimeConfigPath))
                {
                    candidate.Dispose();
                    continue;
                }

                AttachToProcess(candidate, emitOutput: false);
                return true;
            }
            catch (Exception ex)
            {
                candidate.Dispose();
                _logger.LogDebug(ex, "Failed to inspect FRP process {ProcessId}.", TryGetProcessId(candidate));
            }
        }

        return false;
    }

    private void AttachToProcess(Process process, bool emitOutput)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;
        _process = process;
        _currentStatus = new FrpRuntimeStatus
        {
            IsRunning = true,
            ProcessId = TryGetProcessId(process),
            StartedAtUtc = TryGetStartTimeUtc(process)
        };
        NotifyStatusChanged();

        if (emitOutput)
        {
            _logger.LogInformation(
                "Attached to existing FRP process. ProcessId={ProcessId}, ConfigPath={ConfigPath}.",
                _currentStatus.ProcessId,
                WorkspacePathHelper.FrpConfigPath);
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        _logger.LogDebug("[frp] {Line}", e.Data);
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        if (_process is null && !_currentStatus.IsRunning)
            return;

        var previousProcessId = _currentStatus.ProcessId;
        _currentStatus = new FrpRuntimeStatus();

        var process = _process;
        _process = null;

        if (process is not null)
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnOutputDataReceived;
            process.Exited -= OnProcessExited;
            process.Dispose();
        }

        NotifyStatusChanged();
        _logger.LogInformation("FRP process exited. PreviousProcessId={ProcessId}.", previousProcessId);
    }

    private static string NormalizeCommandText(string? commandText)
    {
        return string.IsNullOrWhiteSpace(commandText)
            ? FrpIntegrationSettings.DefaultFrpCommand
            : commandText.Trim();
    }

    private static IReadOnlyList<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
            return result;

        var current = new StringBuilder();
        var inQuotes = false;

        for (var i = 0; i < commandLine.Length; i++)
        {
            var c = commandLine[i];
            if (c == '\"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
            result.Add(current.ToString());

        return result;
    }

    private static string ResolveExecutablePath(string executableToken)
    {
        var token = executableToken.Trim();
        if (string.IsNullOrWhiteSpace(token))
            return string.Empty;

        if (Path.IsPathRooted(token))
            return NormalizePath(token);

        return NormalizePath(Path.Combine(WorkspacePathHelper.FrpRoot, token));
    }

    private readonly record struct CommandLaunchSpec(string ExecutablePath, IReadOnlyList<string> Arguments);

    private void ClearTrackedProcessIfTerminated()
    {
        var process = _process;
        if (process is null)
            return;

        if (!IsProcessTerminated(process))
            return;

        OnProcessExited(this, EventArgs.Empty);
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

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string TryReadCommandLine(int processId)
    {
        if (!OperatingSystem.IsWindows())
            return string.Empty;

        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            foreach (ManagementObject item in searcher.Get())
                return item["CommandLine"]?.ToString() ?? string.Empty;
        }
        catch
        {
            // ignore
        }

        return string.Empty;
    }

    private static FrpRuntimeStatus CloneStatus(FrpRuntimeStatus status)
    {
        return new FrpRuntimeStatus
        {
            IsRunning = status.IsRunning,
            ProcessId = status.ProcessId,
            StartedAtUtc = status.StartedAtUtc
        };
    }

    private void NotifyStatusChanged()
    {
        StatusChanged?.Invoke(this, CloneStatus(_currentStatus));
    }

    private static bool MatchesRuntimeCommandLine(string commandLine, string runtimeConfigPath)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return false;

        if (commandLine.Contains(Path.GetFileName(runtimeConfigPath), StringComparison.OrdinalIgnoreCase))
            return true;

        return commandLine.Contains(runtimeConfigPath, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return Path.GetFullPath(path)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }
}

