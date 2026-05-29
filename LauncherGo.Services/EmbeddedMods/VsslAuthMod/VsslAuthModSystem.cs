using Vintagestory.API.Common;
using VsslAuth.Network;

namespace VsslAuth;

public sealed class VsslAuthModSystem : ModSystem
{
    public const string ChannelName = "serverauth.auth";
    public const string LogPrefix = "[SERVER-AUTH]";

    public override void Start(ICoreAPI api)
    {
        api.Network.RegisterChannel(ChannelName)
            .RegisterMessageType<AuthChallengePacket>()
            .RegisterMessageType<AuthStatePacket>();
    }
}
