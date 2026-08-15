namespace LauncherGoRedirect;

[ProtoBuf.ProtoContract]
public sealed class GatewayRedirectExecutePacket
{
    [ProtoBuf.ProtoMember(1)]
    public string ServerId { get; set; } = string.Empty;

    [ProtoBuf.ProtoMember(2)]
    public string TransferTicket { get; set; } = string.Empty;

    [ProtoBuf.ProtoMember(3)]
    public string Name { get; set; } = string.Empty;
}
