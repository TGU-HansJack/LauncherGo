using System.Collections.Concurrent;
using System.Text;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     服务器日志跟随服务默认实现
/// </summary>
public sealed class LogTailService : ILogTailService
{
    private const int MaxReplayLogBytes = 256 * 1024;
    private const int MaxReplayLogLines = 200;

    private readonly ConcurrentDictionary<string, TailState> _tails = new(StringComparer.OrdinalIgnoreCase);

    /// <inheritdoc />
    public event EventHandler<string>? LogLineReceived;

    /// <inheritdoc />
    public event EventHandler<ProfileLogLine>? ProfileLogLineReceived;

    /// <inheritdoc />
    public async Task StartAsync(InstanceProfile profile, bool replayExisting = false, CancellationToken cancellationToken = default)
    {
        if (profile is null)
        {
            throw new ArgumentNullException(nameof(profile));
        }

        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            return;
        }

        await StopAsync(profile.Id, cancellationToken);

        var profileDataPath = WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath);
        var logsPath = WorkspacePathHelper.GetProfileLogsPath(profileDataPath);
        Directory.CreateDirectory(logsPath);

        var mainLogPath = WorkspacePathHelper.GetServerMainLogPath(profileDataPath);
        var trackedLogPaths = ResolveTrackedLogPaths(mainLogPath);
        var positions = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (replayExisting)
        {
            ReplayExistingLogs(profile, trackedLogPaths, cancellationToken);
        }

        foreach (var logPath in trackedLogPaths)
        {
            positions[logPath] = GetExistingLogLength(logPath);
        }

        var state = new TailState(
            CloneProfile(profile),
            trackedLogPaths,
            positions,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        if (!_tails.TryAdd(profile.Id, state))
        {
            await state.DisposeAsync(cancellationToken);
            return;
        }

        state.RunTask = TailLoopAsync(state);
    }

    /// <inheritdoc />
    public async Task StopAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return;
        }

        if (_tails.TryRemove(profileId.Trim(), out var state))
        {
            await state.DisposeAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        var states = _tails.ToArray();
        _tails.Clear();

        foreach (var (_, state) in states)
        {
            await state.DisposeAsync(cancellationToken);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        foreach (var (_, state) in _tails)
        {
            state.Cancellation.Cancel();
            state.Cancellation.Dispose();
        }

        _tails.Clear();
    }

    private async Task TailLoopAsync(TailState state)
    {
        var cancellationToken = state.Cancellation.Token;
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (state.TrackedLogPaths.Count == 0)
                {
                    await Task.Delay(1000, cancellationToken);
                    continue;
                }

                foreach (var logPath in state.TrackedLogPaths)
                {
                    await TailSingleLogAsync(state, logPath, cancellationToken);
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

    private async Task TailSingleLogAsync(TailState state, string logPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return;
        }

        state.Positions.TryGetValue(logPath, out var position);

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

            EmitLine(state.Profile, line);
        }

        state.Positions[logPath] = stream.Position;
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

    private void ReplayExistingLogs(InstanceProfile profile, IReadOnlyList<string> logPaths, CancellationToken cancellationToken)
    {
        foreach (var logPath in logPaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var line in ReadTailLines(logPath, MaxReplayLogBytes, MaxReplayLogLines, cancellationToken))
            {
                EmitLine(profile, line);
            }
        }
    }

    private void EmitLine(InstanceProfile profile, string line)
    {
        if (string.IsNullOrWhiteSpace(line) || ServerLogPrivacyFilter.ShouldSuppressConsoleLogLine(line))
        {
            return;
        }

        LogLineReceived?.Invoke(this, line);
        ProfileLogLineReceived?.Invoke(this, new ProfileLogLine
        {
            ProfileId = profile.Id,
            ProfileName = profile.Name,
            Line = line,
            TimestampUtc = DateTimeOffset.UtcNow
        });
    }

    private static IReadOnlyList<string> ReadTailLines(
        string logPath,
        int maxBytes,
        int maxLines,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return [];
        }

        try
        {
            using var stream = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var start = Math.Max(0, stream.Length - maxBytes);
            stream.Seek(start, SeekOrigin.Begin);

            using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            if (start > 0)
            {
                _ = reader.ReadLine();
            }

            var lines = new Queue<string>();
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = reader.ReadLine();
                if (line is null)
                {
                    break;
                }

                lines.Enqueue(line);
                while (lines.Count > maxLines)
                {
                    lines.Dequeue();
                }
            }

            return lines.ToArray();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return [];
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

    private static InstanceProfile CloneProfile(InstanceProfile profile)
    {
        return new InstanceProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Version = profile.Version,
            DirectoryPath = profile.DirectoryPath,
            SaveDirectory = profile.SaveDirectory,
            ActiveSaveFile = profile.ActiveSaveFile,
            CreatedAtUtc = profile.CreatedAtUtc,
            LastUpdatedUtc = profile.LastUpdatedUtc
        };
    }

    private sealed class TailState(
        InstanceProfile profile,
        IReadOnlyList<string> trackedLogPaths,
        Dictionary<string, long> positions,
        CancellationTokenSource cancellation)
    {
        public InstanceProfile Profile { get; } = profile;

        public IReadOnlyList<string> TrackedLogPaths { get; } = trackedLogPaths;

        public Dictionary<string, long> Positions { get; } = positions;

        public CancellationTokenSource Cancellation { get; } = cancellation;

        public Task? RunTask { get; set; }

        public async Task DisposeAsync(CancellationToken cancellationToken)
        {
            try
            {
                await Cancellation.CancelAsync();
                if (RunTask is not null)
                {
                    await RunTask.WaitAsync(cancellationToken);
                }
            }
            catch (OperationCanceledException)
            {
                // ignore
            }
            finally
            {
                Cancellation.Dispose();
            }
        }
    }
}
