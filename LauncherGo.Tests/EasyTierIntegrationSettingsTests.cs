using System.Text.Json;
using LauncherGo.Domains.Models;
using Xunit;

namespace LauncherGo.Tests;

public sealed class EasyTierIntegrationSettingsTests
{
    [Fact]
    public void MissingGamePort_UsesDefaultPort()
    {
        var settings = JsonSerializer.Deserialize<EasyTierIntegrationSettings>("{}");

        Assert.NotNull(settings);
        Assert.Equal(EasyTierIntegrationSettings.DefaultGamePort, settings.GamePort);
    }

    [Fact]
    public void CustomGamePort_RoundTripsThroughJson()
    {
        var settings = new EasyTierIntegrationSettings { GamePort = 25565 };
        var json = JsonSerializer.Serialize(settings);
        var loaded = JsonSerializer.Deserialize<EasyTierIntegrationSettings>(json);

        Assert.NotNull(loaded);
        Assert.Equal(25565, loaded.GamePort);
    }
}
