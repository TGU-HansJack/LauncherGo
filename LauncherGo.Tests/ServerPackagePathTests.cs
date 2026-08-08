using LauncherGo.Services;
using LauncherGo.Services.Paths;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerPackagePathTests
{
    [Theory]
    [InlineData("vs_server_win-x64_1.22.6.zip", "1.22.6")]
    [InlineData("stratum-1.22.6-stratum.2-win-x64.zip", "1.22.6-stratum.2")]
    [InlineData("stratum-1.22.6-stratum.2-rc.1-win-x64.zip", "1.22.6-stratum.2-rc.1")]
    public void TryExtractVersionFromPackageName_RecognizesSupportedWindowsPackages(
        string fileName,
        string expectedVersion)
    {
        Assert.Equal(
            expectedVersion,
            LauncherWorkspacePathHelper.TryExtractVersionFromPackageName(fileName));
    }

    [Theory]
    [InlineData("1.22.6-stratum.2", "1.22.6")]
    [InlineData("1.22.6-stratum.2-rc.1", "1.22.6")]
    public void TryExtractStratumBaseVersion_ReturnsGameVersion(
        string version,
        string expectedBaseVersion)
    {
        Assert.Equal(
            expectedBaseVersion,
            LauncherWorkspacePathHelper.TryExtractStratumBaseVersion(version));
    }

    [Theory]
    [InlineData("10597029", "10.1 MB")]
    [InlineData("1024", "1.0 KB")]
    [InlineData("61.4 MB", "61.4 MB")]
    public void FormatCatalogFileSize_FormatsRawBytesAndPreservesDisplayText(
        string value,
        string expected)
    {
        Assert.Equal(expected, ServerPackageService.FormatCatalogFileSize(value));
    }
}
