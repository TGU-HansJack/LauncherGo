using System.Diagnostics;
using System.Text.RegularExpressions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

public sealed partial class ServerProcessService(IInstanceProfileService profileService) : IServerProcessService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly object _statusGate = new();
    private readonly HashSet<string> _onlinePlayers = new(StringComparer.OrdinalIgnoreCase);
    private Process? _process;
    private InstanceProfile? _currentProfile;
    private DateTimeOffset? _startedAtUtc;
    private TimeSpan _lastProcessorTime;
    private DateTimeOffset _lastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _lastCpuPercent;
    private int _peakOnlinePlayers;
    private ServerRuntimeStatus _cachedStatus = new() { Message = "未启动" };

    public event EventHandler<string>? OutputReceived;

    public event EventHandler<ServerRuntimeStatus>? StatusChanged;

    public ServerRuntimeStatus GetCurrentStatus()
    {
        lock (_statusGate)
        {
            if (_process is null || HasExited(_process))
            {
                _cachedStatus = new ServerRuntimeStatus
                {
                    IsRunning = false,
                    ProfileId = _currentProfile?.Id,
                    OnlinePlayers = 0,
                    PeakOnlinePlayers = _peakOnlinePlayers,
                    Message = "未启动"
                };
                return _cachedStatus;
            }

            var memoryBytes = SafeRead(() => _process.WorkingSet64, 0L);
            var cpuPercent = CalculateCpuPercent(_process);
            _cachedStatus = new ServerRuntimeStatus
            {
                IsRunning = true,
                ProcessId = SafeRead(() => _process.Id, (int?)null),
                StartedAtUtc = _startedAtUtc,
                ProfileId = _currentProfile?.Id,
                CpuPercent = cpuPercent,
                MemoryBytes = memoryBytes,
                OnlinePlayers = _onlinePlayers.Count,
                PeakOnlinePlayers = _peakOnlinePlayers,
                CanSendCommands = true,
                Message = "运行中"
            };

            return _cachedStatus;
        }
    }

    public async Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_process is not null && !HasExited(_process))
            {
                throw new InvalidOperationException("服务器已在运行中。");
            }

            var installPath = profileService.EnsureVersionInstalled(profile.Version);
            var serverExe = Path.Combine(installPath, "VintagestoryServer.exe");
            if (!File.Exists(serverExe))
            {
                throw new InvalidOperationException($"未找到服务端程序：{serverExe}");
            }

            Directory.CreateDirectory(profile.DirectoryPath);
            Directory.CreateDirectory(profile.SaveDirectory);
            Directory.CreateDirectory(Path.Combine(profile.DirectoryPath, "Logs"));
            ServerConfigBootstrapper.EnsureGenerated(installPath, profile);

            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = serverExe,
                    WorkingDirectory = installPath,
                    Arguments = $"--dataPath \"{profile.DirectoryPath}\"",
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.OutputDataReceived += (_, args) => HandleOutput(args.Data);
            process.ErrorDataReceived += (_, args) => HandleOutput(args.Data);
            process.Exited += (_, _) =>
            {
                HandleOutput("[system] 服务器进程已退出。");
                lock (_statusGate)
                {
                    _cachedStatus = new ServerRuntimeStatus
                    {
                        IsRunning = false,
                        ProfileId = _currentProfile?.Id,
                        PeakOnlinePlayers = _peakOnlinePlayers,
                        Message = "已停止"
                    };
                    StatusChanged?.Invoke(this, _cachedStatus);
                }
            };

            if (!process.Start())
            {
                throw new InvalidOperationException("服务器进程启动失败。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            lock (_statusGate)
            {
                _process = process;
                _currentProfile = profile;
                _startedAtUtc = DateTimeOffset.UtcNow;
                _lastProcessorTime = SafeRead(() => process.TotalProcessorTime, TimeSpan.Zero);
                _lastCpuSampleUtc = DateTimeOffset.UtcNow;
                _lastCpuPercent = 0;
                _onlinePlayers.Clear();
                _peakOnlinePlayers = 0;
            }

            HandleOutput($"[system] 服务器启动请求已发送，PID={process.Id}，档案={profile.Name}。");
            StatusChanged?.Invoke(this, GetCurrentStatus());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var process = _process;
            if (process is null || HasExited(process))
            {
                return;
            }

            try
            {
                await SendCommandAsync("/stop", cancellationToken);
            }
            catch
            {
                // stdin 不可写时直接进入等待/强制结束流程。
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(gracefulTimeout);
            try
            {
                await process.WaitForExitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (!HasExited(process))
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return;
        }

        Process? process;
        lock (_statusGate)
        {
            process = _process;
        }

        if (process is null || HasExited(process))
        {
            throw new InvalidOperationException("服务器未运行。");
        }

        await process.StandardInput.WriteLineAsync(command.AsMemory(), cancellationToken);
        await process.StandardInput.FlushAsync(cancellationToken);
        HandleOutput($"> {command}");
    }

    private void HandleOutput(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        TrackPlayers(line);
        OutputReceived?.Invoke(this, line);
        StatusChanged?.Invoke(this, GetCurrentStatus());
    }

    private void TrackPlayers(string line)
    {
        lock (_statusGate)
        {
            var join = PlayerJoinRegex().Match(line);
            if (join.Success)
            {
                var name = join.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _onlinePlayers.Add(name);
                }
            }

            var leave = PlayerLeaveRegex().Match(line);
            if (leave.Success)
            {
                var name = leave.Groups["name"].Value.Trim();
                if (!string.IsNullOrWhiteSpace(name))
                {
                    _onlinePlayers.Remove(name);
                }
            }

            var count = OnlineCountRegex().Match(line);
            if (count.Success && int.TryParse(count.Groups["count"].Value, out var parsedCount))
            {
                ReconcileOnlineCount(parsedCount);
            }

            if (LastPlayerDisconnectedRegex().IsMatch(line))
            {
                _onlinePlayers.Clear();
            }

            _peakOnlinePlayers = Math.Max(_peakOnlinePlayers, _onlinePlayers.Count);
        }
    }

    private void ReconcileOnlineCount(int count)
    {
        if (count <= 0)
        {
            _onlinePlayers.Clear();
            return;
        }

        while (_onlinePlayers.Count < count)
        {
            _onlinePlayers.Add($"Player-{_onlinePlayers.Count + 1}");
        }

        while (_onlinePlayers.Count > count)
        {
            _onlinePlayers.Remove(_onlinePlayers.First());
        }
    }

    private double CalculateCpuPercent(Process process)
    {
        var now = DateTimeOffset.UtcNow;
        var totalProcessorTime = SafeRead(() => process.TotalProcessorTime, _lastProcessorTime);
        var elapsedMs = Math.Max(1, (now - _lastCpuSampleUtc).TotalMilliseconds);
        var processorElapsedMs = Math.Max(0, (totalProcessorTime - _lastProcessorTime).TotalMilliseconds);
        var cpu = processorElapsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0;

        _lastProcessorTime = totalProcessorTime;
        _lastCpuSampleUtc = now;
        _lastCpuPercent = Math.Clamp(cpu, 0, 100);
        return _lastCpuPercent;
    }

    private static bool HasExited(Process process)
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

    private static T SafeRead<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch
        {
            return fallback;
        }
    }

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+\[[^\]\r\n]+\]:\d+|\s+\S+:\d+)?\s+joins\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerJoinRegex();

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+(?:left\.|leaves\.|got removed(?:\.|:|：))|(?:离开了游戏[。\.]?|离开了服务器[。\.]?|已被移除(?:。|\.|:|：)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLeaveRegex();

    [GeneratedRegex(@"(?:\bonline\s+players?\D+(?<count>\d+))|(?:\bplayers?\s+online\D+(?<count>\d+))|(?:(?<count>\d+)\D*player(?:s|\(s\))?\D+online\b)|(?:在线(?:玩家|人数)?\D*(?<count>\d+))|(?:(?<count>\d+)\D*人\D*在线)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlineCountRegex();

    [GeneratedRegex(@"\[(?:Server\s+)?Notification\]\s+Last player disconnected\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastPlayerDisconnectedRegex();
}
