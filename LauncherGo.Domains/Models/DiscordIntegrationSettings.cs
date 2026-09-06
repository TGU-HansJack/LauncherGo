using System.Text.RegularExpressions;

namespace LauncherGo.Domains.Models;

/// <summary>
///     Discord 机器人集成配置。此配置与 OneBot/QQ 机器人配置完全独立。
/// </summary>
public sealed class DiscordIntegrationSettings
{
    public const int DefaultReconnectIntervalSec = 5;

    /// <summary>
    ///     Discord Bot Token。不得显示在日志或普通运行状态中。
    /// </summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>
    ///     Gateway 断线后的重连间隔（秒）。
    /// </summary>
    public int ReconnectIntervalSec { get; set; } = DefaultReconnectIntervalSec;

    /// <summary>
    ///     具有管理权限的 Discord 用户 Snowflake ID。
    /// </summary>
    public List<string> AdminUserIds { get; set; } = [];

    /// <summary>
    ///     具有管理权限的 Discord 角色 Snowflake ID。
    /// </summary>
    public List<string> AdminRoleIds { get; set; } = [];

    /// <summary>
    ///     Profile 到 Discord Guild 频道的绑定。
    /// </summary>
    public List<DiscordProfileBinding> ProfileBindings { get; set; } = [];

    /// <summary>
    ///     Discord 专用自定义命令。命令内容模型与 OneBot 复用，但配置不共享。
    /// </summary>
    public List<RobotCustomCommand> CustomCommands { get; set; } = [];
}

/// <summary>
///     一个服务器 Profile 与 Discord Guild 频道的绑定。
/// </summary>
public sealed class DiscordProfileBinding
{
    public string ProfileId { get; set; } = string.Empty;

    public string GuildId { get; set; } = string.Empty;

    public string ChannelId { get; set; } = string.Empty;
}

/// <summary>
///     Discord 配置的验证与规范化规则。
/// </summary>
public static class DiscordIntegrationSettingsRules
{
    private static readonly Regex BotTokenRegex = new(
        @"^[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{5,}\.[A-Za-z0-9_-]{20,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SlashCommandNameRegex = new(
        @"^[a-z0-9_-]{1,32}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DiscordIntegrationSettings Normalize(DiscordIntegrationSettings? source)
    {
        source ??= new DiscordIntegrationSettings();

        return new DiscordIntegrationSettings
        {
            BotToken = source.BotToken?.Trim() ?? string.Empty,
            ReconnectIntervalSec = Math.Clamp(
                source.ReconnectIntervalSec <= 0
                    ? DiscordIntegrationSettings.DefaultReconnectIntervalSec
                    : source.ReconnectIntervalSec,
                1,
                120),
            AdminUserIds = NormalizeSnowflakeIds(source.AdminUserIds),
            AdminRoleIds = NormalizeSnowflakeIds(source.AdminRoleIds),
            ProfileBindings = NormalizeProfileBindings(source.ProfileBindings),
            CustomCommands = RobotCustomCommandRules.NormalizeMany(source.CustomCommands)
        };
    }

    public static bool TryNormalizeSnowflakeId(string? value, out string normalized)
    {
        normalized = string.Empty;
        if (!ulong.TryParse(value?.Trim(), out var id) || id == 0)
        {
            return false;
        }

        normalized = id.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return true;
    }

    /// <summary>
    ///     Checks the shape of a Discord bot token without logging or decoding it.
    ///     An empty token means the integration is disabled and is therefore allowed by this helper.
    /// </summary>
    public static bool IsValidBotToken(string? value)
    {
        var token = value?.Trim() ?? string.Empty;
        return token.Length == 0 || BotTokenRegex.IsMatch(token);
    }

    public static List<string> NormalizeSnowflakeIds(IEnumerable<string>? values)
    {
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in values ?? [])
        {
            if (TryNormalizeSnowflakeId(value, out var normalized) && seen.Add(normalized))
            {
                result.Add(normalized);
            }
        }

        return result;
    }

    public static List<DiscordProfileBinding> NormalizeProfileBindings(IEnumerable<DiscordProfileBinding>? bindings)
    {
        var result = new List<DiscordProfileBinding>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var binding in bindings ?? [])
        {
            var profileId = binding?.ProfileId?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(profileId) ||
                !TryNormalizeSnowflakeId(binding!.GuildId, out var guildId) ||
                !TryNormalizeSnowflakeId(binding.ChannelId, out var channelId))
            {
                continue;
            }

            var key = $"{profileId}|{guildId}|{channelId}";
            if (!seen.Add(key))
            {
                continue;
            }

            result.Add(new DiscordProfileBinding
            {
                ProfileId = profileId,
                GuildId = guildId,
                ChannelId = channelId
            });
        }

        return result;
    }

    /// <summary>
    ///     Determines whether a normalized custom command can be registered as a native Discord slash command.
    ///     Commands returning false remain usable through the shared <c>/custom</c> entry point.
    /// </summary>
    public static bool IsNativeSlashCommandName(string? command)
    {
        var candidate = command?.Trim() ?? string.Empty;
        if (candidate.StartsWith("/", StringComparison.Ordinal))
        {
            candidate = candidate[1..];
        }

        return SlashCommandNameRegex.IsMatch(candidate);
    }

    public static bool IsAdministrator(DiscordIntegrationSettings settings, string? userId, IEnumerable<string>? roleIds)
    {
        var normalizedUser = userId?.Trim() ?? string.Empty;
        if (settings.AdminUserIds.Contains(normalizedUser, StringComparer.Ordinal)) return true;
        var configuredRoles = settings.AdminRoleIds.ToHashSet(StringComparer.Ordinal);
        return (roleIds ?? []).Any(role => configuredRoles.Contains(role?.Trim() ?? string.Empty));
    }

    public static DiscordProfileBinding? FindBinding(DiscordIntegrationSettings settings, string? guildId, string? channelId)
    {
        var guild = guildId?.Trim() ?? string.Empty;
        var channel = channelId?.Trim() ?? string.Empty;
        return settings.ProfileBindings.FirstOrDefault(binding => binding.GuildId == guild && binding.ChannelId == channel);
    }
}
