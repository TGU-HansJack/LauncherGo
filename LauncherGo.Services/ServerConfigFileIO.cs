using System.Collections.Concurrent;
using System.Text;

namespace LauncherGo.Services;

internal static class ServerConfigFileIO
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    public static Task WriteAllTextAtomicAsync(
        string path,
        string content,
        CancellationToken cancellationToken = default)
    {
        var normalizedPath = NormalizePath(path);
        return RunExclusiveAsync(
            normalizedPath,
            () => WriteAllTextAtomicCoreAsync(normalizedPath, content, cancellationToken),
            cancellationToken);
    }

    public static void WriteAllTextAtomic(string path, string content)
    {
        var normalizedPath = NormalizePath(path);
        RunExclusive(normalizedPath, () => WriteAllTextAtomicCore(normalizedPath, content));
    }

    public static void UpdateTextFile(string path, Func<string, string?> updater)
    {
        ArgumentNullException.ThrowIfNull(updater);

        var normalizedPath = NormalizePath(path);
        RunExclusive(normalizedPath, () =>
        {
            var currentContent = File.Exists(normalizedPath)
                ? File.ReadAllText(normalizedPath)
                : string.Empty;
            var updatedContent = updater(currentContent);
            if (updatedContent is null ||
                string.Equals(currentContent, updatedContent, StringComparison.Ordinal))
            {
                return;
            }

            WriteAllTextAtomicCore(normalizedPath, updatedContent);
        });
    }

    private static async Task RunExclusiveAsync(
        string path,
        Func<Task> action,
        CancellationToken cancellationToken)
    {
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action().ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    private static void RunExclusive(string path, Action action)
    {
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            action();
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task WriteAllTextAtomicCoreAsync(
        string path,
        string content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"无效配置路径：{path}");
        Directory.CreateDirectory(directory);

        var tempPath = BuildTempPath(path);
        try
        {
            await File.WriteAllTextAsync(tempPath, content, Utf8NoBom, cancellationToken).ConfigureAwait(false);
            BackupExistingServerConfig(path);
            ReplaceTempFile(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void WriteAllTextAtomicCore(string path, string content)
    {
        var directory = Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"无效配置路径：{path}");
        Directory.CreateDirectory(directory);

        var tempPath = BuildTempPath(path);
        try
        {
            File.WriteAllText(tempPath, content, Utf8NoBom);
            BackupExistingServerConfig(path);
            ReplaceTempFile(tempPath, path);
        }
        catch
        {
            TryDelete(tempPath);
            throw;
        }
    }

    private static void ReplaceTempFile(string tempPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            File.Replace(tempPath, destinationPath, null, ignoreMetadataErrors: true);
            return;
        }

        File.Move(tempPath, destinationPath);
    }

    private static void BackupExistingServerConfig(string destinationPath)
    {
        if (!File.Exists(destinationPath) ||
            !Path.GetFileName(destinationPath).Equals("serverconfig.json", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var directory = Path.GetDirectoryName(destinationPath)
                        ?? throw new InvalidOperationException($"无效配置路径：{destinationPath}");
        var backupDirectory = Path.Combine(directory, "ConfigBackups");
        Directory.CreateDirectory(backupDirectory);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fffffff");
        var backupPath = Path.Combine(backupDirectory, $"serverconfig-{stamp}.json");
        if (File.Exists(backupPath))
        {
            backupPath = Path.Combine(backupDirectory, $"serverconfig-{stamp}-{Guid.NewGuid():N}.json");
        }

        File.Copy(destinationPath, backupPath, overwrite: false);
    }

    private static string BuildTempPath(string path)
    {
        return $"{path}.{Guid.NewGuid():N}.tmp";
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("配置路径不能为空。");
        }

        return Path.GetFullPath(path.Trim());
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 临时文件清理失败不影响主流程。
        }
    }
}
