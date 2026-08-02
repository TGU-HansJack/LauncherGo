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

    public ServerAuthOAuth2Settings OAuth2 { get; init; } = new();
}

public sealed class ServerAuthDiscourseSettings
{
    public bool Enabled { get; init; }

    public string BaseUrl { get; init; } = string.Empty;

    public string SharedSecret { get; init; } = string.Empty;

    public string PublicCallbackBaseUrl { get; init; } = "http://127.0.0.1:18092/";

    public string ListenPrefix { get; init; } = "http://127.0.0.1:18092/";
}

/// <summary>
///     通用 OAuth2/OIDC 授权码客户端配置。
/// </summary>
public sealed class ServerAuthOAuth2Settings
{
    public bool Enabled { get; init; }

    public string DiscoveryUrl { get; init; } = string.Empty;

    public string AuthorizationEndpoint { get; init; } = string.Empty;

    public string TokenEndpoint { get; init; } = string.Empty;

    public string UserInfoEndpoint { get; init; } = string.Empty;

    public string ClientId { get; init; } = string.Empty;

    public string ClientSecret { get; init; } = string.Empty;

    public string Scope { get; init; } = "openid profile email";

    public string PublicCallbackBaseUrl { get; init; } = "http://127.0.0.1:18092/";

    public string ListenPrefix { get; init; } = "http://127.0.0.1:18092/";

    public string UserIdClaim { get; init; } = "sub";

    public string UsernameClaim { get; init; } = "preferred_username";

    public string DisplayNameClaim { get; init; } = "name";

    public string EmailClaim { get; init; } = "email";
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

    public string OAuth2Subject { get; init; } = string.Empty;

    public string OAuth2Username { get; init; } = string.Empty;

    public string OAuth2DisplayName { get; init; } = string.Empty;

    public string OAuth2Email { get; init; } = string.Empty;
}

