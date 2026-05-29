using System.Text.RegularExpressions;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

internal static partial class WorkspacePathHelper
{
    public static string WorkspaceRoot => LauncherPathHelper.AppRoot;

    public static string RobotRoot => Path.Combine(WorkspaceRoot, "qqbot");

    public static string RobotSettingsPath => Path.Combine(RobotRoot, "vs2qq-settings.json");

    public static void EnsureWorkspace()
    {
        Directory.CreateDirectory(WorkspaceRoot);
        Directory.CreateDirectory(RobotRoot);
    }

    public static string GetProfileConfigPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "serverconfig.json");

    public static string GetProfileModsPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "Mods");

    public static string GetProfileLogsPath(string profileDataPath) =>
        Path.Combine(ResolveProfileDataPath(profileDataPath), "Logs");

    public static string ResolveProfileDataPath(string profileDataPath)
    {
        var fullPath = NormalizePathOrEmpty(profileDataPath);
        if (string.IsNullOrWhiteSpace(fullPath) || !Directory.Exists(fullPath))
        {
            return string.IsNullOrWhiteSpace(fullPath) ? profileDataPath : fullPath;
        }

        if (IsProfileDataDirectory(fullPath))
        {
            return fullPath;
        }

        var directoryName = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrWhiteSpace(directoryName))
        {
            var sameNameChild = Path.Combine(fullPath, directoryName);
            if (IsProfileDataDirectory(sameNameChild))
            {
                return sameNameChild;
            }
        }

        try
        {
            var candidates = Directory
                .EnumerateDirectories(fullPath, "*", SearchOption.TopDirectoryOnly)
                .Where(IsProfileDataDirectory)
                .Take(2)
                .ToList();
            if (candidates.Count == 1)
            {
                return candidates[0];
            }
        }
        catch
        {
            // Keep the registered path when probing is not possible.
        }

        return fullPath;
    }

    public static string SanitizeFileName(string name)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join('_', name.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized;
    }

    public static string? TryExtractVersionFromPackageName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var match = VersionPattern().Match(fileName);
        return match.Success ? match.Groups["version"].Value.Trim() : null;
    }

    private static bool IsProfileDataDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return false;
        }

        if (File.Exists(Path.Combine(path, "serverconfig.json")))
        {
            return true;
        }

        var knownDirectoryCount = 0;
        if (Directory.Exists(Path.Combine(path, "Logs"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Saves"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Mods"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "ModConfig"))) knownDirectoryCount++;
        if (Directory.Exists(Path.Combine(path, "Playerdata"))) knownDirectoryCount++;

        return knownDirectoryCount >= 2;
    }

    private static string NormalizePathOrEmpty(string? path)
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

    [GeneratedRegex(@"^vs_server_win-x64_(?<version>.+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
}
