namespace LauncherGo.Domains.Models;

/// <summary>
///     服务端基础配置
/// </summary>
public class ServerCommonSettings
{
    public string ServerName { get; set; } = "Vintage Story Server";

    public string? ServerDescription { get; set; }

    public string? ServerUrl { get; set; }

    public string? Ip { get; set; }

    public int Port { get; set; } = 42420;

    public int MaxClients { get; set; } = 16;

    public int MaxClientsInQueue { get; set; }

    public string? Password { get; set; }

    public bool AdvertiseServer { get; set; }

    public int WhitelistMode { get; set; }

    public bool Upnp { get; set; }

    public bool AllowPvP { get; set; } = true;

    public bool AllowFireSpread { get; set; } = true;

    public bool AllowFallingBlocks { get; set; } = true;

    public bool PassTimeWhenEmpty { get; set; }

    public int WarnClientsAfterAfkSeconds { get; set; }

    public int KickClientsAfterAfkSeconds { get; set; }

    public int ClientConnectionTimeout { get; set; } = 150;

    public int MaxChunkRadius { get; set; } = 12;

    public int DieBelowDiskSpaceMb { get; set; } = 400;

    public bool CorruptionProtection { get; set; } = true;

    public bool RegenerateCorruptChunks { get; set; }

    public string StartupCommands { get; set; } = string.Empty;

    public bool VerifyPlayerAuth { get; set; } = true;

    public string ServerLanguage { get; set; } = "en";

    public string DefaultRoleCode { get; set; } = "suplayer";

    public string WelcomeMessage { get; set; } = string.Empty;
}
