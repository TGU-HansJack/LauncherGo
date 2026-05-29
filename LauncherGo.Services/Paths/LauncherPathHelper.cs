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

    public static string DefaultServerDirectory => Path.Combine(AppRoot, "servers");

    public static string DefaultProfileDirectory => Path.Combine(AppRoot, "profiles");

    public static string DefaultSaveDirectory => Path.Combine(AppRoot, "saves");

    public static string DefaultQqBotDirectory => Path.Combine(AppRoot, "qqbot");

    public static LauncherPreferences BuildDefaults()
    {
        return new LauncherPreferences
        {
            Language = CultureInfo.CurrentUICulture.Name,
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
