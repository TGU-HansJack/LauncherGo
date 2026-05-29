using System.Text;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     服务器日志跟随服务默认实现
/// </summary>
public class LogTailService : ILogTailService
{
    private CancellationTokenSource? _cts;
    private Task? _tailTask;
    private IReadOnlyList<string> _trackedLogPaths = [];
    private readonly Dictionary<string, long> _positions = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public event EventHandler<string>? LogLineReceived;

    /// <inheritdoc />
    public async Task StartAsync(InstanceProfile profile, CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);

        var profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
        var logsPath = WorkspacePathHelper.GetProfileLogsPath(profileDataPath);
        Directory.CreateDirectory(logsPath);

        var mainLogPath = WorkspacePathHelper.GetServerMainLogPath(profileDataPath);
        _trackedLogPaths = ResolveTrackedLogPaths(mainLogPath);
        _positions.Clear();
        foreach (var logPath in _trackedLogPaths)
        {
            _positions[logPath] = GetExistingLogLength(logPath);
        }

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _tailTask = TailLoopAsync(_cts.Token);
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        if (_cts is null) return;

        try
        {
            await _cts.CancelAsync();
            if (_tailTask is not null)
                await _tailTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // ignore
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _tailTask = null;
            _trackedLogPaths = [];
            _positions.Clear();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _cts?.Cancel();
        _cts?.Dispose();
    }

    private async Task TailLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (_trackedLogPaths.Count == 0)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                foreach (var logPath in _trackedLogPaths)
                {
                    await TailSingleLogAsync(logPath, cancellationToken);
                }

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

    private async Task TailSingleLogAsync(string logPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return;
        }

        _positions.TryGetValue(logPath, out var position);

        await using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        if (stream.Length < position)
        {
            position = 0;
        }

        stream.Seek(position, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (true)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
            {
                break;
            }

            if (!string.IsNullOrWhiteSpace(line) && !ServerLogPrivacyFilter.ShouldSuppressConsoleLogLine(line))
            {
                LogLineReceived?.Invoke(this, line);
            }
        }

        _positions[logPath] = stream.Position;
    }

    private static long GetExistingLogLength(string logPath)
    {
        try
        {
            return File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    private static IReadOnlyList<string> ResolveTrackedLogPaths(string mainLogPath)
    {
        if (string.IsNullOrWhiteSpace(mainLogPath))
        {
            return [];
        }

        var directory = Path.GetDirectoryName(mainLogPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return [mainLogPath];
        }

        var candidates = new[]
        {
            mainLogPath,
            Path.Combine(directory, "server-chat.log"),
            Path.Combine(directory, "server-audit.log")
        };

        return candidates
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}

