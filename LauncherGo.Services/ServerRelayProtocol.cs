using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LauncherGo.Services;

internal static class ServerRelayProtocol
{
    public const string LauncherArgument = "--LauncherGo-server-relay";
    public const string RequestTypePing = "ping";
    public const string RequestTypeStatus = "status";
    public const string RequestTypeCommand = "command";

    public static readonly ServerRelayTimeouts DefaultTimeouts = new(
        RequestRead: TimeSpan.FromSeconds(1),
        CommandForward: TimeSpan.FromSeconds(3),
        ResponseWrite: TimeSpan.FromSeconds(1));

    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string CreatePipeName(string profileId)
    {
        var suffix = string.IsNullOrWhiteSpace(profileId)
            ? Guid.NewGuid().ToString("N")
            : HashText(profileId.Trim());

        return $"LauncherGo-server-relay-{suffix}";
    }

    private static string HashText(string text)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(bytes, 0, 12).ToLowerInvariant();
    }
}

internal readonly record struct ServerRelayTimeouts(
    TimeSpan RequestRead,
    TimeSpan CommandForward,
    TimeSpan ResponseWrite);

internal sealed class ServerRelayRequest
{
    public string Type { get; set; } = string.Empty;

    public string? Command { get; set; }
}

internal sealed class ServerRelayResponse
{
    public bool Success { get; set; }

    public string? Error { get; set; }

    public ServerRelayState? State { get; set; }
}

