using System.Diagnostics;
using System.IO.Compression;
using System.Text;

namespace LauncherGo.Services;

internal static class StratumPackageInstaller
{
    private const int PrepareTimeoutMilliseconds = 180_000;

    public static bool IsPrepared(string installPath)
    {
        return File.Exists(Path.Combine(installPath, "StratumServer.exe")) &&
               File.Exists(Path.Combine(installPath, ".stratum-base")) &&
               File.Exists(Path.Combine(installPath, ".stratum-patched-files"));
    }

    public static void OverlayAndPrepare(
        string packagePath,
        string installPath,
        string baseVersion,
        string tempRoot)
    {
        var extractRoot = Path.Combine(tempRoot, "stratum-extract");
        Directory.CreateDirectory(extractRoot);
        ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: true);

        var extractedExecutable = Directory
            .EnumerateFiles(extractRoot, "StratumServer.exe", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(extractedExecutable))
        {
            throw new InvalidOperationException("Stratum 压缩包内未找到 StratumServer.exe。");
        }

        var packageRoot = Path.GetDirectoryName(extractedExecutable)
                          ?? throw new InvalidOperationException("无法识别 Stratum 压缩包目录。");
        CopyDirectoryContents(packageRoot, installPath);

        File.WriteAllText(Path.Combine(installPath, ".stratum-base"), baseVersion);
        RunPrepare(Path.Combine(installPath, "StratumServer.exe"), installPath);

        if (!IsPrepared(installPath))
        {
            throw new InvalidOperationException("Stratum 本地补丁准备未完成。");
        }
    }

    private static void CopyDirectoryContents(string sourceRoot, string targetRoot)
    {
        foreach (var sourcePath in Directory.EnumerateFiles(sourceRoot, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceRoot, sourcePath);
            var targetPath = Path.Combine(targetRoot, relativePath);
            var targetDirectory = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrWhiteSpace(targetDirectory))
            {
                Directory.CreateDirectory(targetDirectory);
            }

            File.Copy(sourcePath, targetPath, overwrite: true);
        }
    }

    private static void RunPrepare(string executablePath, string installPath)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                WorkingDirectory = installPath,
                Arguments = "--stratum-prepare-only --stratum-no-banner",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };

        var output = new StringBuilder();
        process.OutputDataReceived += (_, args) => AppendLine(output, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLine(output, args.Data);

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        if (!process.WaitForExit(PrepareTimeoutMilliseconds))
        {
            TryKill(process);
            throw new InvalidOperationException("Stratum 本地补丁准备超时。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Stratum 本地补丁准备失败，退出码 {process.ExitCode}。{output.ToString().Trim()}");
        }
    }

    private static void AppendLine(StringBuilder builder, string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            builder.AppendLine(line);
        }
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // The timeout error is more useful than a secondary kill failure.
        }
    }
}
