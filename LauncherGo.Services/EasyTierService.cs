using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace LauncherGo.Services;

/// <summary>
///     EasyTier 服务端房间进程管理。
/// </summary>
public sealed class EasyTierService : IEasyTierService
{
    private const int ReadyTimeoutSeconds = 30;
    private readonly SemaphoreSlim _processGate = new(1, 1);
    private readonly ILauncherPreferencesService? _preferencesService;
    private readonly ILogger<EasyTierService> _logger;
    private Process? _process;
    private MvlRoomHost? _roomHost;
    private EasyTierRoomSession? _roomSession;
    private EasyTierRuntimeStatus _currentStatus = new();
    private bool _isStopping;

    public EasyTierService()
        : this(null, NullLogger<EasyTierService>.Instance)
    {
    }

    public EasyTierService(ILogger<EasyTierService>? logger = null)
        : this(null, logger)
    {
    }

    public EasyTierService(
        ILauncherPreferencesService? preferencesService,
        ILogger<EasyTierService>? logger = null)
    {
        _preferencesService = preferencesService;
        _logger = logger ?? NullLogger<EasyTierService>.Instance;
    }

    public event EventHandler<EasyTierRuntimeStatus>? StatusChanged;

    public string CoreExecutablePath => WorkspacePathHelper.EasyTierCoreExecutablePath;

    public string CliExecutablePath => WorkspacePathHelper.EasyTierCliExecutablePath;

    public EasyTierRuntimeStatus GetCurrentStatus()
    {
        if (!_processGate.Wait(0))
        {
            return CloneStatus(_currentStatus);
        }

        try
        {
            ClearTerminatedProcess();
            return CloneStatus(_currentStatus);
        }
        finally
        {
            _processGate.Release();
        }
    }

    public Task ImportCoreExecutableAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        ImportExecutableAsync(sourcePath, CoreExecutablePath, "EasyTier Core", cancellationToken);

    public Task ImportCliExecutableAsync(string sourcePath, CancellationToken cancellationToken = default) =>
        ImportExecutableAsync(sourcePath, CliExecutablePath, "EasyTier CLI", cancellationToken);

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ClearTerminatedProcess();
            if (_process is not null && !IsTerminated(_process))
            {
                return;
            }

            WorkspacePathHelper.EnsureWorkspace();
            Directory.CreateDirectory(WorkspacePathHelper.EasyTierRoot);

            if (!File.Exists(CoreExecutablePath))
            {
                throw new FileNotFoundException("未找到 EasyTier Core，请先导入 easytier-core.exe。", CoreExecutablePath);
            }

            if (!File.Exists(CliExecutablePath))
            {
                throw new FileNotFoundException("未找到 EasyTier CLI，请先导入 easytier-cli.exe。", CliExecutablePath);
            }

            var settings = GetCurrentSettings();
            var gamePort = Math.Clamp(settings.GamePort, 1, ushort.MaxValue);
            var hasCustomNetworkName = !string.IsNullOrWhiteSpace(settings.NetworkName);
            var hasCustomNetworkSecret = !string.IsNullOrWhiteSpace(settings.NetworkSecret);
            if (hasCustomNetworkName != hasCustomNetworkSecret)
            {
                throw new InvalidOperationException("自定义网络名称和网络密钥必须同时填写。");
            }

            var useMvlRoom = !hasCustomNetworkName;
            var controlPort = useMvlRoom ? GetAvailablePort() : 0;
            _roomSession = useMvlRoom
                ? EasyTierRoomCode.Create(settings.RoomPrefix, checked((ushort)controlPort))
                : null;
            var rpcPort = GetAvailablePort(controlPort);
            var networkName = _roomSession?.NetworkName ?? settings.NetworkName.Trim();
            var networkSecret = _roomSession?.NetworkSecret ?? settings.NetworkSecret.Trim();
            var hostName = useMvlRoom ? networkName : settings.Hostname.Trim();
            if (string.IsNullOrWhiteSpace(hostName))
            {
                hostName = "LauncherGo-vs-server";
            }

            var process = CreateCoreProcess(
                settings,
                networkName,
                networkSecret,
                hostName,
                rpcPort,
                controlPort,
                gamePort);
            process.OutputDataReceived += OnProcessOutput;
            process.ErrorDataReceived += OnProcessOutput;
            process.Exited += OnProcessExited;
            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("未能启动 EasyTier Core 进程。");
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            _process = process;
            _currentStatus = new EasyTierRuntimeStatus
            {
                IsRunning = true,
                IsReady = false,
                ProcessId = process.Id,
                StartedAtUtc = DateTimeOffset.UtcNow,
                RpcPort = rpcPort,
                ControlPort = controlPort,
                NetworkName = networkName,
                ExecutablePath = CoreExecutablePath
            };
            NotifyStatusChanged();

            var localPeer = await WaitForLocalPeerAsync(process, rpcPort, cancellationToken).ConfigureAwait(false);
            var peers = await GetPeersAsync(rpcPort, cancellationToken).ConfigureAwait(false);
            if (useMvlRoom && _roomSession is not null)
            {
                _roomHost = new MvlRoomHost(
                    _roomSession.ControlPort,
                    new MvlRoomPlayerInfo(
                        MvlRoomType.Host,
                        hostName,
                        checked((ushort)gamePort),
                        localPeer.Ipv4,
                        localPeer.Version)
                    {
                        Identity = localPeer.Id,
                        LastHeartbeat = DateTimeOffset.UtcNow
                    },
                    (message, error) => _logger.LogError(error, "{Message}", message));
                _roomHost.GuestCountChanged += OnRoomGuestCountChanged;
            }

            _currentStatus = new EasyTierRuntimeStatus
            {
                IsRunning = true,
                IsReady = true,
                ProcessId = process.Id,
                StartedAtUtc = _currentStatus.StartedAtUtc,
                RpcPort = rpcPort,
                ControlPort = controlPort,
                ConnectedPeerCount = Math.Max(0, peers.Count - 1),
                LocalIpV4 = localPeer.Ipv4,
                NetworkName = networkName,
                GameAddress = string.IsNullOrWhiteSpace(localPeer.Ipv4)
                    ? string.Empty
                    : $"{localPeer.Ipv4}:{gamePort}",
                ExecutablePath = CoreExecutablePath
            };

            _roomHost?.Start();
            _currentStatus.RoomCode = _roomSession?.Code ?? string.Empty;
            NotifyStatusChanged();

            _logger.LogInformation(
                "EasyTier started. ProcessId={ProcessId}, NetworkName={NetworkName}, RpcPort={RpcPort}, ControlPort={ControlPort}.",
                process.Id,
                networkName,
                rpcPort,
                controlPort);
        }
        catch (Exception ex)
        {
            await StopTrackedProcessAsync(TimeSpan.FromSeconds(2), CancellationToken.None).ConfigureAwait(false);
            _currentStatus = new EasyTierRuntimeStatus
            {
                LastError = ex.Message,
                ExecutablePath = CoreExecutablePath
            };
            NotifyStatusChanged();
            throw;
        }
        finally
        {
            _processGate.Release();
        }
    }

    public async Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _isStopping = true;
            await StopTrackedProcessAsync(gracefulTimeout, cancellationToken).ConfigureAwait(false);
            _currentStatus = new EasyTierRuntimeStatus
            {
                ExecutablePath = CoreExecutablePath
            };
            NotifyStatusChanged();
        }
        finally
        {
            _isStopping = false;
            _processGate.Release();
        }
    }

    private async Task ImportExecutableAsync(
        string sourcePath,
        string targetPath,
        string executableName,
        CancellationToken cancellationToken)
    {
        await _processGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ClearTerminatedProcess();
            if (_process is not null && !IsTerminated(_process))
            {
                throw new InvalidOperationException("请先停止 EasyTier 后再导入可执行文件。");
            }

            var fullSourcePath = NormalizePath(sourcePath);
            if (string.IsNullOrWhiteSpace(fullSourcePath) || !File.Exists(fullSourcePath))
            {
                throw new FileNotFoundException($"未找到 {executableName} 可执行文件。", sourcePath);
            }

            WorkspacePathHelper.EnsureWorkspace();
            Directory.CreateDirectory(WorkspacePathHelper.EasyTierRoot);
            var fullTargetPath = NormalizePath(targetPath);
            if (!fullSourcePath.Equals(fullTargetPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Copy(fullSourcePath, fullTargetPath, overwrite: true);
            }

            CopyOptionalNativeDependencies(fullSourcePath, fullTargetPath);
        }
        finally
        {
            _processGate.Release();
        }
    }

    private Process CreateCoreProcess(
        EasyTierIntegrationSettings settings,
        string networkName,
        string networkSecret,
        string hostName,
        int rpcPort,
        int controlPort,
        int gamePort)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CoreExecutablePath,
                WorkingDirectory = WorkspacePathHelper.EasyTierRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };

        var arguments = process.StartInfo.ArgumentList;
        arguments.Add("--no-tun");
        arguments.Add("--multi-thread");
        if (settings.Compression)
        {
            arguments.Add("--compression=zstd");
        }

        if (settings.LatencyFirst)
        {
            arguments.Add("--latency-first");
        }

        if (settings.EnableKcpProxy)
        {
            arguments.Add("--enable-kcp-proxy");
        }

        arguments.Add("-l");
        arguments.Add("tcp://0.0.0.0:0");
        arguments.Add("-l");
        arguments.Add("udp://0.0.0.0:0");
        arguments.Add("--network-name");
        arguments.Add(networkName);
        arguments.Add("--network-secret");
        arguments.Add(networkSecret);
        arguments.Add("--hostname");
        arguments.Add(hostName);
        arguments.Add("--ipv4");
        arguments.Add(settings.Ipv4Address);
        arguments.Add($"--tcp-whitelist={gamePort}");
        if (controlPort > 0)
        {
            arguments.Add($"--tcp-whitelist={controlPort}");
        }

        if (settings.EnableUdp)
        {
            arguments.Add($"--udp-whitelist={gamePort}");
        }

        foreach (var peer in ParsePeerNodes(settings.PeerNodesText))
        {
            arguments.Add("-p");
            arguments.Add(peer);
        }

        arguments.Add("-r");
        arguments.Add(rpcPort.ToString(CultureInfo.InvariantCulture));
        arguments.Add("--file-log-size");
        arguments.Add("1");
        arguments.Add("--file-log-level");
        arguments.Add("info");
        return process;
    }

    private async Task<EasyTierPeerSnapshot> WaitForLocalPeerAsync(
        Process process,
        int rpcPort,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(ReadyTimeoutSeconds);
        Exception? lastException = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsTerminated(process))
            {
                throw new InvalidOperationException("EasyTier Core 启动后立即退出，请检查节点和网络配置。");
            }

            try
            {
                var peers = await GetPeersAsync(rpcPort, cancellationToken).ConfigureAwait(false);
                if (peers.Count > 1)
                {
                    var local = peers.FirstOrDefault(peer =>
                        peer.Cost.Equals("Local", StringComparison.OrdinalIgnoreCase));
                    if (local is { Id: > 0 } && TryGetIpv4Address(local, out var localIpv4))
                    {
                        local.Ipv4 = localIpv4;
                        return local;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                lastException = ex;
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        throw new InvalidOperationException(
            lastException is null
                ? "EasyTier 未在超时时间内连接到引导/中继节点。"
                : $"EasyTier 未在超时时间内就绪：{lastException.Message}");
    }

    private async Task<List<EasyTierPeerSnapshot>> GetPeersAsync(int rpcPort, CancellationToken cancellationToken)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = CliExecutablePath,
                WorkingDirectory = WorkspacePathHelper.EasyTierRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            }
        };
        process.StartInfo.ArgumentList.Add("-p");
        process.StartInfo.ArgumentList.Add($"127.0.0.1:{rpcPort}");
        process.StartInfo.ArgumentList.Add("-o");
        process.StartInfo.ArgumentList.Add("json");
        process.StartInfo.ArgumentList.Add("peer");
        if (!process.Start())
        {
            throw new InvalidOperationException("未能启动 EasyTier CLI。");
        }

        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var output = await outputTask.ConfigureAwait(false);
        var error = await errorTask.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? "EasyTier CLI 查询失败。"
                : error.Trim());
        }

        var json = ExtractJsonArray(output);
        return JsonSerializer.Deserialize<List<EasyTierPeerSnapshot>>(json, EasyTierJsonOptions) ?? [];
    }

    private async Task StopTrackedProcessAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken)
    {
        StopRoomHost();

        var process = _process;
        _process = null;
        _roomSession = null;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!IsTerminated(process))
            {
                process.Kill(entireProcessTree: true);
                var exitTask = process.WaitForExitAsync(cancellationToken);
                await Task.WhenAny(exitTask, Task.Delay(gracefulTimeout, cancellationToken)).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // Process already exited.
        }
        finally
        {
            process.OutputDataReceived -= OnProcessOutput;
            process.ErrorDataReceived -= OnProcessOutput;
            process.Exited -= OnProcessExited;
            process.Dispose();
        }
    }

    private void StopRoomHost()
    {
        if (_roomHost is null)
        {
            return;
        }

        _roomHost.GuestCountChanged -= OnRoomGuestCountChanged;
        _roomHost.Dispose();
        _roomHost = null;
    }

    private void ClearTerminatedProcess()
    {
        var process = _process;
        if (process is null || !IsTerminated(process))
        {
            return;
        }

        process.OutputDataReceived -= OnProcessOutput;
        process.ErrorDataReceived -= OnProcessOutput;
        process.Exited -= OnProcessExited;
        process.Dispose();
        _process = null;
        _roomSession = null;
        StopRoomHost();
        _currentStatus = new EasyTierRuntimeStatus
        {
            LastError = _isStopping ? string.Empty : "EasyTier Core 已退出。",
            ExecutablePath = CoreExecutablePath
        };
        NotifyStatusChanged();
    }

    private void OnProcessOutput(object sender, DataReceivedEventArgs eventArgs)
    {
        if (!string.IsNullOrWhiteSpace(eventArgs.Data))
        {
            _logger.LogDebug("[easytier] {Line}", eventArgs.Data);
        }
    }

    private void OnProcessExited(object? sender, EventArgs eventArgs)
    {
        _ = HandleUnexpectedExitAsync(sender as Process);
    }

    private async Task HandleUnexpectedExitAsync(Process? exitedProcess)
    {
        await _processGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (exitedProcess is null || !ReferenceEquals(_process, exitedProcess))
            {
                return;
            }

            ClearTerminatedProcess();
        }
        finally
        {
            _processGate.Release();
        }
    }

    private void OnRoomGuestCountChanged(object? sender, int guestCount)
    {
        _currentStatus.ConnectedPlayerCount = guestCount;
        NotifyStatusChanged();
    }

    private EasyTierIntegrationSettings GetCurrentSettings()
    {
        if (_preferencesService is null)
        {
            return new EasyTierIntegrationSettings();
        }

        return _preferencesService.Load().EasyTier ?? new EasyTierIntegrationSettings();
    }

    private static IEnumerable<string> ParsePeerNodes(string? text) =>
        (text ?? string.Empty)
        .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Where(static peer => !string.IsNullOrWhiteSpace(peer) && !peer.StartsWith('#'))
        .Distinct(StringComparer.OrdinalIgnoreCase);

    private static int GetAvailablePort(int excludedPort = 0)
    {
        while (true)
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            if (port != excludedPort)
            {
                return port;
            }
        }
    }

    private static bool TryGetIpv4Address(EasyTierPeerSnapshot peer, out string ipv4)
    {
        foreach (var rawValue in new[] { peer.Ipv4, peer.Cidr })
        {
            var value = rawValue?.Trim() ?? string.Empty;
            var subnetSeparator = value.IndexOf('/');
            if (subnetSeparator >= 0)
            {
                value = value[..subnetSeparator];
            }

            if (IPAddress.TryParse(value, out var address) && address.AddressFamily == AddressFamily.InterNetwork)
            {
                ipv4 = address.ToString();
                return true;
            }
        }

        ipv4 = string.Empty;
        return false;
    }

    private static void CopyOptionalNativeDependencies(string sourcePath, string targetPath)
    {
        var sourceDirectory = Path.GetDirectoryName(sourcePath);
        var targetDirectory = Path.GetDirectoryName(targetPath);
        if (string.IsNullOrWhiteSpace(sourceDirectory) || string.IsNullOrWhiteSpace(targetDirectory))
        {
            return;
        }

        const string packetDll = "Packet.dll";
        var targetDependencyPath = Path.Combine(targetDirectory, packetDll);
        var sourceDependencyPath = new[]
            {
                Path.Combine(sourceDirectory, packetDll),
                Path.Combine(sourceDirectory, "native", packetDll),
                Path.Combine(sourceDirectory, "..", "native", packetDll)
            }
            .Select(NormalizePath)
            .FirstOrDefault(File.Exists);
        if (!string.IsNullOrWhiteSpace(sourceDependencyPath) &&
            !sourceDependencyPath.Equals(targetDependencyPath, StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourceDependencyPath, targetDependencyPath, overwrite: true);
        }
    }

    private static bool IsTerminated(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ExtractJsonArray(string value)
    {
        var start = value.IndexOf('[', StringComparison.Ordinal);
        var end = value.LastIndexOf(']');
        if (start < 0 || end < start)
        {
            throw new InvalidOperationException("EasyTier CLI 未返回节点 JSON。");
        }

        return value[start..(end + 1)];
    }

    private void NotifyStatusChanged()
    {
        StatusChanged?.Invoke(this, CloneStatus(_currentStatus));
    }

    private static EasyTierRuntimeStatus CloneStatus(EasyTierRuntimeStatus status) => new()
    {
        IsRunning = status.IsRunning,
        IsReady = status.IsReady,
        ProcessId = status.ProcessId,
        StartedAtUtc = status.StartedAtUtc,
        RpcPort = status.RpcPort,
        ControlPort = status.ControlPort,
        ConnectedPeerCount = status.ConnectedPeerCount,
        ConnectedPlayerCount = status.ConnectedPlayerCount,
        LocalIpV4 = status.LocalIpV4,
        NetworkName = status.NetworkName,
        RoomCode = status.RoomCode,
        GameAddress = status.GameAddress,
        LastError = status.LastError,
        ExecutablePath = status.ExecutablePath
    };

    private static readonly JsonSerializerOptions EasyTierJsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    internal sealed class EasyTierPeerSnapshot
    {
        [JsonPropertyName("hostname")]
        public string HostName { get; set; } = string.Empty;

        [JsonPropertyName("ipv4")]
        public string Ipv4 { get; set; } = string.Empty;

        [JsonPropertyName("cidr")]
        public string Cidr { get; set; } = string.Empty;

        [JsonPropertyName("version")]
        public string Version { get; set; } = string.Empty;

        [JsonPropertyName("id")]
        [JsonNumberHandling(JsonNumberHandling.AllowReadingFromString)]
        public uint Id { get; set; }

        [JsonPropertyName("cost")]
        public string Cost { get; set; } = string.Empty;
    }
}
