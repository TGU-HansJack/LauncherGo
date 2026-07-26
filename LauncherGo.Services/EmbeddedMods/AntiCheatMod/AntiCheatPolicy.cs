using Vintagestory.API.Server;

namespace LauncherGoAntiCheat;

internal static class DetectorIds
{
    public const string MovementTeleport = "movement.teleport";
    public const string MovementSpeed = "movement.speed";
    public const string MovementVertical = "movement.vertical";
    public const string MovementFlight = "movement.flight";
    public const string MovementNoClip = "movement.noclip";
    public const string MovementNoFall = "movement.nofall";
    public const string BlockFastBreak = "block.fastbreak";
    public const string CombatRate = "combat.rate";
    public const string CombatReach = "combat.reach";
    public const string CombatMultiTarget = "combat.multitarget";
    public const string HealthMaximum = "health.maximum";
    public const string HealthHealRate = "health.healrate";
    public const string MiningOrePattern = "mining.orepattern";
    public const string MarketRate = "market.rate";

    public static string Automation(string category) => $"automation.{category}";
}

internal readonly record struct DetectorPolicy(
    bool Bypass,
    double SpeedMultiplier,
    double ActionRateMultiplier,
    IReadOnlyList<string> MatchedRuleIds)
{
    public static DetectorPolicy Default { get; } = new(false, 1, 1, []);
}

internal static class AntiCheatPolicy
{
    public static DetectorPolicy Resolve(
        AntiCheatConfig config,
        IServerPlayer player,
        string detector,
        IReadOnlySet<string> contexts,
        DateTimeOffset nowUtc)
    {
        var bypass = false;
        var speedMultiplier = 1d;
        var actionRateMultiplier = 1d;
        var matched = new List<string>();

        foreach (var rule in config.Whitelist)
        {
            if (!rule.Enabled ||
                !MatchesIdentity(rule, player) ||
                !MatchesDetector(rule.Detectors, detector) ||
                !MatchesContext(rule.Contexts, contexts) ||
                rule.ExpiresAtUtc is { } expiresAt && expiresAt <= nowUtc)
            {
                continue;
            }

            bypass |= rule.Bypass;
            speedMultiplier = Math.Max(speedMultiplier, rule.SpeedMultiplier);
            actionRateMultiplier = Math.Max(actionRateMultiplier, rule.ActionRateMultiplier);
            matched.Add(string.IsNullOrWhiteSpace(rule.Id) ? "unnamed" : rule.Id);
        }

        return matched.Count == 0
            ? DetectorPolicy.Default
            : new DetectorPolicy(bypass, speedMultiplier, actionRateMultiplier, matched);
    }

    private static bool MatchesIdentity(AntiCheatWhitelistRule rule, IServerPlayer player)
    {
        var hasSelector = !string.IsNullOrWhiteSpace(rule.PlayerUid) ||
                          !string.IsNullOrWhiteSpace(rule.PlayerName) ||
                          !string.IsNullOrWhiteSpace(rule.Role) ||
                          rule.Groups.Count > 0;
        if (!hasSelector)
            return false;

        if (!string.IsNullOrWhiteSpace(rule.PlayerUid) &&
            !MatchesValue(rule.PlayerUid, player.PlayerUID))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.PlayerName) &&
            !MatchesValue(rule.PlayerName, player.PlayerName))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.Role) &&
            !MatchesValue(rule.Role, player.Role?.Code ?? string.Empty))
        {
            return false;
        }

        if (rule.Groups.Count > 0)
        {
            var memberships = player.Groups ?? [];
            var matchesGroup = rule.Groups.Any(ruleGroup => memberships.Any(membership =>
                MatchesValue(ruleGroup, membership.GroupName ?? string.Empty) ||
                MatchesValue(ruleGroup, membership.GroupUid.ToString())));
            if (!matchesGroup)
                return false;
        }

        return true;
    }

    private static bool MatchesDetector(IEnumerable<string> patterns, string detector)
    {
        foreach (var rawPattern in patterns)
        {
            var pattern = rawPattern.Trim();
            if (pattern == "*")
                return true;
            if (pattern.EndsWith(".*", StringComparison.Ordinal))
            {
                var prefix = pattern[..^1];
                if (detector.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            else if (pattern.Equals(detector, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // An empty detector list never creates a blanket exemption.
        return false;
    }

    private static bool MatchesContext(
        IReadOnlyCollection<string> requiredContexts,
        IReadOnlySet<string> actualContexts)
    {
        return requiredContexts.Count == 0 ||
               requiredContexts.Any(context => context == "*" || actualContexts.Contains(context));
    }

    private static bool MatchesValue(string expected, string actual)
    {
        return expected == "*" || expected.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }
}
