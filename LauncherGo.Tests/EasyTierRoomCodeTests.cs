using LauncherGo.Services;
using System.Text.Json;
using Xunit;

namespace LauncherGo.Tests;

public sealed class EasyTierRoomCodeTests
{
    [Fact]
    public void EasyTierPeerSnapshot_ParsesCurrentCliStringNodeId()
    {
        const string peersJson = """
            [
              {
                "cidr": "10.199.199.1/24",
                "ipv4": "10.199.199.1",
                "hostname": "launchergo-probe",
                "cost": "Local",
                "id": "3163653896",
                "version": "2.6.4-8428a89d"
              }
            ]
            """;

        var peers = JsonSerializer.Deserialize<List<EasyTierService.EasyTierPeerSnapshot>>(peersJson);

        var peer = Assert.Single(peers!);
        Assert.Equal(3163653896u, peer.Id);
        Assert.Equal("Local", peer.Cost);
    }

    [Fact]
    public void CreateAndTryParse_RoundTripsMvlCompatibleFields()
    {
        var session = EasyTierRoomCode.Create(
            "MVL",
            42420,
            [0x01, 0x02, 0x03, 0x04],
            [0xa1, 0xb2, 0xc3, 0xd4]);

        var parsedSuccessfully = EasyTierRoomCode.TryParse(session.Code.ToLowerInvariant(), out var result);

        Assert.Equal("MVL-vs-server-01020304", session.NetworkName);
        Assert.Equal("a1b2c3d4", session.NetworkSecret);
        Assert.Equal((ushort)42420, session.ControlPort);
        Assert.Equal("4D564C-00A2F4401-SKZBX5AZ8", session.Code);
        Assert.True(parsedSuccessfully);
        var parsed = Assert.IsType<EasyTierRoomSession>(result);
        Assert.Equal(session, parsed);
        Assert.Matches("^[0-9A-F]+-[0-9A-Z]{9}-[0-9A-Z]{9}$", session.Code);
    }

    [Fact]
    public void TryParse_RejectsCodeWithInvalidPartBoundaries()
    {
        Assert.False(EasyTierRoomCode.TryParse("4D564C-12345678-1234567890", out _));
    }
}
