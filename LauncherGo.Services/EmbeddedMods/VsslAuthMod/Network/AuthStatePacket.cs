using ProtoBuf;

namespace VsslAuth.Network;

[ProtoContract]
public sealed class AuthStatePacket
{
    [ProtoMember(1)] public bool IsAuthenticated { get; set; }
    [ProtoMember(2)] public string Message { get; set; } = string.Empty;
}
