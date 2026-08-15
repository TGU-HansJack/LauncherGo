using System.Security.Cryptography;
using System.Text;

namespace LauncherGo.Services;

/// <summary>
///     LauncherGo 客户端中继和 GatewayHost 之间的一次性转移凭证协议。
/// </summary>
public static class GatewayTransferProtocol
{
    private static readonly byte[] Magic = "LGTR"u8.ToArray();
    private const byte Version = 1;
    private const int MaximumTicketLength = 2048;

    public static string CreateTicket(
        string signingSecret,
        string sourceServerId,
        string targetServerId,
        string playerUid,
        TimeSpan lifetime)
    {
        if (string.IsNullOrWhiteSpace(signingSecret))
        {
            throw new InvalidOperationException("Gateway redirect ticket secret is unavailable.");
        }

        var payload = string.Join(
            '|',
            Version.ToString(),
            Base64UrlEncode(Encoding.UTF8.GetBytes(sourceServerId.Trim())),
            Base64UrlEncode(Encoding.UTF8.GetBytes(targetServerId.Trim())),
            Base64UrlEncode(Encoding.UTF8.GetBytes(playerUid.Trim())),
            DateTimeOffset.UtcNow.Add(lifetime).ToUnixTimeSeconds().ToString(),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(16)));
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var signature = Sign(payloadBytes, signingSecret);
        return Base64UrlEncode(payloadBytes) + "." + Base64UrlEncode(signature);
    }

    public static bool TryValidateTicket(
        string? ticket,
        string signingSecret,
        out GatewayTransferTicket transfer)
    {
        transfer = default!;
        if (string.IsNullOrWhiteSpace(ticket) || string.IsNullOrWhiteSpace(signingSecret))
        {
            return false;
        }

        var parts = ticket.Split('.', 2, StringSplitOptions.None);
        if (parts.Length != 2 || !TryBase64UrlDecode(parts[0], out var payloadBytes) ||
            !TryBase64UrlDecode(parts[1], out var suppliedSignature))
        {
            return false;
        }

        var expectedSignature = Sign(payloadBytes, signingSecret);
        if (!CryptographicOperations.FixedTimeEquals(suppliedSignature, expectedSignature))
        {
            return false;
        }

        var fields = Encoding.UTF8.GetString(payloadBytes).Split('|');
        if (fields.Length != 6 || fields[0] != Version.ToString() ||
            !TryBase64UrlDecode(fields[1], out var sourceServerIdBytes) ||
            !TryBase64UrlDecode(fields[2], out var targetServerIdBytes) ||
            !TryBase64UrlDecode(fields[3], out var playerUidBytes) ||
            !long.TryParse(fields[4], out var expiresAtUnixSeconds) ||
            fields[5].Length != 32)
        {
            return false;
        }

        var sourceServerId = Encoding.UTF8.GetString(sourceServerIdBytes);
        var targetServerId = Encoding.UTF8.GetString(targetServerIdBytes);
        var playerUid = Encoding.UTF8.GetString(playerUidBytes);
        if (!fields[5].All(Uri.IsHexDigit) ||
            string.IsNullOrWhiteSpace(sourceServerId) ||
            string.IsNullOrWhiteSpace(targetServerId) ||
            string.IsNullOrWhiteSpace(playerUid))
        {
            return false;
        }

        var expiresAtUtc = DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds);
        if (expiresAtUtc <= DateTimeOffset.UtcNow)
        {
            return false;
        }

        transfer = new GatewayTransferTicket(
            fields[5],
            sourceServerId,
            targetServerId,
            playerUid,
            expiresAtUtc);
        return true;
    }

    public static async Task WritePreambleAsync(
        Stream stream,
        string ticket,
        CancellationToken cancellationToken = default)
    {
        var ticketBytes = Encoding.ASCII.GetBytes(ticket);
        if (ticketBytes.Length is 0 or > MaximumTicketLength)
        {
            throw new InvalidOperationException("Gateway redirect ticket has an invalid length.");
        }

        var header = new byte[Magic.Length + 3];
        Magic.CopyTo(header, 0);
        header[4] = Version;
        header[5] = (byte)(ticketBytes.Length >> 8);
        header[6] = (byte)ticketBytes.Length;
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(ticketBytes, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public static async Task<GatewayPreambleReadResult> ReadPreambleAsync(
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var firstByte = new byte[1];
        if (await ReadAtMostAsync(stream, firstByte, cancellationToken).ConfigureAwait(false) == 0)
        {
            return GatewayPreambleReadResult.Normal(ReadOnlyMemory<byte>.Empty);
        }

        if (firstByte[0] != Magic[0])
        {
            return GatewayPreambleReadResult.Normal(firstByte);
        }

        var remainingMagic = new byte[Magic.Length - 1];
        if (!await ReadExactlyAsync(stream, remainingMagic, cancellationToken).ConfigureAwait(false))
        {
            var initialBytes = new byte[Magic.Length];
            initialBytes[0] = firstByte[0];
            remainingMagic.CopyTo(initialBytes, 1);
            return GatewayPreambleReadResult.Normal(initialBytes);
        }

        if (!remainingMagic.AsSpan().SequenceEqual(Magic.AsSpan(1)))
        {
            var initialBytes = new byte[Magic.Length];
            initialBytes[0] = firstByte[0];
            remainingMagic.CopyTo(initialBytes, 1);
            return GatewayPreambleReadResult.Normal(initialBytes);
        }

        var header = new byte[3];
        if (!await ReadExactlyAsync(stream, header, cancellationToken).ConfigureAwait(false) || header[0] != Version)
        {
            return GatewayPreambleReadResult.Invalid();
        }

        var ticketLength = (header[1] << 8) | header[2];
        if (ticketLength is 0 or > MaximumTicketLength)
        {
            return GatewayPreambleReadResult.Invalid();
        }

        var ticketBytes = new byte[ticketLength];
        if (!await ReadExactlyAsync(stream, ticketBytes, cancellationToken).ConfigureAwait(false))
        {
            return GatewayPreambleReadResult.Invalid();
        }

        return GatewayPreambleReadResult.Transfer(Encoding.ASCII.GetString(ticketBytes));
    }

    private static byte[] Sign(byte[] payload, string signingSecret)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        return hmac.ComputeHash(payload);
    }

    private static async Task<int> ReadAtMostAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset;
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        return await ReadAtMostAsync(stream, buffer, cancellationToken).ConfigureAwait(false) == buffer.Length;
    }

    private static string Base64UrlEncode(byte[] bytes) => Convert.ToBase64String(bytes)
        .TrimEnd('=')
        .Replace('+', '-')
        .Replace('/', '_');

    private static bool TryBase64UrlDecode(string value, out byte[] bytes)
    {
        bytes = [];
        if (string.IsNullOrWhiteSpace(value)) return false;
        var base64 = value.Replace('-', '+').Replace('_', '/');
        base64 = base64.PadRight(base64.Length + (4 - base64.Length % 4) % 4, '=');
        try
        {
            bytes = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

}

public sealed record GatewayTransferTicket(
    string Nonce,
    string SourceServerId,
    string TargetServerId,
    string PlayerUid,
    DateTimeOffset ExpiresAtUtc);

public sealed record GatewayPreambleReadResult(bool HasTransferPreamble, bool IsValid, string Ticket, ReadOnlyMemory<byte> InitialBytes)
{
    public static GatewayPreambleReadResult Normal(ReadOnlyMemory<byte> initialBytes) => new(false, true, string.Empty, initialBytes);

    public static GatewayPreambleReadResult Transfer(string ticket) => new(true, true, ticket, ReadOnlyMemory<byte>.Empty);

    public static GatewayPreambleReadResult Invalid() => new(true, false, string.Empty, ReadOnlyMemory<byte>.Empty);
}
