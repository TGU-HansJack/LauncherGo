namespace LauncherGo.Domains.Models;

/// <summary>
///     Discord 机器人的当前运行状态。Token 不属于运行状态，避免意外暴露。
/// </summary>
public sealed class DiscordRuntimeStatus
{
    public bool IsRunning { get; set; }

    public bool IsConnected { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public string BotUserId { get; set; } = string.Empty;

    public string LastError { get; set; } = string.Empty;
}
