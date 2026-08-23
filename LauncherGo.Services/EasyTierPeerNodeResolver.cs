using System.Buffers;
using System.Text;
using System.Text.Json;

namespace LauncherGo.Services;

/// <summary>
///     Resolves direct EasyTier peers and MVL-compatible shared-node subscriptions.
/// </summary>
internal static class EasyTierPeerNodeResolver
{
    private const uint Base62Chunk = 916_132_832;
    private static readonly HttpClient HttpClient = CreateHttpClient();

    public static async Task<IReadOnlyList<string>> ResolveAsync(
        string? text,
        CancellationToken cancellationToken,
        Action<string>? warning = null)
    {
        var peers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entries = (text ?? string.Empty)
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static item => !string.IsNullOrWhiteSpace(item) && !item.StartsWith('#'));

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (LooksLikeSubscription(entry))
            {
                try
                {
                    var json = await HttpClient.GetStringAsync(entry, cancellationToken).ConfigureAwait(false);
                    foreach (var peer in DecodeSubscription(json))
                    {
                        if (seen.Add(peer))
                        {
                            peers.Add(peer);
                        }
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or FormatException)
                {
                    warning?.Invoke($"EasyTier 节点订阅加载失败：{entry} ({ex.Message})");
                }

                continue;
            }

            if (seen.Add(entry))
            {
                peers.Add(entry);
            }
        }

        return peers;
    }

    internal static IReadOnlyList<string> DecodeSubscription(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var nodeElements = new List<JsonElement>();
        if (root.ValueKind == JsonValueKind.Array)
        {
            nodeElements.AddRange(root.EnumerateArray());
        }
        else if (root.ValueKind == JsonValueKind.Object &&
                 TryGetPropertyIgnoreCase(root, "nodes", out var nodes) &&
                 nodes.ValueKind == JsonValueKind.Array)
        {
            nodeElements.AddRange(nodes.EnumerateArray());
        }

        var peers = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in nodeElements)
        {
            if (node.ValueKind != JsonValueKind.Object ||
                !TryGetString(node, "address", out var address) ||
                string.IsNullOrWhiteSpace(address))
            {
                continue;
            }

            var codec = TryGetString(node, "codec", out var codecText) ? codecText : string.Empty;
            var decoded = DecodeAddress(address.Trim(), codec);
            if (!string.IsNullOrWhiteSpace(decoded) && seen.Add(decoded))
            {
                peers.Add(decoded);
            }
        }

        return peers;
    }

    internal static string DecodeAddress(string address, string? codec) =>
        codec?.Trim().ToLowerInvariant() switch
        {
            "base62" => Encoding.UTF8.GetString(DecodeBase62(address)),
            "base64" => Encoding.UTF8.GetString(Convert.FromBase64String(address)),
            "" or "none" => address,
            _ => throw new FormatException($"不支持的节点编码：{codec}")
        };

    internal static byte[] DecodeBase62(ReadOnlySpan<char> token)
    {
        if (token.IsEmpty)
        {
            return [];
        }

        var limbCount = token.Length / 5 + 1;
        var limbs = ArrayPool<uint>.Shared.Rent(limbCount);
        try
        {
            Array.Clear(limbs, 0, limbCount);
            var used = 1;
            var index = token.Length;
            var head = index % 5;
            if (head == 0)
            {
                head = 5;
            }

            while (index > 0)
            {
                var start = index - (index == token.Length ? head : 5);
                uint chunk = 0;
                for (var i = index - 1; i >= start; i--)
                {
                    var digit = CharToDigit(token[i]);
                    if (digit < 0)
                    {
                        throw new FormatException("发现非法 Base62 节点地址。");
                    }

                    chunk = checked(chunk * 62 + (uint)digit);
                }

                ulong carry = chunk;
                for (var i = 0; i < used; i++)
                {
                    var value = (ulong)limbs[i] * Base62Chunk + carry;
                    limbs[i] = (uint)value;
                    carry = value >> 32;
                }

                if (carry != 0)
                {
                    limbs[used++] = (uint)carry;
                }

                index = start;
            }

            var top = used - 1;
            while (top > 0 && limbs[top] == 0)
            {
                top--;
            }

            var high = limbs[top];
            var byteCount = top << 2;
            while (high != 0)
            {
                high >>= 8;
                byteCount++;
            }

            if (byteCount < 2)
            {
                throw new FormatException("Base62 节点地址数据不完整。");
            }

            var originalLength = (ushort)(limbs[0] & 0xFFFF);
            var result = new byte[originalLength];
            var copyLength = Math.Min(byteCount - 2, originalLength);
            for (var i = 0; i < copyLength; i++)
            {
                result[i] = (byte)(limbs[(i + 2) >> 2] >> (((i + 2) & 3) << 3));
            }

            return result;
        }
        finally
        {
            ArrayPool<uint>.Shared.Return(limbs);
        }
    }

    private static bool LooksLikeSubscription(string entry)
    {
        if (!Uri.TryCreate(entry, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        var path = uri.AbsolutePath;
        return path.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("shared-nodes", StringComparison.OrdinalIgnoreCase) ||
               path.Contains("subscription", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetString(JsonElement objectElement, string propertyName, out string value)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                value = property.Value.GetString() ?? string.Empty;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement objectElement,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in objectElement.EnumerateObject())
        {
            if (property.Name.Equals(propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static int CharToDigit(char value) => value switch
    {
        >= '0' and <= '9' => value - '0',
        >= 'A' and <= 'Z' => value - 'A' + 10,
        >= 'a' and <= 'z' => value - 'a' + 36,
        _ => -1
    };

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("LauncherGo/1.0");
        return client;
    }
}
