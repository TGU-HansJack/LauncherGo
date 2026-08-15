namespace LauncherGo.Domains.Models;

/// <summary>
///     TCP 网关的持久化配置。
/// </summary>
public sealed class TcpGatewaySettings
{
    public const string DefaultListenHost = "0.0.0.0";
    public const int DefaultListenPort = 42421;

    public string ListenHost { get; set; } = DefaultListenHost;

    public int ListenPort { get; set; } = DefaultListenPort;

    public int MaxConnections { get; set; } = 200;

    public int MaxConnectionsPerIp { get; set; } = 4;

    public int ConnectTimeoutSec { get; set; } = 8;

    public int HealthCheckIntervalSec { get; set; } = 5;

    /// <summary>
    ///     服务端重定向模组与网关共享的签名密钥。由 LauncherGo 自动生成，不在界面显示。
    /// </summary>
    public string RedirectTicketSecret { get; set; } = string.Empty;

    /// <summary>
    ///     每行一个 IP 地址或 CIDR 网段。留空表示不限制。
    /// </summary>
    public string AllowListText { get; set; } = string.Empty;

    /// <summary>
    ///     每行一个 IP 地址或 CIDR 网段。黑名单优先于白名单。
    /// </summary>
    public string BlockListText { get; set; } = string.Empty;

    public List<TcpGatewayBackend> Backends { get; set; } = [];
}
