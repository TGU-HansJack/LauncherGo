using System.Globalization;
using LauncherGo.Domains.Enums;

namespace LauncherGo.Domains.Models;

public class LauncherPreferences
{
    public bool IsOnboardingCompleted { get; set; }

    public string Language { get; set; } = CultureInfo.CurrentUICulture.Name;

    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    public string ServerDirectory { get; set; } = string.Empty;

    public string ProfileDirectory { get; set; } = string.Empty;

    public string SaveDirectory { get; set; } = string.Empty;

    public string QqBotDirectory { get; set; } = string.Empty;
}
