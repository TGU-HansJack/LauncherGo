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

    /// <summary>
    ///     新版周期备份配置。
    /// </summary>
    public List<BackupSchedule> BackupSchedules { get; set; } = [];

    /// <summary>
    ///     受管自动备份保留份数，0 表示不限制。
    /// </summary>
    public int BackupRetentionCount { get; set; }

    /// <summary>
    ///     旧版每日时间列表，仅用于兼容旧配置。
    /// </summary>
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

    /// <summary>
    ///     启动服务端前清理当前实例的 Cache 目录内容。
    /// </summary>
    public bool ClearCacheBeforeStart { get; set; }

    /// <summary>
    ///     是否启用生命周期脚本。
    /// </summary>
    public bool AutomationScriptsEnabled { get; set; }

    public List<AutomationScript> AutomationScripts { get; set; } = [];
}

