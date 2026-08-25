using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ModUpdateServiceTests
{
    [Theory]
    [InlineData("1.0.0", "1.1.0", true)]
    [InlineData("v1.2.0", "1.2.0", false)]
    [InlineData("1.2.0-beta.2", "1.2.0-beta.10", true)]
    [InlineData("1.2.0", "1.2.0-beta.1", false)]
    [InlineData("unknown", "unknown", false)]
    public void CompareVersionsDetectsSemanticUpdates(string current, string latest, bool expectedUpdate)
    {
        var comparison = ModUpdateService.CompareVersions(current, latest);

        Assert.Equal(expectedUpdate, comparison < 0);
    }
}
