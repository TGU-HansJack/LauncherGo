using Vintagestory.API.Client;
using Vintagestory.API.Common;

namespace LauncherGoRestriction.Client;

public sealed class LauncherGoRestrictionClientSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Client;
    }

    public override double ExecuteOrder()
    {
        return 0.01;
    }

    public override void StartClientSide(ICoreClientAPI api)
    {
        // Vintage Story applies the server's ModIdBlackList/ModIdWhiteList before this phase.
        api.Logger.Notification("{0} Client policy filter active.", LauncherGoRestrictionModSystem.LogPrefix);
    }
}
