namespace LauncherGo.Domains.Models;

public sealed class OpenServerQueryEndpointConfig
{
    public string ProfileId { get; set; } = string.Empty;

    public string ServerHost { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;

    public bool AllowInsecureHttp { get; set; }

    public bool IncludeServerInfo { get; set; } = true;

    public bool IncludePlayers { get; set; } = true;

    public bool IncludePlayerEvents { get; set; } = true;

    public bool IncludeChats { get; set; } = true;

    public bool IncludeNotifications { get; set; } = true;

    public bool IncludeMapData { get; set; } = true;
}
