namespace LauncherGo.Domains.Models;

/// <summary>
///     VS2QQ 机器人配置
/// </summary>
public class RobotSettings
{
    public string OneBotWsUrl { get; init; } = "ws://127.0.0.1:3001/";

    public string? AccessToken { get; init; }

    public IReadOnlyList<long> BoundGroupIds { get; init; } = [];

    public int ReconnectIntervalSec { get; init; } = 5;

    public string DatabasePath { get; init; } = string.Empty;

    public double PollIntervalSec { get; init; } = 1.0;

    public string DefaultEncoding { get; init; } = "utf-8";

    public string FallbackEncoding { get; init; } = "gbk";

    public IReadOnlyList<long> SuperUsers { get; init; } = [];

    public int OsqPollIntervalSec { get; init; } = 20;

    public int OsqRequestTimeoutSec { get; init; } = 8;

    public bool OsqAllowInsecureHttp { get; init; }

    public string OsqListenPrefix { get; init; } = "http://127.0.0.1:18089/";

    public bool EnableOsqListener { get; init; }
}
