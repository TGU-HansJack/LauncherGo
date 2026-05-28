using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class LauncherPreferencesService : ILauncherPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LauncherPreferences Load()
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        if (!File.Exists(LauncherPathHelper.PreferencesFilePath))
        {
            var defaults = Normalize(LauncherPathHelper.BuildDefaults());
            LauncherPathHelper.EnsureBaseDirectories(defaults);
            return defaults;
        }

        try
        {
            var rawJson = File.ReadAllText(LauncherPathHelper.PreferencesFilePath);
            var parsed = JsonSerializer.Deserialize<LauncherPreferences>(rawJson, JsonOptions) ?? LauncherPathHelper.BuildDefaults();
            var normalized = Normalize(parsed);
            LauncherPathHelper.EnsureBaseDirectories(normalized);
            return normalized;
        }
        catch
        {
            var fallback = Normalize(LauncherPathHelper.BuildDefaults());
            LauncherPathHelper.EnsureBaseDirectories(fallback);
            return fallback;
        }
    }

    public void Save(LauncherPreferences preferences)
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        var normalized = Normalize(preferences);
        LauncherPathHelper.EnsureBaseDirectories(normalized);

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(LauncherPathHelper.PreferencesFilePath, json);
    }

    private static LauncherPreferences Normalize(LauncherPreferences source)
    {
        var defaults = LauncherPathHelper.BuildDefaults();

        return new LauncherPreferences
        {
            IsOnboardingCompleted = source.IsOnboardingCompleted,
            Language = string.IsNullOrWhiteSpace(source.Language)
                ? CultureInfo.CurrentUICulture.Name
                : source.Language.Trim(),
            ThemeMode = Enum.IsDefined(source.ThemeMode) ? source.ThemeMode : ThemeMode.System,
            ServerDirectory = LauncherPathHelper.NormalizeDirectoryOrDefault(source.ServerDirectory, defaults.ServerDirectory),
            ProfileDirectory = LauncherPathHelper.NormalizeDirectoryOrDefault(source.ProfileDirectory, defaults.ProfileDirectory),
            SaveDirectory = LauncherPathHelper.NormalizeDirectoryOrDefault(source.SaveDirectory, defaults.SaveDirectory),
            QqBotDirectory = LauncherPathHelper.NormalizeDirectoryOrDefault(source.QqBotDirectory, defaults.QqBotDirectory),
            DefaultLaunchProfileId = string.IsNullOrWhiteSpace(source.DefaultLaunchProfileId)
                ? string.Empty
                : source.DefaultLaunchProfileId.Trim(),
            DefaultLaunchSaveFile = NormalizeFilePathOrEmpty(source.DefaultLaunchSaveFile)
        };
    }

    private static string NormalizeFilePathOrEmpty(string? path)
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
}
