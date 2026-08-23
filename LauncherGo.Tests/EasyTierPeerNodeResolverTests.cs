using System.Text;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class EasyTierPeerNodeResolverTests
{
    [Fact]
    public void DecodeSubscription_DecodesBase62AndKeepsPlainNodes()
    {
        const string plainAddress = "tcp://127.0.0.1:11010";
        var encodedAddress = EncodeBase62(Encoding.UTF8.GetBytes(plainAddress));
        var json = $$"""
            {
              "name": "test",
              "nodes": [
                { "address": "{{encodedAddress}}", "codec": "Base62" },
                { "address": "tcp://127.0.0.1:11010", "codec": "None" }
              ]
            }
            """;

        var nodes = EasyTierPeerNodeResolver.DecodeSubscription(json);

        var node = Assert.Single(nodes);
        Assert.Equal(plainAddress, node);
    }

    [Fact]
    public void DecodeSubscription_SupportsBase64AndArrayRoot()
    {
        const string plainAddress = "tcp://relay.example:11010";
        var encodedAddress = Convert.ToBase64String(Encoding.UTF8.GetBytes(plainAddress));

        var nodes = EasyTierPeerNodeResolver.DecodeSubscription(
            $"[{{ \"address\": \"{encodedAddress}\", \"codec\": \"Base64\" }}]");

        Assert.Equal([plainAddress], nodes);
    }

    [Fact]
    public void DecodeAddress_DecodesTheCurrentMvlBase62NodeFormat()
    {
        var decoded = EasyTierPeerNodeResolver.DecodeAddress(
            "ogr13eblKPEbX79Ztt2MxU3qHFmCKUYdd07sg2",
            "Base62");

        Assert.StartsWith("tcp://", decoded, StringComparison.Ordinal);
    }

    private static string EncodeBase62(byte[] data)
    {
        var bytes = new byte[data.Length + 2];
        bytes[0] = (byte)data.Length;
        bytes[1] = (byte)(data.Length >> 8);
        Buffer.BlockCopy(data, 0, bytes, 2, data.Length);

        var limbs = new List<uint>();
        for (var index = 0; index < bytes.Length; index += 4)
        {
            uint value = 0;
            for (var offset = 0; offset < 4 && index + offset < bytes.Length; offset++)
            {
                value |= (uint)bytes[index + offset] << (offset * 8);
            }

            limbs.Add(value);
        }

        var result = new StringBuilder();
        var top = limbs.Count - 1;
        while (true)
        {
            ulong carry = 0;
            for (var index = top; index >= 0; index--)
            {
                var current = (carry << 32) | limbs[index];
                limbs[index] = (uint)(current / 916_132_832);
                carry = current % 916_132_832;
            }

            for (var i = 0; i < 5; i++)
            {
                result.Append("0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz"[(int)(carry % 62)]);
                carry /= 62;
            }

            while (top > 0 && limbs[top] == 0)
            {
                top--;
            }

            if (top == 0 && limbs[0] == 0)
            {
                break;
            }
        }

        var end = result.Length;
        while (end > 1 && result[end - 1] == '0')
        {
            end--;
        }

        return result.ToString(0, end);
    }
}
