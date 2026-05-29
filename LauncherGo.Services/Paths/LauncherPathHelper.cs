using System.Globalization;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services.Paths;

public static class LauncherPathHelper
{
    public const string DefaultServerDownloadCatalogUrl = "https://api.vintagestory.at/stable-unstable.json";

    public static string AppRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "LauncherGo");

    public static string PreferencesFilePath => Path.Combine(AppRoot, "launcher-preferences.json");

    public static string LogDirectory => Path.Combine(AppRoot, "logs");

    public static string DefaultWorkspaceRoot => AppRoot;

    public static string DefaultServerDirectory => GetServerDirectory(DefaultWorkspaceRoot);

    public static string DefaultProfileDirectory => GetProfileDirectory(DefaultWorkspaceRoot);

    public static string DefaultSaveDirectory => GetSaveDirectory(DefaultWorkspaceRoot);

    public static string DefaultQqBotDirectory => GetQqBotDirectory(DefaultWorkspaceRoot);

    public static string GetWorkspaceRootOrDefault(string? workspaceRoot) =>
        NormalizeDirectoryOrDefault(workspaceRoot, DefaultWorkspaceRoot);

    public static string GetServerDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "servers");

    public static string GetProfileDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "profiles");

    public static string GetSaveDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "saves");

    public static string GetQqBotDirectory(string workspaceRoot) =>
        Path.Combine(GetWorkspaceRootOrDefault(workspaceRoot), "qqbot");

    public static LauncherPreferences BuildDefaults()
    {
        return new LauncherPreferences
        {
            Language = CultureInfo.CurrentUICulture.Name,
            WorkspaceRoot = DefaultWorkspaceRoot,
            ServerDirectory = DefaultServerDirectory,
            ProfileDirectory = DefaultProfileDirectory,
            SaveDirectory = DefaultSaveDirectory,
            QqBotDirectory = DefaultQqBotDirectory,
            ServerDownloadCatalogUrl = DefaultServerDownloadCatalogUrl,
            EnableChunkedDownloads = false,
            DownloadChunkCount = 4,
            DefaultLaunchProfileId = string.Empty,
            DefaultLaunchSaveFile = string.Empty,
            OpenServerQuery = new OpenServerQuerySettings(),
            Robot = new RobotIntegrationSettings
            {
                DatabasePath = Path.Combine(DefaultQqBotDirectory, "vs2qq.db")
            },
            Frp = new FrpIntegrationSettings()
        };
    }

    public static void EnsureBaseDirectories(LauncherPreferences preferences)
    {
        Directory.CreateDirectory(AppRoot);
        Directory.CreateDirectory(preferences.ServerDirectory);
        Directory.CreateDirectory(preferences.ProfileDirectory);
        Directory.CreateDirectory(preferences.SaveDirectory);
        Directory.CreateDirectory(preferences.QqBotDirectory);
    }

    public static string NormalizeDirectoryOrDefault(string? path, string defaultPath)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                return Path.GetFullPath(path.Trim());
            }
            catch
            {
                // ignore and fallback to default
            }
        }

        return Path.GetFullPath(defaultPath);
    }
}
