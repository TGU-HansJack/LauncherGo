using System.Text.RegularExpressions;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services.Paths;

internal static partial class LauncherWorkspacePathHelper
{
    public static string InstalledServerRoot(LauncherPreferences preferences) =>
        Path.Combine(preferences.ServerDirectory, "installed");

    public static string TempRoot(LauncherPreferences preferences) =>
        Path.Combine(preferences.ServerDirectory, ".tmp");

    public static string ProfilesIndexPath(LauncherPreferences preferences) =>
        Path.Combine(preferences.ProfileDirectory, "profiles.json");

    public static string ProfileDataPath(LauncherPreferences preferences, string profileId) =>
        Path.Combine(preferences.ProfileDirectory, SanitizeFileName(profileId));

    public static string ProfileSaveDirectory(LauncherPreferences preferences, string profileId) =>
        Path.Combine(ProfileDataPath(preferences, profileId), "Saves");

    public static string ProfileDefaultSaveFile(LauncherPreferences preferences, string profileId) =>
        Path.Combine(ProfileSaveDirectory(preferences, profileId), "default.vcdbs");

    public static string ProfileConfigPath(InstanceProfile profile) =>
        Path.Combine(profile.DirectoryPath, "serverconfig.json");

    public static string ProfileLogsPath(InstanceProfile profile) =>
        Path.Combine(profile.DirectoryPath, "Logs");

    public static string ServerInstallPath(LauncherPreferences preferences, string version) =>
        Path.Combine(InstalledServerRoot(preferences), SanitizeFileName(version));

    public static string ResolveServerExecutablePath(string installPath)
    {
        var stratumExecutable = Path.Combine(installPath, "StratumServer.exe");
        return File.Exists(stratumExecutable)
            ? stratumExecutable
            : Path.Combine(installPath, "VintagestoryServer.exe");
    }

    public static void EnsureWorkspace(LauncherPreferences preferences)
    {
        LauncherPathHelper.EnsureBaseDirectories(preferences);
        Directory.CreateDirectory(InstalledServerRoot(preferences));
        Directory.CreateDirectory(TempRoot(preferences));
    }

    public static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var sanitized = string.Join('_', value.Split(invalidChars, StringSplitOptions.RemoveEmptyEntries));
        return string.IsNullOrWhiteSpace(sanitized) ? "unnamed" : sanitized.Trim();
    }

    public static string? TryExtractVersionFromPackageName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return null;
        }

        var normalized = fileName.Trim();
        var vanillaMatch = VanillaServerPackageVersionRegex().Match(normalized);
        if (vanillaMatch.Success)
        {
            return vanillaMatch.Groups["version"].Value.Trim();
        }

        var stratumMatch = StratumServerPackageVersionRegex().Match(normalized);
        return stratumMatch.Success ? stratumMatch.Groups["version"].Value.Trim() : null;
    }

    public static string? TryExtractStratumBaseVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            return null;
        }

        var match = StratumVersionRegex().Match(version.Trim());
        return match.Success ? match.Groups["baseVersion"].Value.Trim() : null;
    }

    public static bool IsSameOrChildPath(string? candidatePath, string? rootPath)
    {
        var candidate = NormalizePath(candidatePath);
        var root = NormalizePath(rootPath);
        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(root))
        {
            return false;
        }

        return candidate.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim())
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch
        {
            return string.Empty;
        }
    }

    [GeneratedRegex(@"^vs_server_win-x64_(?<version>.+)\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex VanillaServerPackageVersionRegex();

    [GeneratedRegex(@"^stratum-(?<version>.+)-win-x64\.zip$", RegexOptions.IgnoreCase)]
    private static partial Regex StratumServerPackageVersionRegex();

    [GeneratedRegex(@"^(?<baseVersion>.+)-stratum\..+$", RegexOptions.IgnoreCase)]
    private static partial Regex StratumVersionRegex();
}
