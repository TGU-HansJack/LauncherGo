namespace LauncherGo.Domains.Models;

public sealed class OpenServerQueryEndpointConfig
{
    public string ServerHost { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public bool Enabled { get; set; } = true;
}
