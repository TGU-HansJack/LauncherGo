namespace LauncherGo.Domains.Models;

/// <summary>
///     自动备份周期类型。
/// </summary>
public enum BackupScheduleType
{
    Monthly,
    Weekly,
    Daily,
    Hourly,
    EveryNDays,
    EveryNHours,
    EveryNMinutes
}
