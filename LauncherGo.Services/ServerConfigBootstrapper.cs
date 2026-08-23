using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

internal static class ServerConfigBootstrapper
{
    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public static void EnsureGenerated(string installPath, string profileDataPath, bool forceRegenerate = false)
    {
        var saveDirectory = Path.Combine(profileDataPath, "Saves");
        EnsureGenerated(installPath, new InstanceProfile
        {
            Id = Path.GetFileName(profileDataPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            Name = Path.GetFileName(profileDataPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
            DirectoryPath = profileDataPath,
            SaveDirectory = saveDirectory,
            ActiveSaveFile = Path.Combine(saveDirectory, "default.vcdbs")
        }, forceRegenerate);
    }

    public static void EnsureGenerated(string installPath, InstanceProfile profile, bool forceRegenerate = false)
    {
        var configPath = Path.Combine(profile.DirectoryPath, "serverconfig.json");
        if (File.Exists(configPath) && !forceRegenerate)
        {
            ApplyLocalizedLanguage(configPath);
            ApplySaveLocation(configPath, profile.ActiveSaveFile);
            return;
        }

        var serverExe = LauncherWorkspacePathHelper.ResolveServerExecutablePath(installPath);
        if (!File.Exists(serverExe))
        {
            throw new InvalidOperationException($"未找到服务端程序：{serverExe}");
        }

        Directory.CreateDirectory(profile.DirectoryPath);
        Directory.CreateDirectory(profile.SaveDirectory);

        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = serverExe,
                WorkingDirectory = installPath,
                Arguments = $"--genconfig --dataPath \"{profile.DirectoryPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                UseShellExecute = false
            }
        };

        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, args) =>
        {
            if (!string.IsNullOrWhiteSpace(args.Data))
            {
                stderr.AppendLine(args.Data);
            }
        };

        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        var timeoutMilliseconds = Path.GetFileName(serverExe).Equals(
            StringComparison.OrdinalIgnoreCase)
            ? 60_000
            : 30_000;
        if (!process.WaitForExit(timeoutMilliseconds))
        {
            TryKill(process);
            throw new InvalidOperationException("生成 serverconfig 超时。");
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"生成 serverconfig 失败，退出码 {process.ExitCode}。{stderr.ToString().Trim()}");
        }

        if (!File.Exists(configPath))
        {
            throw new InvalidOperationException("服务端未生成 serverconfig.json。");
        }

        ApplyLocalizedLanguage(configPath);
        ApplySaveLocation(configPath, profile.ActiveSaveFile);
    }

    public static void ApplySaveLocation(string configPath, string saveFilePath)
    {
        if (string.IsNullOrWhiteSpace(saveFilePath) || !File.Exists(configPath))
        {
            return;
        }

        try
        {
            var normalizedSavePath = Path.GetFullPath(saveFilePath);
            ServerConfigFileIO.UpdateTextFile(configPath, currentJson =>
            {
                if (string.IsNullOrWhiteSpace(currentJson) ||
                    JsonNode.Parse(currentJson) is not JsonObject root)
                {
                    return null;
                }

                if (root["WorldConfig"] is not JsonObject worldConfig)
                {
                    worldConfig = [];
                    root["WorldConfig"] = worldConfig;
                }

                var currentSavePath = worldConfig["SaveFileLocation"]?.GetValue<string>() ?? string.Empty;
                if (normalizedSavePath.Equals(currentSavePath, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                worldConfig["SaveFileLocation"] = normalizedSavePath;
                return root.ToJsonString(JsonWriteOptions);
            });
        }
        catch
        {
            // 失败时保持原配置，启动过程会继续使用服务端默认逻辑。
        }
    }

    private static void ApplyLocalizedLanguage(string configPath)
    {
        try
        {
            var language = CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh-cn"
                : "en";
            ServerConfigFileIO.UpdateTextFile(configPath, currentJson =>
            {
                if (string.IsNullOrWhiteSpace(currentJson) ||
                    JsonNode.Parse(currentJson) is not JsonObject root)
                {
                    return null;
                }

                var current = root["ServerLanguage"]?.GetValue<string>() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(current) &&
                    (!language.Equals("zh-cn", StringComparison.OrdinalIgnoreCase) ||
                     !current.Equals("en", StringComparison.OrdinalIgnoreCase)))
                {
                    return null;
                }

                if (language.Equals(current, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                root["ServerLanguage"] = language;
                return root.ToJsonString(JsonWriteOptions);
            });
        }
        catch
        {
            // 本地化默认值失败不影响实例创建。
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
            // ignored
        }
    }
}
