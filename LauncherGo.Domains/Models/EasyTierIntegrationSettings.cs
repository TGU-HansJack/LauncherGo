namespace LauncherGo.Domains.Models;

/// <summary>
///     EasyTier 集成配置。
/// </summary>
public sealed class EasyTierIntegrationSettings
{
    public const int DefaultGamePort = 42420;
    public const string DefaultRoomPrefix = "MVL";
    public const string DefaultIpv4Address = "10.144.144.1";
    public const string DefaultPeerNodesText =
        "tcp://public.easytier.top:11010\n" +
        "tcp://public2.easytier.cn:54321\n" +
        "https://etnode.zkitefly.eu.org/node1\n" +
        "https://etnode.zkitefly.eu.org/node2";

    /// <summary>
    ///     MVL 分享码使用的房间前缀。
    /// </summary>
    public string RoomPrefix { get; set; } = DefaultRoomPrefix;

    /// <summary>
    ///     EasyTier 引导/中继节点，每行一个地址。
    /// </summary>
    public string PeerNodesText { get; set; } = DefaultPeerNodesText;

    /// <summary>
    ///     自定义网络名称。留空时由 LauncherGo 生成 MVL 兼容网络。
    /// </summary>
    public string NetworkName { get; set; } = string.Empty;

    /// <summary>
    ///     自定义网络密钥。必须与网络名称同时填写。
    /// </summary>
    public string NetworkSecret { get; set; } = string.Empty;

    /// <summary>
    ///     ET 暴露的 Vintage Story 游戏端口。
    /// </summary>
    public int GamePort { get; set; } = DefaultGamePort;

    /// <summary>
    ///     是否允许通过 ET 转发游戏端口的 UDP 流量。
    /// </summary>
    public bool EnableUdp { get; set; } = true;

    /// <summary>
    ///     是否优先使用低延迟路径。
    /// </summary>
    public bool LatencyFirst { get; set; } = true;

    /// <summary>
    ///     是否启用 zstd 压缩。
    /// </summary>
    public bool Compression { get; set; } = true;

    /// <summary>
    ///     是否启用 KCP 代理。
    /// </summary>
    public bool EnableKcpProxy { get; set; } = true;

    /// <summary>
    ///     ET 节点主机名。留空时使用默认值。
    /// </summary>
    public string Hostname { get; set; } = "LauncherGo-vs-server";

    /// <summary>
    ///     ET 无 TUN 模式使用的虚拟 IPv4 地址。
    /// </summary>
    public string Ipv4Address { get; set; } = DefaultIpv4Address;
}
