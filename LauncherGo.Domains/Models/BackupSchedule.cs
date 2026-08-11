namespace LauncherGo.Domains.Models;

/// <summary>
///     自动备份周期。
/// </summary>
public sealed class BackupSchedule
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public bool Enabled { get; set; } = true;

    public BackupScheduleType Type { get; set; } = BackupScheduleType.Daily;

    /// <summary>
    ///     每月模式的日期（1-31，不存在的日期按当月最后一天执行）。
    /// </summary>
    public int DayOfMonth { get; set; } = 1;

    /// <summary>
    ///     每周模式的星期（1=周一，7=周日）。
    /// </summary>
    public int DayOfWeek { get; set; } = 1;

    /// <summary>
    ///     每日、每周、每月和间隔周期的执行时间，格式为 HH:mm。
    /// </summary>
    public string Time { get; set; } = "03:00";

    /// <summary>
    ///     每小时模式在第几分钟执行（0-59）。
    /// </summary>
    public int MinuteOfHour { get; set; }

    /// <summary>
    ///     每 N 天/小时/分钟模式的间隔数。
    /// </summary>
    public int Interval { get; set; } = 1;

    /// <summary>
    ///     间隔周期的本地日期锚点，格式为 yyyy-MM-dd。
    /// </summary>
    public string AnchorDate { get; set; } = string.Empty;
}
