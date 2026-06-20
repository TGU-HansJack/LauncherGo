namespace LauncherGo.Domains.Models;

public sealed class RobotIntegrationSettings
{
    public string OneBotWsUrl { get; set; } = "ws://127.0.0.1:3001/";

    public string AccessToken { get; set; } = string.Empty;

    public string BoundGroupIdsText { get; set; } = string.Empty;

    public int ReconnectIntervalSec { get; set; } = 5;

    public string DatabasePath { get; set; } = string.Empty;

    public double PollIntervalSec { get; set; } = 1.0;

    public string DefaultEncoding { get; set; } = "utf-8";

    public string FallbackEncoding { get; set; } = "gbk";

    public string SuperUsersText { get; set; } = string.Empty;

    public List<RobotProfileBinding> ProfileBindings { get; set; } = [];

    public int OsqPollIntervalSec { get; set; } = 20;

    public int OsqRequestTimeoutSec { get; set; } = 8;
}

public sealed class RobotProfileBinding
{
    public string ProfileId { get; set; } = string.Empty;

    public string GroupId { get; set; } = string.Empty;

    public string SuperUserId { get; set; } = string.Empty;
}
