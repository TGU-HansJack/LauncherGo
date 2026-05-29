namespace LauncherGo.Domains.Models;

/// <summary>
///     定时播报消息
/// </summary>
public class ScheduledBroadcastMessage
{
    /// <summary>
    ///     播报时间，格式 HH:mm
    /// </summary>
    public string Time { get; set; } = "12:00";

    /// <summary>
    ///     播报文本
    /// </summary>
    public string Message { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}

