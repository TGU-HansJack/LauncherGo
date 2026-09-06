namespace LauncherGo.Domains.Models;

/// <summary>
///     VS2QQ 机器人配置
/// </summary>
public class RobotSettings
{
    public string OneBotWsUrl { get; init; } = "ws://127.0.0.1:3001/";

    public string? AccessToken { get; init; }

    public IReadOnlyList<long> BoundGroupIds { get; init; } = [];

    public IReadOnlyList<RobotProfileBinding> ProfileBindings { get; init; } = [];

    public IReadOnlyList<RobotCustomCommand> CustomCommands { get; init; } = [];

    public IReadOnlyList<RobotTeleportPoint> TeleportPoints { get; init; } = [];

    public int ReconnectIntervalSec { get; init; } = 5;

    public string DatabasePath { get; init; } = string.Empty;

    public string DefaultEncoding { get; init; } = "utf-8";

    public string FallbackEncoding { get; init; } = "gbk";

    public IReadOnlyList<long> SuperUsers { get; init; } = [];

}
