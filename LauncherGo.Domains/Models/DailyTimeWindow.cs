namespace LauncherGo.Domains.Models;

/// <summary>
///     每日时间段
/// </summary>
public class DailyTimeWindow
{
    /// <summary>
    ///     开服时间，格式 HH:mm
    /// </summary>
    public string StartTime { get; set; } = "08:00";

    /// <summary>
    ///     关服时间，格式 HH:mm
    /// </summary>
    public string EndTime { get; set; } = "23:00";

    public bool Enabled { get; set; } = true;
}

