namespace LauncherGo.Domains.Models;

/// <summary>
///     ServerAuth 配置
/// </summary>
public sealed class ServerAuthSettings
{
    public bool Enabled { get; init; }

    public int LoginTimeoutSeconds { get; init; } = 60;

    public int RememberSessionMinutes { get; init; } = 30;

    public ServerAuthDiscourseSettings Discourse { get; init; } = new();
}

public sealed class ServerAuthDiscourseSettings
{
    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string SharedSecret { get; init; } = string.Empty;

    public string PublicCallbackBaseUrl { get; init; } = "http://127.0.0.1:18092/";

    public string ListenPrefix { get; init; } = "http://127.0.0.1:18092/";
}

public sealed class ServerAuthPlayerSummary
{
    public string PlayerUid { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string NormalizedPlayerName { get; init; } = string.Empty;

    public string RegisteredIp { get; init; } = string.Empty;

    public DateTimeOffset RegisteredAtUtc { get; init; }

    public string LastIp { get; init; } = string.Empty;

    public DateTimeOffset? LastLoginAtUtc { get; init; }

    public bool PasswordResetRequired { get; init; }

    public bool HasPassword { get; init; }

    public string DiscourseExternalId { get; init; } = string.Empty;

    public string DiscourseUsername { get; init; } = string.Empty;

    public string DiscourseEmail { get; init; } = string.Empty;
}

