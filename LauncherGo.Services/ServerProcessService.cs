using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     服务器进程服务默认实现
/// </summary>
public partial class ServerProcessService : IServerProcessService
{
    private const long PlayerCountBootstrapWindowBytes = 4L * 1024 * 1024;
    private const long CommandAckReadWindowBytes = 256L * 1024;
    private const int CommandAckDelayMilliseconds = 3000;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly IInstanceProfileService? _profileService;
    private readonly IServerAuthService? _serverAuthService;
    private readonly IServerMapService? _serverMapService;
    private readonly ILogger<ServerProcessService> _logger;
    private Process? _process;
    private InstanceProfile? _currentProfile;
    private ServerRelayState? _relayState;
    private CancellationTokenSource? _monitorCts;
    private Task? _monitorTask;
    private bool _canWriteStandardInput;
    private string? _playerCountLogPath;
    private long _playerCountLogPosition;

    private ServerRuntimeStatus _currentStatus = new();
    private readonly object _playerCountGate = new();
    private readonly HashSet<string> _onlinePlayerNames = new(StringComparer.OrdinalIgnoreCase);
    private int _onlinePlayers;
    private int _peakOnlinePlayers;
    private TimeSpan _lastProcessorTime;
    private DateTimeOffset _lastCpuSampleUtc = DateTimeOffset.UtcNow;
    private double _lastCpuPercent;

    public ServerProcessService()
        : this(null, null, null, NullLogger<ServerProcessService>.Instance)
    {
    }

    public ServerProcessService(
        IInstanceProfileService? profileService,
        IServerAuthService? serverAuthService = null,
        IServerMapService? serverMapService = null,
        ILogger<ServerProcessService>? logger = null)
    {
        _profileService = profileService;
        _serverAuthService = serverAuthService;
        _serverMapService = serverMapService;
        _logger = logger ?? NullLogger<ServerProcessService>.Instance;
    }

    /// <inheritdoc />
    public event EventHandler<string>? OutputReceived;

    /// <inheritdoc />
    public event EventHandler<ServerRuntimeStatus>? StatusChanged;

    /// <inheritdoc />
    public ServerRuntimeStatus GetCurrentStatus()
    {
        if (!_processGate.Wait(0))
            return _currentStatus;

        try
        {
            ClearTrackedProcessIfTerminated();

            if (_process is null)
            {
                if (!TryAttachToExistingWorkspaceServerRelay(preferredProfile: null, emitOutput: false) &&
                    !TryAttachToExistingWorkspaceServerProcess(preferredProfile: null, emitOutput: false))
                {
                    PublishStoppedStatusIfStale();
                }
            }

            return _currentStatus;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public ServerRuntimeStatus GetCachedStatus()
    {
        return _currentStatus;
    }

    /// <inheritdoc />
    public async Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ClearTrackedProcessIfTerminated();

            if (_process is { HasExited: false })
                throw new InvalidOperationException("服务器已在运行中。");

            WorkspacePathHelper.EnsureWorkspace();
            profile.DirectoryPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);

            if (TryAttachToExistingWorkspaceServerRelay(profile, emitOutput: true) ||
                TryAttachToExistingWorkspaceServerProcess(profile, emitOutput: true))
            {
                if (_currentProfile?.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase) == true)
                {
                    if (!HasControlChannel())
                        throw new InvalidOperationException(
                            $"检测到该档案已有服务端进程正在运行（PID={_currentStatus.ProcessId}），但它没有可恢复控制通道。请先停止当前服务端，再由新版 LauncherGo 重新启动以恢复命令输入。");

                    return;
                }

                throw new InvalidOperationException(
                    $"检测到已有服务端进程正在运行（PID={_currentStatus.ProcessId}），已接管其状态。请先停止当前服务端后再启动其他档案。");
            }

            var installPath = _profileService?.EnsureVersionInstalled(profile.Version)
                              ?? WorkspacePathHelper.GetServerInstallPath(profile.Version);
            var serverExe = Path.Combine(installPath, "VintagestoryServer.exe");
            if (!File.Exists(serverExe))
                throw new InvalidOperationException($"未找到服务端程序：{serverExe}");

            _logger.LogInformation(
                "Starting Vintage Story server. ProfileId={ProfileId}, ProfileName={ProfileName}, Version={Version}, DataPath={DataPath}.",
                profile.Id,
                profile.Name,
                profile.Version,
                profile.DirectoryPath);

            Directory.CreateDirectory(profile.DirectoryPath);
            var logsPath = WorkspacePathHelper.GetProfileLogsPath(profile.DirectoryPath);
            Directory.CreateDirectory(logsPath);
            await EnsureBuiltInModsBeforeStartAsync(profile, cancellationToken);

            // 缺失配置时自动生成；已有配置仅做必要的非破坏性归一化。
            ServerConfigBootstrapper.EnsureGenerated(installPath, profile);
            RepairLaunchModPaths(profile);
            PrepareSaveFileForStart(profile);
            SqliteConnection.ClearAllPools();

            var relayState = await StartRelayAsync(profile, serverExe, installPath, cancellationToken);
            AttachToRelayState(relayState, profile, emitOutput: false);

            OutputReceived?.Invoke(this,
                $"[system] 服务器进程已通过后台控制通道启动，PID={relayState.ServerProcessId}，Relay PID={relayState.RelayProcessId}");
            _logger.LogInformation(
                "Vintage Story server process started through relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}.",
                relayState.ServerProcessId,
                relayState.RelayProcessId);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task EnsureBuiltInModsBeforeStartAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        if (_serverAuthService is not null && await IsAuthEnabledAsync(profile, cancellationToken))
        {
            await _serverAuthService.EnsureAuthModDeployedAsync(profile, cancellationToken);
            OutputReceived?.Invoke(this, "[system] 已在启动前检查并部署 ServerAuth 模组。");
        }

        if (_serverMapService is not null && await _serverMapService.GetMapModEnabledAsync(profile, cancellationToken))
        {
            await _serverMapService.EnsureMapModDeployedAsync(profile, cancellationToken);
            OutputReceived?.Invoke(this, "[system] 已在启动前检查并部署 ServerMap 地图模组。");
        }
    }

    private async Task<bool> IsAuthEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            if (_serverAuthService is null)
                return false;

            var settings = await _serverAuthService.LoadSettingsAsync(profile, cancellationToken);
            return settings.Enabled;
        }
        catch
        {
            return false;
        }
    }

    private static void RepairLaunchModPaths(InstanceProfile profile)
    {
        try
        {
            var configPath = WorkspacePathHelper.GetProfileConfigPath(profile.DirectoryPath);
            if (!File.Exists(configPath))
                return;

            var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
            Directory.CreateDirectory(modsPath);
            var normalizedModsPath = NormalizePath(modsPath);
            ServerConfigFileIO.UpdateTextFile(configPath, currentJson =>
            {
                if (string.IsNullOrWhiteSpace(currentJson) ||
                    JsonNode.Parse(currentJson) is not JsonObject root)
                {
                    return null;
                }

                if (root["ModPaths"] is JsonArray currentPaths &&
                    currentPaths.Count == 2 &&
                    string.Equals(currentPaths[0]?.GetValue<string>(), "Mods", StringComparison.Ordinal) &&
                    string.Equals(currentPaths[1]?.GetValue<string>(), normalizedModsPath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                root["ModPaths"] = new JsonArray
                {
                    "Mods",
                    normalizedModsPath
                };

                return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
            });
        }
        catch
        {
            // 启动修复失败时不阻断服务器启动，避免把路径修复变成新的故障点。
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            ClearTrackedProcessIfTerminated();

            var process = _process;
            if (process is null || IsProcessTerminated(process))
            {
                if (!TryAttachToExistingWorkspaceServerRelay(preferredProfile: null, emitOutput: true) &&
                    !TryAttachToExistingWorkspaceServerProcess(preferredProfile: null, emitOutput: true))
                {
                    PublishStoppedStatusIfStale();
                    return;
                }

                process = _process;
                if (process is null || IsProcessTerminated(process))
                {
                    PublishStoppedStatusIfStale();
                    return;
                }
            }

            var targetDataPath = ResolveStopTargetDataPath(process);
            var trackedProcessId = TryGetProcessId(process);
            var gracefulCommandSent = false;

            try
            {
                await SendCommandInternalAsync("/stop", cancellationToken);
                gracefulCommandSent = true;
            }
            catch (Exception ex)
            {
                // stdin 写入失败时，继续走强制终止兜底，避免出现“点击停止但进程仍存活”。
                OutputReceived?.Invoke(this, $"[system] 发送停服命令失败，将尝试强制终止：{ex.Message}");
                _logger.LogWarning(ex, "Failed to send graceful stop command to server process {ProcessId}.", trackedProcessId);
            }

            if (gracefulCommandSent && !IsProcessTerminated(process))
            {
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeoutCts.CancelAfter(gracefulTimeout <= TimeSpan.Zero ? TimeSpan.FromSeconds(1) : gracefulTimeout);
                    await process.WaitForExitAsync(timeoutCts.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Graceful 停止超时，继续进入强制终止。
                }
                catch (ObjectDisposedException)
                {
                    // 进程退出事件可能已释放 Process 对象，按已退出处理。
                }
            }

            if (!IsProcessTerminated(process))
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(cancellationToken);
                    OutputReceived?.Invoke(this, "[system] 服务器未在超时时间内退出，已强制终止。");
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"强制终止服务器进程失败：{ex.Message}", ex);
                }
            }

            if (!string.IsNullOrWhiteSpace(targetDataPath))
            {
                await StopOrphanWorkspaceServerProcessesAsync(
                    cancellationToken,
                    excludePid: null,
                    targetDataPath: targetDataPath);

                var remainingMatchedProcessCount = CountWorkspaceServerProcessesByDataPath(targetDataPath);
                if (remainingMatchedProcessCount > 0)
                {
                    throw new InvalidOperationException(
                        $"停服后仍检测到 {remainingMatchedProcessCount} 个同档案服务端进程残留，请稍后重试。");
                }
            }

            ClearTrackedProcessIfTerminated();
            _canWriteStandardInput = false;
        }
        finally
        {
            _processGate.Release();
        }
    }

    /// <inheritdoc />
    public async Task SendCommandAsync(string command, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken);
        try
        {
            await SendCommandInternalAsync(command, cancellationToken);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private async Task SendCommandInternalAsync(string command, CancellationToken cancellationToken)
    {
        if (_process is null || _process.HasExited)
            throw new InvalidOperationException("服务器未运行。");

        var normalized = string.IsNullOrWhiteSpace(command) ? string.Empty : command.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("命令不能为空。");
        if (!normalized.StartsWith('/'))
            normalized = "/" + normalized;

        if (_relayState is not null)
        {
            var response = await ServerRelayClient.SendCommandAsync(
                _relayState.PipeName,
                normalized,
                cancellationToken);
            if (!response.Success)
            {
                _logger.LogWarning(
                    "Failed to send command through relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}, Error={Error}.",
                    _currentStatus.ProcessId,
                    _relayState.RelayProcessId,
                    response.Error);

                if (!IsProcessIdRunning(_relayState.RelayProcessId))
                    _relayState = null;

                throw new InvalidOperationException(response.Error ?? "后台控制通道不可用。");
            }

            if (response.State is not null)
                _relayState = response.State;

            OutputReceived?.Invoke(this, $"[cmd] {normalized}");
            ScheduleCommandReceiptCheck(normalized);
            return;
        }

        if (!_canWriteStandardInput)
            throw new InvalidOperationException("当前服务端进程没有可用控制通道。若它是在旧版本 LauncherGo 崩溃前启动的，需要先停止该服务端，并由新版 LauncherGo 重新启动一次。");

        await WriteServerConsoleCommandAsync(_process, normalized, cancellationToken);
        OutputReceived?.Invoke(this, $"[cmd] {normalized}");
        ScheduleCommandReceiptCheck(normalized);
    }

    private async Task MonitorLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var process = _process;
                if (process is null || IsProcessTerminated(process))
                    break;

                RefreshPlayerCountFromLog();

                var startedAt = _currentStatus.StartedAtUtc ?? DateTimeOffset.UtcNow;
                var onlinePlayers = GetOnlinePlayerCount();
                _peakOnlinePlayers = Math.Max(_peakOnlinePlayers, onlinePlayers);
                UpdateStatus(new ServerRuntimeStatus
                {
                    IsRunning = true,
                    ProcessId = TryGetProcessId(process),
                    StartedAtUtc = startedAt,
                    ProfileId = _currentProfile?.Id,
                    CpuPercent = SampleCpuPercent(process),
                    MemoryBytes = TryGetWorkingSet64(process),
                    OnlinePlayers = onlinePlayers,
                    PeakOnlinePlayers = _peakOnlinePlayers,
                    CanSendCommands = HasControlChannel(),
                    ControlMode = GetControlMode(),
                    Message = HasControlChannel() ? "Relay" : "attached"
                });

                await Task.Delay(1000, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                await Task.Delay(1200, cancellationToken);
            }
        }
    }

    private void OnOutputDataReceived(object sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data)) return;

        var line = e.Data;
        TryUpdatePlayerCountByLine(line);
        OutputReceived?.Invoke(this, line);
    }

    private void TryUpdatePlayerCountByLine(string line)
    {
        var changed = false;
        lock (_playerCountGate)
        {
            changed = TryApplyPlayerCountLine(line, _onlinePlayerNames, ref _onlinePlayers);
        }

        if (changed)
            PublishPlayerCountOnly();
    }

    private void PublishPlayerCountOnly()
    {
        if (!_currentStatus.IsRunning) return;

        var onlinePlayers = GetOnlinePlayerCount();
        _peakOnlinePlayers = Math.Max(_peakOnlinePlayers, onlinePlayers);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = _currentStatus.ProcessId,
            StartedAtUtc = _currentStatus.StartedAtUtc,
            ProfileId = _currentStatus.ProfileId,
            CpuPercent = _currentStatus.CpuPercent,
            MemoryBytes = _currentStatus.MemoryBytes,
            OnlinePlayers = onlinePlayers,
            PeakOnlinePlayers = Math.Max(_currentStatus.PeakOnlinePlayers, onlinePlayers),
            CanSendCommands = HasControlChannel(),
            ControlMode = GetControlMode(),
            Message = _currentStatus.Message
        });
    }

    private void ResetPlayerCountLogMonitor(InstanceProfile? profile, string? dataPath = null)
    {
        var profileDataPath = !string.IsNullOrWhiteSpace(dataPath)
            ? dataPath
            : profile?.DirectoryPath;
        if (string.IsNullOrWhiteSpace(profileDataPath))
        {
            _playerCountLogPath = null;
            _playerCountLogPosition = 0;
            return;
        }

        profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profileDataPath);
        _playerCountLogPath = WorkspacePathHelper.GetServerMainLogPath(profileDataPath);
        _playerCountLogPosition = 0;

        try
        {
            if (File.Exists(_playerCountLogPath))
            {
                _playerCountLogPosition = new FileInfo(_playerCountLogPath).Length;
                BootstrapPlayerCountFromRecentLog();
            }
        }
        catch
        {
            _playerCountLogPosition = 0;
        }
    }

    private void RefreshPlayerCountFromLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_playerCountLogPath) || !File.Exists(_playerCountLogPath))
                return;

            using var stream = new FileStream(_playerCountLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (stream.Length < _playerCountLogPosition)
                _playerCountLogPosition = 0;

            stream.Seek(_playerCountLogPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (!string.IsNullOrWhiteSpace(line))
                    TryUpdatePlayerCountByLine(line);
            }

            _playerCountLogPosition = stream.Position;
        }
        catch
        {
            // Runtime status should not flap because a log file is rotating or temporarily locked.
        }
    }

    private void BootstrapPlayerCountFromRecentLog()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(_playerCountLogPath) || !File.Exists(_playerCountLogPath))
                return;

            using var stream = new FileStream(_playerCountLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startPosition = Math.Max(0, stream.Length - PlayerCountBootstrapWindowBytes);
            stream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);

            // Skip a potential partial line when reading from the middle of the file.
            if (startPosition > 0)
                reader.ReadLine();

            var detected = false;
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;

            string? line;
            while ((line = reader.ReadLine()) is not null)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;

                detected |= TryApplyPlayerCountLine(line, names, ref count);
            }

            if (detected)
            {
                lock (_playerCountGate)
                {
                    _onlinePlayerNames.Clear();
                    foreach (var name in names)
                        _onlinePlayerNames.Add(name);
                    _onlinePlayers = count;
                }
            }
        }
        catch
        {
            // Keep runtime state stable if bootstrap parsing fails.
        }
    }

    private static bool TryApplyPlayerCountLine(string line, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        if (ServerReadyPattern().IsMatch(line))
        {
            onlinePlayerNames.Clear();
            var changed = onlinePlayers != 0;
            onlinePlayers = 0;
            return changed;
        }

        if (TryParseAbsoluteOnlineCount(line, out var absoluteCount))
        {
            var changed = onlinePlayers != absoluteCount;
            onlinePlayers = absoluteCount;
            if (absoluteCount == 0 || onlinePlayerNames.Count > absoluteCount)
                onlinePlayerNames.Clear();
            return changed;
        }

        if (TryParseLastPlayerDisconnected(line))
        {
            var changed = onlinePlayers != 0 || onlinePlayerNames.Count > 0;
            onlinePlayers = 0;
            onlinePlayerNames.Clear();
            return changed;
        }

        if (TryParsePlayerJoin(line, out var joinedPlayerName))
            return ApplyPlayerJoin(joinedPlayerName, onlinePlayerNames, ref onlinePlayers);

        if (TryParsePlayerLeave(line, out var leftPlayerName))
            return ApplyPlayerLeave(leftPlayerName, onlinePlayerNames, ref onlinePlayers);

        return false;
    }

    private static bool ApplyPlayerJoin(string playerName, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        var normalizedPlayerName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedPlayerName) && !onlinePlayerNames.Add(normalizedPlayerName))
            return false;

        onlinePlayers = Math.Max(0, onlinePlayers + 1);
        if (onlinePlayerNames.Count > onlinePlayers)
            onlinePlayers = onlinePlayerNames.Count;

        return true;
    }

    private static bool ApplyPlayerLeave(string playerName, HashSet<string> onlinePlayerNames, ref int onlinePlayers)
    {
        var normalizedPlayerName = NormalizePlayerName(playerName);
        if (!string.IsNullOrWhiteSpace(normalizedPlayerName))
        {
            if (onlinePlayerNames.Remove(normalizedPlayerName))
            {
                onlinePlayers = Math.Max(0, onlinePlayers - 1);
                return true;
            }

            if (onlinePlayerNames.Count > 0 && onlinePlayers <= onlinePlayerNames.Count)
                return false;
        }

        if (onlinePlayers <= 0)
            return false;

        onlinePlayers--;
        if (onlinePlayers == 0)
            onlinePlayerNames.Clear();

        return true;
    }

    private static bool TryParsePlayerJoin(string line, out string playerName)
    {
        var match = PlayerJoinPattern().Match(line);
        playerName = match.Success ? NormalizePlayerName(match.Groups["name"].Value) : string.Empty;
        return match.Success;
    }

    private static bool TryParsePlayerLeave(string line, out string playerName)
    {
        var match = PlayerLeavePattern().Match(line);
        playerName = match.Success ? NormalizePlayerName(match.Groups["name"].Value) : string.Empty;
        return match.Success;
    }

    private static bool TryParseLastPlayerDisconnected(string line)
    {
        return LastPlayerDisconnectedPattern().IsMatch(line);
    }

    private static bool TryParseAbsoluteOnlineCount(string line, out int count)
    {
        count = 0;
        var onlineMatch = OnlineCountPattern().Match(line);
        if (!onlineMatch.Success)
            return false;

        var countCapture = onlineMatch.Groups["count"].Captures;
        var rawCount = countCapture.Count > 0 ? countCapture[^1].Value : onlineMatch.Groups["count"].Value;
        if (!int.TryParse(rawCount, out var parsed))
            return false;

        count = Math.Max(0, parsed);
        return true;
    }

    private int GetOnlinePlayerCount()
    {
        lock (_playerCountGate)
        {
            return _onlinePlayers;
        }
    }

    private void ResetOnlinePlayerTracking()
    {
        lock (_playerCountGate)
        {
            _onlinePlayers = 0;
            _onlinePlayerNames.Clear();
        }
    }

    private static string NormalizePlayerName(string? playerName)
    {
        if (string.IsNullOrWhiteSpace(playerName))
            return string.Empty;

        var normalized = playerName.Trim();
        const string playerPrefix = "Player ";
        return normalized.StartsWith(playerPrefix, StringComparison.OrdinalIgnoreCase)
            ? normalized[playerPrefix.Length..].Trim()
            : normalized;
    }

    private static async Task WriteServerConsoleCommandAsync(
        Process process,
        string command,
        CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(command + Environment.NewLine);
        await process.StandardInput.BaseStream.WriteAsync(bytes.AsMemory(0, bytes.Length), cancellationToken);
        await process.StandardInput.BaseStream.FlushAsync(cancellationToken);
    }

    private void ScheduleCommandReceiptCheck(string normalizedCommand)
    {
        if (IsCommandReceiptCheckSkipped(normalizedCommand))
            return;

        var logPath = _playerCountLogPath;
        if (string.IsNullOrWhiteSpace(logPath))
            return;

        _ = Task.Run(() => VerifyCommandReceiptAsync(normalizedCommand, logPath));
    }

    private async Task VerifyCommandReceiptAsync(string normalizedCommand, string logPath)
    {
        try
        {
            await Task.Delay(CommandAckDelayMilliseconds).ConfigureAwait(false);
            if (!File.Exists(logPath))
                return;

            var expectedCommand = CollapseWhitespace(normalizedCommand);
            if (string.IsNullOrWhiteSpace(expectedCommand))
                return;

            if (RecentLogContainsCommandReceipt(logPath, expectedCommand))
                return;

            OutputReceived?.Invoke(this,
                $"[system] 命令已写入控制通道，但 {CommandAckDelayMilliseconds / 1000} 秒内未看到服务端接收记录：{GetCommandName(expectedCommand)}。如果所有命令都无响应，通常是 Vintage Story 服务端控制台输入线程已失效，需要重启服务端。");
        }
        catch
        {
            // Command receipt diagnostics must not affect command sending.
        }
    }

    private static bool RecentLogContainsCommandReceipt(string logPath, string expectedCommand)
    {
        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var startPosition = Math.Max(0, stream.Length - CommandAckReadWindowBytes);
            stream.Seek(startPosition, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            var text = CollapseWhitespace(reader.ReadToEnd());
            return text.Contains(
                $"Handling Console Command {expectedCommand}",
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return true;
        }
    }

    private static bool IsCommandReceiptCheckSkipped(string normalizedCommand)
    {
        var commandName = GetCommandName(normalizedCommand);
        return commandName.Equals("/stop", StringComparison.OrdinalIgnoreCase)
               || commandName.Equals("/stats", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetCommandName(string command)
    {
        var compact = CollapseWhitespace(command);
        if (string.IsNullOrWhiteSpace(compact))
            return string.Empty;

        var spaceIndex = compact.IndexOf(' ', StringComparison.Ordinal);
        return spaceIndex < 0 ? compact : compact[..spaceIndex];
    }

    private static string CollapseWhitespace(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join(
            ' ',
            value.Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var previousProfileId = _currentProfile?.Id;
        _monitorCts?.Cancel();
        _monitorCts?.Dispose();
        _monitorCts = null;
        _monitorTask = null;
        _relayState = null;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = false,
            ProcessId = null,
            StartedAtUtc = null,
            ProfileId = previousProfileId,
            CpuPercent = 0,
            MemoryBytes = 0,
            OnlinePlayers = 0,
            PeakOnlinePlayers = _peakOnlinePlayers
        });

        OutputReceived?.Invoke(this, "[system] 服务器进程已退出。");
        _logger.LogInformation("Vintage Story server process exited. PreviousProfileId={ProfileId}.", previousProfileId);

        if (_process is not null)
        {
            _process.OutputDataReceived -= OnOutputDataReceived;
            _process.ErrorDataReceived -= OnOutputDataReceived;
            _process.Exited -= OnProcessExited;
            _process.Dispose();
            _process = null;
        }
    }

    private void StartMonitorLoop()
    {
        try
        {
            _monitorCts?.Cancel();
            _monitorCts?.Dispose();
        }
        catch
        {
            // ignore
        }

        _monitorCts = new CancellationTokenSource();
        _monitorTask = Task.Run(() => MonitorLoopAsync(_monitorCts.Token), CancellationToken.None);
    }

    private void ClearTrackedProcessIfTerminated()
    {
        var process = _process;
        if (process is null)
        {
            PublishStoppedStatusIfStale();
            return;
        }

        if (!IsProcessTerminated(process))
            return;

        var previousProfileId = _currentProfile?.Id;
        _canWriteStandardInput = false;
        _relayState = null;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();

        try
        {
            process.OutputDataReceived -= OnOutputDataReceived;
            process.ErrorDataReceived -= OnOutputDataReceived;
            process.Exited -= OnProcessExited;
            process.Dispose();
        }
        catch
        {
            // ignore
        }

        _process = null;

        if (_currentStatus.IsRunning)
        {
            UpdateStatus(new ServerRuntimeStatus
            {
                IsRunning = false,
                ProcessId = null,
                StartedAtUtc = null,
                ProfileId = previousProfileId,
                CpuPercent = 0,
                MemoryBytes = 0,
                OnlinePlayers = 0,
                PeakOnlinePlayers = _peakOnlinePlayers
            });
        }
    }

    private void PublishStoppedStatusIfStale()
    {
        if (!_currentStatus.IsRunning)
            return;

        var previousProfileId = _currentStatus.ProfileId ?? _currentProfile?.Id;
        _relayState = null;
        _canWriteStandardInput = false;
        _playerCountLogPath = null;
        _playerCountLogPosition = 0;
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetOnlinePlayerTracking();

        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = false,
            ProcessId = null,
            StartedAtUtc = null,
            ProfileId = previousProfileId,
            CpuPercent = 0,
            MemoryBytes = 0,
            OnlinePlayers = 0,
            PeakOnlinePlayers = _peakOnlinePlayers
        });
    }

    private void UpdateStatus(ServerRuntimeStatus status)
    {
        _currentStatus = status;
        StatusChanged?.Invoke(this, status);
    }

    private double SampleCpuPercent(Process process)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var currentProcessorTime = process.TotalProcessorTime;
            if (_lastProcessorTime == TimeSpan.Zero)
            {
                _lastProcessorTime = currentProcessorTime;
                _lastCpuSampleUtc = now;
                return _lastCpuPercent;
            }

            var elapsedMs = Math.Max(1, (now - _lastCpuSampleUtc).TotalMilliseconds);
            var processorElapsedMs = Math.Max(0, (currentProcessorTime - _lastProcessorTime).TotalMilliseconds);
            var cpu = Math.Max(0, Math.Min(100, processorElapsedMs / (elapsedMs * Environment.ProcessorCount) * 100.0));
            _lastProcessorTime = currentProcessorTime;
            _lastCpuSampleUtc = now;
            _lastCpuPercent = cpu;
            return cpu;
        }
        catch
        {
            return _lastCpuPercent;
        }
    }

    private bool HasControlChannel()
    {
        if (_canWriteStandardInput)
            return true;

        return _relayState is not null && IsProcessIdRunning(_relayState.RelayProcessId);
    }

    private string GetControlMode()
    {
        if (_relayState is not null && IsProcessIdRunning(_relayState.RelayProcessId))
            return "relay";

        return _canWriteStandardInput ? "direct" : string.Empty;
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

    private static bool IsProcessIdRunning(int processId)
    {
        if (processId <= 0)
            return false;

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
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

    private static Process? TryOpenProcess(int? processId)
    {
        if (!processId.HasValue || processId.Value <= 0)
            return null;

        try
        {
            var process = Process.GetProcessById(processId.Value);
            if (!process.HasExited)
                return process;

            process.Dispose();
            return null;
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

    private static long TryGetWorkingSet64(Process process)
    {
        try
        {
            return process.WorkingSet64;
        }
        catch
        {
            return 0;
        }
    }

    private static DateTimeOffset? TryGetStartTimeUtc(Process process)
    {
        try
        {
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        }
        catch
        {
            return null;
        }
    }

    private async Task<ServerRelayState> StartRelayAsync(
        InstanceProfile profile,
        string serverExe,
        string installPath,
        CancellationToken cancellationToken)
    {
        var launcherPath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(launcherPath) || !File.Exists(launcherPath))
            throw new InvalidOperationException("无法定位 LauncherGo 主程序，不能启动后台控制通道。");

        var pipeName = ServerRelayProtocol.CreatePipeName(profile.Id);
        var statePath = WorkspacePathHelper.GetServerRelayStatePath(profile.Id);
        TryDeleteFile(statePath);

        var startInfo = new ProcessStartInfo
        {
            FileName = launcherPath,
            WorkingDirectory = Path.GetDirectoryName(launcherPath) ?? AppContext.BaseDirectory,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add(ServerRelayProtocol.LauncherArgument);
        startInfo.ArgumentList.Add("--pipe-name");
        startInfo.ArgumentList.Add(pipeName);
        startInfo.ArgumentList.Add("--state-path");
        startInfo.ArgumentList.Add(statePath);
        startInfo.ArgumentList.Add("--server-exe");
        startInfo.ArgumentList.Add(serverExe);
        startInfo.ArgumentList.Add("--working-dir");
        startInfo.ArgumentList.Add(installPath);
        startInfo.ArgumentList.Add("--data-path");
        startInfo.ArgumentList.Add(profile.DirectoryPath);
        startInfo.ArgumentList.Add("--profile-id");
        startInfo.ArgumentList.Add(profile.Id);
        startInfo.ArgumentList.Add("--profile-name");
        startInfo.ArgumentList.Add(profile.Name);
        startInfo.ArgumentList.Add("--version");
        startInfo.ArgumentList.Add(profile.Version);

        using var relayProcess = new Process { StartInfo = startInfo };
        if (!relayProcess.Start())
            throw new InvalidOperationException("启动后台控制通道失败。");

        return await WaitForRelayStateAsync(relayProcess, statePath, pipeName, cancellationToken);
    }

    private async Task<ServerRelayState> WaitForRelayStateAsync(
        Process relayProcess,
        string statePath,
        string pipeName,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(20);
        string? lastError = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (IsProcessTerminated(relayProcess))
                throw new InvalidOperationException($"后台控制通道启动后已退出，退出码：{TryGetExitCode(relayProcess)}。");

            var state = TryReadRelayState(statePath);
            if (state is not null && state.PipeName.Equals(pipeName, StringComparison.Ordinal))
            {
                var response = await ServerRelayClient.PingAsync(pipeName, cancellationToken);
                if (response.Success && response.State is { } liveState)
                    return liveState;

                lastError = response.Error;
            }

            await Task.Delay(250, cancellationToken);
        }

        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(lastError)
                ? "等待后台控制通道就绪超时。"
                : $"等待后台控制通道就绪超时：{lastError}");
    }

    private bool TryAttachToExistingWorkspaceServerRelay(InstanceProfile? preferredProfile, bool emitOutput)
    {
        WorkspacePathHelper.EnsureWorkspace();

        string[] stateFiles;
        try
        {
            stateFiles = Directory.GetFiles(WorkspacePathHelper.ServerRelayRoot, "*.json", SearchOption.TopDirectoryOnly);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate server relay state files.");
            return false;
        }

        var profiles = SafeGetProfiles();
        Process? selectedProcess = null;
        ServerRelayState? selectedState = null;
        InstanceProfile? selectedProfile = null;
        var selectedScore = int.MinValue;
        var selectedStartedAt = DateTimeOffset.MinValue;

        foreach (var stateFile in stateFiles)
        {
            Process? process = null;
            var processSelected = false;

            try
            {
                var state = TryReadRelayState(stateFile);
                if (state is null || string.IsNullOrWhiteSpace(state.PipeName))
                {
                    TryDeleteFile(stateFile);
                    continue;
                }

                var response = ServerRelayClient.PingAsync(state.PipeName).GetAwaiter().GetResult();
                if (!response.Success)
                {
                    if (!IsProcessIdRunning(state.RelayProcessId))
                        TryDeleteFile(stateFile);
                    continue;
                }

                var liveState = response.State ?? state;
                process = TryOpenProcess(liveState.ServerProcessId);
                if (process is null || IsProcessTerminated(process))
                    continue;

                var profile = ResolveProfileForProcess(
                    preferredProfile,
                    profiles,
                    liveState.DataPath,
                    liveState.Version);
                var score = ScoreProcessMatch(preferredProfile, profile, liveState.DataPath, liveState.Version);
                var startedAt = liveState.StartedAtUtc;

                if (score < selectedScore || score == selectedScore && startedAt <= selectedStartedAt)
                    continue;

                selectedProcess?.Dispose();
                selectedProcess = process;
                selectedState = liveState;
                selectedProfile = profile;
                selectedScore = score;
                selectedStartedAt = startedAt;
                processSelected = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect server relay state file {StateFile}.", stateFile);
            }
            finally
            {
                if (!processSelected)
                    process?.Dispose();
            }
        }

        if (selectedProcess is null || selectedState is null)
            return false;

        try
        {
            AttachToRelayProcess(selectedProcess, selectedState, selectedProfile, emitOutput);
            return true;
        }
        catch (Exception ex)
        {
            selectedProcess.Dispose();
            _logger.LogDebug(ex, "Failed to attach existing Vintage Story server relay.");
            return false;
        }
    }

    private bool TryAttachToExistingWorkspaceServerProcess(InstanceProfile? preferredProfile, bool emitOutput)
    {
        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return false;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("VintagestoryServer");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to enumerate VintagestoryServer processes.");
            return false;
        }

        var profiles = SafeGetProfiles();
        Process? selectedProcess = null;
        InstanceProfile? selectedProfile = null;
        var selectedScore = int.MinValue;
        var selectedStartedAt = DateTimeOffset.MinValue;

        foreach (var candidate in candidates)
        {
            var candidateSelected = false;
            try
            {
                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue || IsProcessTerminated(candidate) || !IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;

                var commandLine = TryReadCommandLine(pid.Value);
                var dataPath = TryExtractDataPath(commandLine);
                var version = TryResolveVersionFromExecutable(candidate, serversRoot);
                var profile = ResolveProfileForProcess(preferredProfile, profiles, dataPath, version);
                var score = ScoreProcessMatch(preferredProfile, profile, dataPath, version);
                var startedAt = TryGetStartTimeUtc(candidate) ?? DateTimeOffset.MinValue;

                if (score < selectedScore || score == selectedScore && startedAt <= selectedStartedAt)
                    continue;

                selectedProcess?.Dispose();
                selectedProcess = candidate;
                selectedProfile = profile;
                selectedScore = score;
                selectedStartedAt = startedAt;
                candidateSelected = true;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Failed to inspect server process.");
            }
            finally
            {
                if (!candidateSelected)
                    candidate.Dispose();
            }
        }

        if (selectedProcess is null)
            return false;

        try
        {
            AttachToExistingProcess(selectedProcess, selectedProfile, emitOutput);
            return true;
        }
        catch (Exception ex)
        {
            selectedProcess.Dispose();
            _logger.LogDebug(ex, "Failed to attach existing Vintage Story server process.");
            return false;
        }
    }

    private IReadOnlyList<InstanceProfile> SafeGetProfiles()
    {
        if (_profileService is null)
            return [];

        try
        {
            return _profileService.GetProfiles();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read profiles while attaching existing server process.");
            return [];
        }
    }

    private void AttachToRelayState(ServerRelayState state, InstanceProfile? profile, bool emitOutput)
    {
        var process = TryOpenProcess(state.ServerProcessId)
                      ?? throw new InvalidOperationException("后台控制通道已启动，但未能打开服务端进程。");

        try
        {
            AttachToRelayProcess(process, state, profile, emitOutput);
        }
        catch
        {
            process.Dispose();
            throw;
        }
    }

    private void AttachToRelayProcess(Process process, ServerRelayState state, InstanceProfile? profile, bool emitOutput)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;

        _process = process;
        _currentProfile = profile;
        _relayState = state;
        _canWriteStandardInput = false;
        ResetOnlinePlayerTracking();
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetPlayerCountLogMonitor(profile, state.DataPath);
        StartMonitorLoop();

        var processId = TryGetProcessId(process);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = processId,
            StartedAtUtc = state.StartedAtUtc == default
                ? TryGetStartTimeUtc(process) ?? DateTimeOffset.UtcNow
                : state.StartedAtUtc,
            ProfileId = profile?.Id ?? state.ProfileId,
            CpuPercent = 0,
            MemoryBytes = TryGetWorkingSet64(process),
            OnlinePlayers = GetOnlinePlayerCount(),
            PeakOnlinePlayers = _peakOnlinePlayers,
            CanSendCommands = HasControlChannel(),
            ControlMode = GetControlMode(),
            Message = "Relay"
        });

        var profileText = profile is null ? "未识别档案" : $"档案={profile.Name}";
        var message = $"[system] 已连接后台控制通道并接管服务端，PID={processId}，Relay PID={state.RelayProcessId}，{profileText}。";
        if (emitOutput)
            OutputReceived?.Invoke(this, message);

        _logger.LogInformation(
            "Attached Vintage Story server relay. ProcessId={ProcessId}, RelayProcessId={RelayProcessId}, ProfileId={ProfileId}.",
            processId,
            state.RelayProcessId,
            profile?.Id ?? state.ProfileId);
    }

    private void AttachToExistingProcess(Process process, InstanceProfile? profile, bool emitOutput)
    {
        process.EnableRaisingEvents = true;
        process.Exited += OnProcessExited;

        _process = process;
        _currentProfile = profile;
        _relayState = null;
        _canWriteStandardInput = false;
        ResetOnlinePlayerTracking();
        _peakOnlinePlayers = 0;
        _lastProcessorTime = TimeSpan.Zero;
        _lastCpuPercent = 0;
        _lastCpuSampleUtc = DateTimeOffset.UtcNow;
        ResetPlayerCountLogMonitor(profile);
        StartMonitorLoop();

        var processId = TryGetProcessId(process);
        UpdateStatus(new ServerRuntimeStatus
        {
            IsRunning = true,
            ProcessId = processId,
            StartedAtUtc = TryGetStartTimeUtc(process) ?? DateTimeOffset.UtcNow,
            ProfileId = profile?.Id,
            CpuPercent = 0,
            MemoryBytes = TryGetWorkingSet64(process),
            OnlinePlayers = GetOnlinePlayerCount(),
            PeakOnlinePlayers = _peakOnlinePlayers,
            CanSendCommands = false,
            ControlMode = string.Empty,
            Message = "attached without control channel"
        });

        var profileText = profile is null ? "未识别档案" : $"档案={profile.Name}";
        var message = $"[system] 检测到已在运行的服务端进程并接管状态，PID={processId}，{profileText}。该进程没有可恢复控制通道，命令发送不可用。";
        if (emitOutput)
            OutputReceived?.Invoke(this, message);

        _logger.LogInformation(
            "Attached existing Vintage Story server process. ProcessId={ProcessId}, ProfileId={ProfileId}.",
            processId,
            profile?.Id);
    }

    private InstanceProfile? ResolveProfileForProcess(
        InstanceProfile? preferredProfile,
        IReadOnlyList<InstanceProfile> profiles,
        string dataPath,
        string version)
    {
        var normalizedDataPath = NormalizePath(dataPath);
        if (!string.IsNullOrWhiteSpace(normalizedDataPath))
        {
            if (preferredProfile is not null &&
                NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(preferredProfile.DirectoryPath))
                    .Equals(normalizedDataPath, StringComparison.OrdinalIgnoreCase))
            {
                return preferredProfile;
            }

            var dataPathMatch = profiles.FirstOrDefault(profile =>
                NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath))
                    .Equals(normalizedDataPath, StringComparison.OrdinalIgnoreCase));
            if (dataPathMatch is not null)
                return dataPathMatch;
        }

        if (!string.IsNullOrWhiteSpace(version))
        {
            if (preferredProfile is not null &&
                preferredProfile.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
            {
                return preferredProfile;
            }

            var versionMatches = profiles
                .Where(profile => profile.Version.Equals(version, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (versionMatches.Count == 1)
                return versionMatches[0];
        }

        return null;
    }

    private static int ScoreProcessMatch(
        InstanceProfile? preferredProfile,
        InstanceProfile? matchedProfile,
        string dataPath,
        string version)
    {
        if (preferredProfile is not null && matchedProfile is not null &&
            preferredProfile.Id.Equals(matchedProfile.Id, StringComparison.OrdinalIgnoreCase))
        {
            return !string.IsNullOrWhiteSpace(dataPath) ? 100 : 70;
        }

        if (matchedProfile is not null)
            return !string.IsNullOrWhiteSpace(dataPath) ? 90 : 50;

        return !string.IsNullOrWhiteSpace(version) ? 20 : 10;
    }

    private string TryReadCommandLine(int processId)
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
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read command line for process {ProcessId}.", processId);
        }

        return string.Empty;
    }

    private static string TryExtractDataPath(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
            return string.Empty;

        var match = DataPathArgumentPattern().Match(commandLine);
        return match.Success ? match.Groups["path"].Value : string.Empty;
    }

    private static string TryResolveVersionFromExecutable(Process process, string serversRoot)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return string.Empty;

            var executableDirectory = NormalizePath(Path.GetDirectoryName(Path.GetFullPath(executablePath)));
            if (string.IsNullOrWhiteSpace(executableDirectory) ||
                !executableDirectory.StartsWith(serversRoot, StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            return Path.GetFileName(executableDirectory) ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private string ResolveStopTargetDataPath(Process? process)
    {
        var dataPath = _relayState?.DataPath;
        if (string.IsNullOrWhiteSpace(dataPath))
            dataPath = _currentProfile?.DirectoryPath;

        if (string.IsNullOrWhiteSpace(dataPath))
        {
            var processId = process is null ? null : TryGetProcessId(process);
            if (processId.HasValue)
            {
                var commandLine = TryReadCommandLine(processId.Value);
                dataPath = TryExtractDataPath(commandLine);
            }
        }

        return NormalizeProfileDataPath(dataPath);
    }

    private int CountWorkspaceServerProcessesByDataPath(string normalizedTargetDataPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetDataPath))
            return 0;

        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return 0;

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("VintagestoryServer");
        }
        catch
        {
            return 0;
        }

        var matchedCount = 0;
        foreach (var candidate in candidates)
        {
            using (candidate)
            {
                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue)
                    continue;
                if (IsProcessTerminated(candidate))
                    continue;
                if (!IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;
                if (!IsTargetDataPathMatch(pid.Value, normalizedTargetDataPath))
                    continue;

                matchedCount++;
            }
        }

        return matchedCount;
    }

    private async Task<int> StopOrphanWorkspaceServerProcessesAsync(
        CancellationToken cancellationToken,
        int? excludePid = null,
        string? targetDataPath = null)
    {
        var serversRoot = NormalizePath(WorkspacePathHelper.ServersRoot);
        if (string.IsNullOrWhiteSpace(serversRoot))
            return 0;

        var normalizedTargetDataPath = NormalizeProfileDataPath(targetDataPath);

        Process[] candidates;
        try
        {
            candidates = Process.GetProcessesByName("VintagestoryServer");
        }
        catch
        {
            return 0;
        }

        var killedCount = 0;
        foreach (var candidate in candidates)
        {
            using (candidate)
            {
                cancellationToken.ThrowIfCancellationRequested();

                var pid = TryGetProcessId(candidate);
                if (!pid.HasValue)
                    continue;
                if (excludePid.HasValue && excludePid.Value == pid.Value)
                    continue;
                if (IsProcessTerminated(candidate))
                    continue;
                if (!IsWorkspaceServerProcess(candidate, serversRoot))
                    continue;
                if (!IsTargetDataPathMatch(pid.Value, normalizedTargetDataPath))
                    continue;

                try
                {
                    candidate.Kill(entireProcessTree: true);
                    await candidate.WaitForExitAsync(cancellationToken);
                    killedCount++;
                    OutputReceived?.Invoke(this, $"[system] 已清理孤立服务端进程，PID={pid.Value}。");
                }
                catch
                {
                    // 无法访问或终止时忽略，避免阻断主流程。
                }
            }
        }

        return killedCount;
    }

    private bool IsTargetDataPathMatch(int processId, string normalizedTargetDataPath)
    {
        if (string.IsNullOrWhiteSpace(normalizedTargetDataPath))
            return true;

        var commandLine = TryReadCommandLine(processId);
        var processDataPath = NormalizeProfileDataPath(TryExtractDataPath(commandLine));
        if (string.IsNullOrWhiteSpace(processDataPath))
            return false;

        return processDataPath.Equals(normalizedTargetDataPath, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsWorkspaceServerProcess(Process process, string serversRoot)
    {
        try
        {
            var executablePath = process.MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(executablePath))
                return false;

            var executableDirectory = Path.GetDirectoryName(Path.GetFullPath(executablePath));
            var normalizedExecutableDirectory = NormalizePath(executableDirectory);
            if (string.IsNullOrWhiteSpace(normalizedExecutableDirectory))
                return false;

            return normalizedExecutableDirectory.StartsWith(serversRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static string NormalizeProfileDataPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;

        try
        {
            return NormalizePath(WorkspacePathHelper.ResolveProfileDataPath(path));
        }
        catch
        {
            return NormalizePath(path);
        }
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

    private static bool IsSameOrChildPath(string candidatePath, string rootPath)
    {
        if (string.IsNullOrWhiteSpace(candidatePath) || string.IsNullOrWhiteSpace(rootPath))
            return false;

        if (candidatePath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
            return true;

        return candidatePath.StartsWith(rootPath + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidatePath.StartsWith(rootPath + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static ServerRelayState? TryReadRelayState(string statePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(statePath) || !File.Exists(statePath))
                return null;

            var json = File.ReadAllText(statePath);
            return JsonSerializer.Deserialize<ServerRelayState>(json, ServerRelayProtocol.JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path))
                File.Delete(path);
        }
        catch
        {
            // Stale runtime files are harmless and validated before use.
        }
    }

    private void PrepareSaveFileForStart(InstanceProfile profile)
    {
        var profileSaveRoot = NormalizePath(GetProfileSaveRoot(profile));
        var savePath = profile.ActiveSaveFile;
        if (string.IsNullOrWhiteSpace(savePath))
            savePath = Path.Combine(profileSaveRoot, "default.vcdbs");

        if (string.IsNullOrWhiteSpace(savePath))
            return;

        string fullSavePath;
        try
        {
            fullSavePath = Path.GetFullPath(savePath);
        }
        catch
        {
            return;
        }

        var saveDirectory = NormalizePath(Path.GetDirectoryName(fullSavePath));
        if (!IsSameOrChildPath(saveDirectory, profileSaveRoot))
        {
            var migratedFileName = Path.GetFileName(fullSavePath);
            if (string.IsNullOrWhiteSpace(migratedFileName))
                migratedFileName = "default.vcdbs";

            var migratedSavePath = Path.Combine(profileSaveRoot, migratedFileName);
            TryCopySaveFile(fullSavePath, migratedSavePath);
            fullSavePath = migratedSavePath;
            saveDirectory = NormalizePath(Path.GetDirectoryName(fullSavePath));
        }

        profile.ActiveSaveFile = fullSavePath;
        profile.SaveDirectory = string.IsNullOrWhiteSpace(saveDirectory)
            ? profile.SaveDirectory
            : saveDirectory;

        if (!string.IsNullOrWhiteSpace(saveDirectory))
            Directory.CreateDirectory(saveDirectory);

        ServerConfigBootstrapper.ApplySaveLocation(WorkspacePathHelper.GetProfileConfigPath(profile.DirectoryPath), fullSavePath);
        TryUpdateProfile(profile);

        if (!File.Exists(fullSavePath))
            return;

        var saveFileInfo = new FileInfo(fullSavePath);
        if (saveFileInfo.Length == 0)
        {
            File.Delete(fullSavePath);
            OutputReceived?.Invoke(this, $"[system] 检测到空存档文件，已删除并允许服务器重新生成：{fullSavePath}");
            return;
        }

        try
        {
            var builder = new SqliteConnectionStringBuilder
            {
                DataSource = fullSavePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
                Cache = SqliteCacheMode.Private
            };
            using var connection = new SqliteConnection(builder.ToString());
            connection.Open();

            var tables = ReadTables(connection);
            var hasChunks = tables.Contains("chunks", StringComparer.OrdinalIgnoreCase);
            var hasChunk = tables.Contains("chunk", StringComparer.OrdinalIgnoreCase);

            // 兼容旧的错误迁移：曾将 chunk 表改名为 chunks，导致 VS 服务器无法写入存档。
            // 这里仅在检测到 chunks 存在、chunk 缺失时回迁；不再执行 chunk -> chunks 的迁移。
            if (hasChunks && !hasChunk)
            {
                var backupPath = $"{fullSavePath}.bak-fix-{DateTime.Now:yyyyMMddHHmmss}";
                File.Copy(fullSavePath, backupPath, overwrite: false);

                using var renameCommand = connection.CreateCommand();
                renameCommand.CommandText = "ALTER TABLE chunks RENAME TO chunk;";
                renameCommand.ExecuteNonQuery();

                OutputReceived?.Invoke(this,
                    $"[system] 已自动修复存档表名 chunks -> chunk，并创建备份：{backupPath}");
            }
        }
        catch (SqliteException ex)
        {
            OutputReceived?.Invoke(this, $"[system] 存档预检查跳过（SQLite）：{ex.Message}");
        }
        catch (Exception ex)
        {
            OutputReceived?.Invoke(this, $"[system] 存档预检查跳过：{ex.Message}");
        }
        finally
        {
            SqliteConnection.ClearAllPools();
        }
    }

    private static string GetProfileSaveRoot(InstanceProfile profile)
    {
        var profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
        return Path.Combine(profileDataPath, "Saves");
    }

    private static void TryCopySaveFile(string sourceSaveFile, string targetSaveFile)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sourceSaveFile) ||
                string.IsNullOrWhiteSpace(targetSaveFile) ||
                !sourceSaveFile.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(sourceSaveFile) ||
                File.Exists(targetSaveFile))
            {
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetSaveFile)!);
            File.Copy(sourceSaveFile, targetSaveFile, overwrite: false);
        }
        catch
        {
            // 复制旧路径存档失败时仍使用档案目录下的新存档路径启动。
        }
    }

    private void TryUpdateProfile(InstanceProfile profile)
    {
        if (_profileService is null)
        {
            return;
        }

        try
        {
            profile.LastUpdatedUtc = DateTimeOffset.UtcNow;
            _profileService.UpdateProfile(profile);
        }
        catch
        {
            // 启动流程以 serverconfig 为准，索引写回失败不阻断开服。
        }
    }

    private static HashSet<string> ReadTables(SqliteConnection connection)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";

        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            if (!reader.IsDBNull(0))
                tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private static string TryReadSaveFileLocation(string profileDirectoryPath)
    {
        try
        {
            var configPath = WorkspacePathHelper.GetProfileConfigPath(profileDirectoryPath);
            if (!File.Exists(configPath))
                return string.Empty;

            using var stream = File.OpenRead(configPath);
            using var json = JsonDocument.Parse(stream);

            if (!json.RootElement.TryGetProperty("WorldConfig", out var worldConfigElement) ||
                worldConfigElement.ValueKind != JsonValueKind.Object)
                return string.Empty;

            if (!worldConfigElement.TryGetProperty("SaveFileLocation", out var saveFileElement) ||
                saveFileElement.ValueKind != JsonValueKind.String)
                return string.Empty;

            return saveFileElement.GetString() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+\[[^\]\r\n]+\]:\d+|\s+\S+:\d+)?\s+joins\.$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerJoinPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Event\]\s+(?<name>.+?)(?:\s+(?:left\.|leaves\.|got removed(?:\.|:|：))|(?:离开了游戏[。\.]?|离开了服务器[。\.]?|已被移除(?:。|\.|:|：)))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlayerLeavePattern();

    [GeneratedRegex(@"(?:\bonline\s+players?\D+(?<count>\d+))|(?:\bplayers?\s+online\D+(?<count>\d+))|(?:(?<count>\d+)\D*player(?:s|\(s\))?\D+online\b)|(?:在线(?:玩家|人数)?\D*(?<count>\d+))|(?:(?<count>\d+)\D*人\D*在线)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex OnlineCountPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Notification\]\s+Last player disconnected\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LastPlayerDisconnectedPattern();

    [GeneratedRegex(@"\[(?:Server\s+)?Event\].*now running on Port\s+\d+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ServerReadyPattern();

    [GeneratedRegex(@"--dataPath(?:=|\s+)(?:""(?<path>[^""]+)""|(?<path>\S+))", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DataPathArgumentPattern();
}

