using Vintagestory.API.Common;
using Vintagestory.API.Server;

namespace LauncherGoRestriction.Server;

public sealed class LauncherGoRestrictionServerSystem : ModSystem
{
    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide != EnumAppSide.Client;
    }

    public override double ExecuteOrder()
    {
        return 0.01;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        var settings = api.LoadModConfig<RestrictionSettings>("launchergorestriction.json")
                       ?? new RestrictionSettings();
        var whitelist = Normalize(settings.WhitelistModIds);
        var blacklist = Normalize(settings.BlacklistModIds);

        ApplyPolicyProperty(
            api.Server.Config,
            "ModIdBlackList",
            settings.BlacklistEnabled ? blacklist : null);
        ApplyPolicyProperty(
            api.Server.Config,
            "ModIdWhiteList",
            settings.ForceWhitelistEnabled
                ? EnsureRestrictionModAllowed(whitelist)
                : null);

        api.Logger.Notification(
            "{0} Active. blacklist={1} ({2}), forceWhitelist={3} ({4}).",
            LauncherGoRestrictionModSystem.LogPrefix,
            settings.BlacklistEnabled,
            blacklist.Length,
            settings.ForceWhitelistEnabled,
            whitelist.Length);
    }

    private static void ApplyPolicyProperty(IServerConfig config, string propertyName, string[]? value)
    {
        var property = config.GetType().GetProperty(propertyName);
        if (property?.CanWrite == true && property.PropertyType == typeof(string[]))
        {
            property.SetValue(config, value);
        }
    }

    private static string[] Normalize(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(static value => value?.Trim().ToLowerInvariant() ?? string.Empty)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static string[] EnsureRestrictionModAllowed(IEnumerable<string> whitelist)
    {
        return whitelist
            .Append("launchergorestriction")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private sealed class RestrictionSettings
    {
        public bool BlacklistEnabled { get; set; }

        public bool ForceWhitelistEnabled { get; set; } = true;

        public List<string> WhitelistModIds { get; set; } = [];

        public List<string> BlacklistModIds { get; set; } = [];
    }
}
