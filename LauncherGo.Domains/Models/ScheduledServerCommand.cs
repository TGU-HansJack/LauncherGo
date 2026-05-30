namespace LauncherGo.Domains.Models;

/// <summary>
///     定时服务器命令
/// </summary>
public class ScheduledServerCommand
{
    /// <summary>
    ///     执行时间，格式 HH:mm
    /// </summary>
    public string Time { get; set; } = "12:00";

    /// <summary>
    ///     服务器命令
    /// </summary>
    public string Command { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
