using ProtoBuf;

namespace VsslAuth.Network;

[ProtoContract]
public sealed class AuthChallengePacket
{
    [ProtoMember(1)] public string ChallengeId { get; set; } = string.Empty;
    [ProtoMember(2)] public string AuthUrl { get; set; } = string.Empty;
    [ProtoMember(3)] public string Mode { get; set; } = string.Empty;
    [ProtoMember(4)] public string Message { get; set; } = string.Empty;
}
