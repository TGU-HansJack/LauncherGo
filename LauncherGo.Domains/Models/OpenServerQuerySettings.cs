namespace LauncherGo.Domains.Models;

public sealed class OpenServerQuerySettings
{
    public bool Enabled { get; set; } = true;

    public string ListenPrefix { get; set; } = "http://127.0.0.1:18089/";

    public bool AllowInsecureHttp { get; set; }

    public int RequestTimeoutSec { get; set; } = 8;

    public bool IncludeServerInfo { get; set; } = true;

    public bool IncludePlayers { get; set; } = true;

    public bool IncludePlayerEvents { get; set; } = true;

    public bool IncludeChats { get; set; } = true;

    public bool IncludeNotifications { get; set; } = true;

    public bool IncludeMapData { get; set; } = true;

    public List<OpenServerQueryEndpointConfig> Endpoints { get; set; } = [];

    // Legacy single-endpoint fields kept for backward compatibility.
    public string EndpointHost { get; set; } = string.Empty;

    public string EndpointToken { get; set; } = string.Empty;
}
