using System.Globalization;
using LauncherGo.Domains.Enums;

using System.Text.Json.Serialization;

namespace LauncherGo.Domains.Models;

public class LauncherPreferences
{
    public bool IsOnboardingCompleted { get; set; }

    public string Language { get; set; } = CultureInfo.CurrentUICulture.Name;

    public ThemeMode ThemeMode { get; set; } = ThemeMode.System;

    public string WorkspaceRoot { get; set; } = string.Empty;

    [JsonIgnore]
    public string ServerDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string ProfileDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string SaveDirectory { get; set; } = string.Empty;

    [JsonIgnore]
    public string QqBotDirectory { get; set; } = string.Empty;

    public string ServerDownloadCatalogUrl { get; set; } = string.Empty;

    public string StratumServerDownloadCatalogUrl { get; set; } = string.Empty;

    public bool EnableChunkedDownloads { get; set; }

    public int DownloadChunkCount { get; set; } = 4;

    public string DefaultLaunchProfileId { get; set; } = string.Empty;

    public string DefaultLaunchSaveFile { get; set; } = string.Empty;

    public List<string> DefaultLaunchProfileIds { get; set; } = [];

    public List<string> QuickCommands { get; set; } = [];

    public bool StartWithWindows { get; set; }

    public bool CloseToTrayOnExit { get; set; }

    public bool StartHiddenOnLaunch { get; set; }

    public bool AutoStartServerOnLaunch { get; set; }

    public bool AutoRestartServerAfterCrash { get; set; }

    public string AutoStartServerProfileId { get; set; } = string.Empty;

    public List<string> AutoStartServerProfileIds { get; set; } = [];

    public bool AutoStartOpenServerQueryOnLaunch { get; set; }

    public bool AutoStartRobotOnLaunch { get; set; }

    public bool AutoStartFrpOnLaunch { get; set; }

    public bool AutoStartThirdPartyFrpcOnLaunch { get; set; }

    public bool AutoStartEasyTierOnLaunch { get; set; }

    public OpenServerQuerySettings OpenServerQuery { get; set; } = new();

    public RobotIntegrationSettings Robot { get; set; } = new();

    public FrpIntegrationSettings Frp { get; set; } = new();

    public EasyTierIntegrationSettings EasyTier { get; set; } = new();

    public SaveCompressionSettings SaveCompression { get; set; } = new();
}
