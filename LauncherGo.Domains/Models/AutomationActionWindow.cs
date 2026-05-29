namespace LauncherGo.Domains.Models;

/// <summary>
///     自动化开关服计划窗口
/// </summary>
public class AutomationActionWindow
{
    public static AutomationScheduleMode[] ScheduleModes { get; } =
        Enum.GetValues<AutomationScheduleMode>();

    public static AutomationActionType[] ActionTypes { get; } =
        Enum.GetValues<AutomationActionType>();

    /// <summary>
    ///     每周/日期范围
    /// </summary>
    public AutomationScheduleMode ScheduleMode { get; set; } = AutomationScheduleMode.Weekly;

    /// <summary>
    ///     每周模式：开始星期（1=Mon ... 7=Sun）
    /// </summary>
    public int StartDayOfWeek { get; set; } = 1;

    /// <summary>
    ///     每周模式：结束星期（1=Mon ... 7=Sun）
    /// </summary>
    public int EndDayOfWeek { get; set; } = 5;

    /// <summary>
    ///     日期范围模式：开始日期 yyyy-MM-dd
    /// </summary>
    public string StartDate { get; set; } = string.Empty;

    /// <summary>
    ///     日期范围模式：结束日期 yyyy-MM-dd
    /// </summary>
    public string EndDate { get; set; } = string.Empty;

    /// <summary>
    ///     时间段开始 HH:mm
    /// </summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>
    ///     时间段结束 HH:mm
    /// </summary>
    public string EndTime { get; set; } = "23:00";

    /// <summary>
    ///     操作：开机/关机
    /// </summary>
    public AutomationActionType Action { get; set; } = AutomationActionType.Start;

    public bool Enabled { get; set; } = true;
}

