using LauncherGo.Domains.Features;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ExperimentalFeaturesTests
{
    [Fact]
    public void AntiCheatFlag_MatchesBuildSwitch()
    {
#if EXPERIMENTAL_ANTICHEAT
        Assert.True(ExperimentalFeatures.AntiCheatEnabled);
#else
        Assert.False(ExperimentalFeatures.AntiCheatEnabled);
#endif
    }
}
