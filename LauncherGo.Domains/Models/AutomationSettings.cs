namespace LauncherGo.Domains.Models;

/// <summary>
///     自动化设置
/// </summary>
public class AutomationSettings
{
    public string TargetProfileId { get; set; } = string.Empty;

    public bool RestartSchedulerEnabled { get; set; }

    public List<DailyTimeWindow> TimeWindows { get; set; } = [];

    public List<AutomationActionWindow> ActionWindows { get; set; } = [];

    public bool BackupEnabled { get; set; }

    public List<string> BackupTimes { get; set; } = [];

    public bool BackupBeforeShutdown { get; set; } = true;

    public bool BroadcastEnabled { get; set; }

    public List<ScheduledBroadcastMessage> BroadcastMessages { get; set; } = [];

    public bool CommandEnabled { get; set; }

    public List<ScheduledServerCommand> ScheduledCommands { get; set; } = [];

    public bool ExportLogEnabled { get; set; }

    public List<string> ExportTimes { get; set; } = [];

    public bool ExportBeforeShutdown { get; set; } = true;

    public bool ExportIncludeChat { get; set; } = true;

    public bool ExportIncludeServerInfo { get; set; } = true;
}

