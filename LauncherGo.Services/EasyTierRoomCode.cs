using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace LauncherGo.Services;

/// <summary>
///     与 MVL 房间分享码保持一致的编码器。
/// </summary>
public static class EasyTierRoomCode
{
    private const int UniqueIdLength = 4;
    private const int SecretLength = 4;
    private const int UniqueIdBase36Length = 7;
    private const int CombinedBase36Length = 11;
    private const int FirstPartLength = 9;

    public static EasyTierRoomSession Create(string prefix, ushort controlPort)
    {
        var uniqueId = RandomNumberGenerator.GetBytes(UniqueIdLength);
        var secret = RandomNumberGenerator.GetBytes(SecretLength);
        return Create(prefix, controlPort, uniqueId, secret);
    }

    public static EasyTierRoomSession Create(
        string prefix,
        ushort controlPort,
        ReadOnlySpan<byte> uniqueId,
        ReadOnlySpan<byte> secret)
    {
        var normalizedPrefix = NormalizePrefix(prefix);
        if (controlPort == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(controlPort));
        }

        if (uniqueId.Length != UniqueIdLength)
        {
            throw new ArgumentException($"Unique ID must be {UniqueIdLength} bytes.", nameof(uniqueId));
        }

        if (secret.Length != SecretLength)
        {
            throw new ArgumentException($"Secret must be {SecretLength} bytes.", nameof(secret));
        }

        Span<byte> combined = stackalloc byte[2 + SecretLength];
        combined[0] = (byte)(controlPort >> 8);
        combined[1] = (byte)controlPort;
        secret.CopyTo(combined[2..]);

        var uniqueIdValue = new BigInteger(uniqueId, isUnsigned: true, isBigEndian: true);
        var combinedValue = new BigInteger(combined, isUnsigned: true, isBigEndian: true);
        var uniqueIdBase36 = ToBase36(uniqueIdValue).PadLeft(UniqueIdBase36Length, '0');
        var combinedBase36 = ToBase36(combinedValue).PadLeft(CombinedBase36Length, '0');
        var fullData = string.Concat(uniqueIdBase36, combinedBase36);
        var prefixHex = Convert.ToHexStringLower(Encoding.UTF8.GetBytes(normalizedPrefix));
        var code = $"{prefixHex}-{fullData[..FirstPartLength]}-{fullData[FirstPartLength..]}".ToUpperInvariant();
        var uniqueIdHex = Convert.ToHexStringLower(uniqueId);

        return new EasyTierRoomSession(
            code,
            string.Format("{0}-vs-server-{1}", normalizedPrefix, uniqueIdHex),
            Convert.ToHexStringLower(secret),
            controlPort,
            normalizedPrefix,
            uniqueIdHex);
    }

    public static bool TryParse(string? code, out EasyTierRoomSession? session)
    {
        session = null;
        if (string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        var parts = code.Trim().Split('-');
        if (parts.Length != 3 ||
            !IsLowerHex(parts[0]) ||
            parts[1].Length != FirstPartLength ||
            parts[2].Length != UniqueIdBase36Length + CombinedBase36Length - FirstPartLength ||
            !IsBase36(parts[1]) ||
            !IsBase36(parts[2]))
        {
            return false;
        }

        try
        {
            var prefix = Encoding.UTF8.GetString(Convert.FromHexString(parts[0]));
            var normalizedPrefix = NormalizePrefix(prefix);
            var fullData = string.Concat(parts[1], parts[2]);
            if (fullData.Length != UniqueIdBase36Length + CombinedBase36Length)
            {
                return false;
            }

            var uniqueValue = FromBase36(fullData[..UniqueIdBase36Length]);
            var combinedValue = FromBase36(fullData[UniqueIdBase36Length..]);
            var uniqueId = PadUnsignedBytes(uniqueValue, UniqueIdLength);
            var combined = PadUnsignedBytes(combinedValue, 2 + SecretLength);
            var controlPort = (ushort)((combined[0] << 8) | combined[1]);
            if (controlPort == 0)
            {
                return false;
            }

            var uniqueIdHex = Convert.ToHexStringLower(uniqueId);
            session = new EasyTierRoomSession(
                code.Trim().ToUpperInvariant(),
                string.Format("{0}-vs-server-{1}", normalizedPrefix, uniqueIdHex),
                Convert.ToHexStringLower(combined.AsSpan(2)),
                controlPort,
                normalizedPrefix,
                uniqueIdHex);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    public static string NormalizePrefix(string? prefix)
    {
        var value = prefix?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value) || value.Contains('-'))
        {
            throw new ArgumentException("Room prefix must not be empty or contain '-'.", nameof(prefix));
        }

        return value;
    }

    private static byte[] PadUnsignedBytes(BigInteger value, int length)
    {
        var source = value.ToByteArray(isUnsigned: true, isBigEndian: true);
        if (source.Length > length)
        {
            throw new FormatException("Encoded room value is too large.");
        }

        var result = new byte[length];
        source.CopyTo(result.AsSpan(length - source.Length));
        return result;
    }

    private static string ToBase36(BigInteger value)
    {
        if (value.Sign < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value));
        }

        if (value.IsZero)
        {
            return "0";
        }

        const string digits = "0123456789abcdefghijklmnopqrstuvwxyz";
        var builder = new StringBuilder();
        while (value > BigInteger.Zero)
        {
            value = BigInteger.DivRem(value, 36, out var remainder);
            builder.Insert(0, digits[(int)remainder]);
        }

        return builder.ToString();
    }

    private static BigInteger FromBase36(string value)
    {
        var result = BigInteger.Zero;
        foreach (var character in value.ToLowerInvariant())
        {
            var digit = character switch
            {
                >= '0' and <= '9' => character - '0',
                >= 'a' and <= 'z' => character - 'a' + 10,
                _ => throw new FormatException("Invalid base36 character.")
            };
            result = result * 36 + digit;
        }

        return result;
    }

    private static bool IsBase36(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'z' or >= 'A' and <= 'Z');

    private static bool IsLowerHex(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value.Length % 2 == 0 &&
        value.All(static character =>
            character is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F');
}

public sealed record EasyTierRoomSession(
    string Code,
    string NetworkName,
    string NetworkSecret,
    ushort ControlPort,
    string Prefix,
    string UniqueIdHex);
