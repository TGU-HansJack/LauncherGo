using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class LauncherUpdateServiceTests
{
    [Theory]
    [InlineData("v2.5.4", "2.5.4")]
    [InlineData("2.5.4-preview+abc", "2.5.4")]
    public void NormalizeVersion_RemovesTagAndMetadata(string input, string expected)
    {
        Assert.Equal(expected, LauncherUpdateService.NormalizeVersion(input));
    }

    [Fact]
    public void SelectAsset_UsesCurrentPackagePrefix()
    {
        var assets = new[]
        {
            new LauncherUpdateAsset { Name = "LauncherGo-Setup-2.0.0-win-x64.exe" },
            new LauncherUpdateAsset { Name = "LauncherGo-Small-Setup-2.0.0-win-x64.exe" },
            new LauncherUpdateAsset { Name = "LauncherGo-portable-2.0.0-win-x64.zip" },
            new LauncherUpdateAsset { Name = "LauncherGo-small-package-2.0.0-win-x64.zip" }
        };

        Assert.Equal("LauncherGo-Setup-2.0.0-win-x64.exe", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.Installer)?.Name);
        Assert.Equal("LauncherGo-Small-Setup-2.0.0-win-x64.exe", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.SmallInstaller)?.Name);
        Assert.Equal("LauncherGo-portable-2.0.0-win-x64.zip", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.Portable)?.Name);
        Assert.Equal("LauncherGo-small-package-2.0.0-win-x64.zip", LauncherUpdateService.SelectAsset(assets, LauncherPackageKind.SmallPackage)?.Name);
    }

    [Fact]
    public void BuildProxyUrl_PrependsSelectedProxy()
    {
        const string url = "https://api.github.com/repos/vscn-studio/LauncherGo/releases/latest";
        Assert.Equal(url, LauncherUpdateService.BuildProxyUrl(url, GitHubProxyKind.Direct));
        Assert.Equal("https://gh-proxy.com/" + url, LauncherUpdateService.BuildProxyUrl(url, GitHubProxyKind.GhProxy));
    }

    [Fact]
    public void CompareVersions_ComparesNumericSegments()
    {
        Assert.True(LauncherUpdateService.CompareVersions("2.10.0", "2.9.9") > 0);
        Assert.Equal(0, LauncherUpdateService.CompareVersions("2.5.4", "2.5.4"));
    }
}
