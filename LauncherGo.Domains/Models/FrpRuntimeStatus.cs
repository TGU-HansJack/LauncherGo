namespace LauncherGo.Domains.Models;

/// <summary>
///     FRP 运行状态
/// </summary>
public class FrpRuntimeStatus
{
    /// <summary>
    ///     是否正在运行
    /// </summary>
    public bool IsRunning { get; set; }

    /// <summary>
    ///     进程 Id
    /// </summary>
    public int? ProcessId { get; set; }

    /// <summary>
    ///     启动时间（UTC）
    /// </summary>
    public DateTimeOffset? StartedAtUtc { get; set; }
}

