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

    public string ServerDownloadCatalogUrl { get; set; } = string.Empty;

    public bool EnableChunkedDownloads { get; set; }

    public int DownloadChunkCount { get; set; } = 4;

    public string DefaultLaunchProfileId { get; set; } = string.Empty;

    public string DefaultLaunchSaveFile { get; set; } = string.Empty;

    public bool StartWithWindows { get; set; }

    public bool CloseToTrayOnExit { get; set; }

    public bool StartHiddenOnLaunch { get; set; }

    public bool AutoStartOpenServerQueryOnLaunch { get; set; }

    public bool AutoStartRobotOnLaunch { get; set; }

    public bool AutoStartFrpOnLaunch { get; set; }

    public bool AutoStartThirdPartyFrpcOnLaunch { get; set; }

    public OpenServerQuerySettings OpenServerQuery { get; set; } = new();

    public RobotIntegrationSettings Robot { get; set; } = new();

    public FrpIntegrationSettings Frp { get; set; } = new();
}
