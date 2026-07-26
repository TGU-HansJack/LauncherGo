using System.Text.Json;

namespace LauncherGoAntiCheat;

internal sealed class AntiCheatConfig
{
    public bool Enabled { get; set; }

    public bool MonitorOnly { get; set; } = true;

    public AntiCheatActionConfig Actions { get; set; } = new();

    public AntiCheatDetectorConfig Detectors { get; set; } = new();

    public List<AntiCheatWhitelistRule> Whitelist { get; set; } = [];

    public static AntiCheatConfig Normalize(AntiCheatConfig? value)
    {
        value ??= new AntiCheatConfig();
        var actions = value.Actions ?? new AntiCheatActionConfig();
        var detectors = value.Detectors ?? new AntiCheatDetectorConfig();
        return new AntiCheatConfig
        {
            Enabled = value.Enabled,
            MonitorOnly = value.MonitorOnly,
            Actions = AntiCheatActionConfig.Normalize(actions),
            Detectors = AntiCheatDetectorConfig.Normalize(detectors),
            Whitelist = (value.Whitelist ?? [])
                .Where(static rule => rule is not null)
                .Select(AntiCheatWhitelistRule.Normalize)
                .Take(2048)
                .ToList()
        };
    }

    public AntiCheatConfig Clone()
    {
        var json = JsonSerializer.Serialize(this);
        return JsonSerializer.Deserialize<AntiCheatConfig>(json) ?? new AntiCheatConfig();
    }
}

internal sealed class AntiCheatActionConfig
{
    public int WarningScore { get; set; } = 3;

    public int KickScore { get; set; } = 8;

    public int BanScore { get; set; } = 16;

    public int ScoreDecaySeconds { get; set; } = 120;

    public int AlertCooldownSeconds { get; set; } = 30;

    public bool WarnAdministrators { get; set; } = true;

    public bool KickEnabled { get; set; }

    public bool BanEnabled { get; set; }

    public bool PunishStatisticalDetections { get; set; }

    public static AntiCheatActionConfig Normalize(AntiCheatActionConfig value)
    {
        var warningScore = Math.Clamp(value.WarningScore <= 0 ? 3 : value.WarningScore, 1, 1000);
        var kickScore = Math.Max(
            warningScore,
            Math.Clamp(value.KickScore <= 0 ? 8 : value.KickScore, 1, 2000));
        var banScore = Math.Max(
            kickScore,
            Math.Clamp(value.BanScore <= 0 ? 16 : value.BanScore, 1, 5000));
        return new AntiCheatActionConfig
        {
            WarningScore = warningScore,
            KickScore = kickScore,
            BanScore = banScore,
            ScoreDecaySeconds = Math.Clamp(value.ScoreDecaySeconds <= 0 ? 120 : value.ScoreDecaySeconds, 10, 86400),
            AlertCooldownSeconds = Math.Clamp(value.AlertCooldownSeconds <= 0 ? 30 : value.AlertCooldownSeconds, 1, 3600),
            WarnAdministrators = value.WarnAdministrators,
            KickEnabled = value.KickEnabled,
            BanEnabled = value.BanEnabled,
            PunishStatisticalDetections = value.PunishStatisticalDetections
        };
    }
}

internal sealed class AntiCheatDetectorConfig
{
    public bool MovementSpeedEnabled { get; set; } = true;

    public double MaxHorizontalSpeed { get; set; } = 12;

    public double MaxVerticalSpeed { get; set; } = 18;

    public double TeleportDistance { get; set; } = 24;

    public int HoverSeconds { get; set; } = 4;

    public bool FlightEnabled { get; set; } = true;

    public bool NoClipEnabled { get; set; } = true;

    public bool FastBreakEnabled { get; set; } = true;

    public double FastBreakMultiplier { get; set; } = 0.35;

    public int FastBreakWindowSeconds { get; set; } = 12;

    public int FastBreakMinimumSamples { get; set; } = 4;

    public bool AutomationEnabled { get; set; } = true;

    public int MaxActionsPerSecond { get; set; } = 12;

    public int AutomationWindowSeconds { get; set; } = 10;

    public int AutomationMinimumSamples { get; set; } = 12;

    public bool CombatEnabled { get; set; } = true;

    public int MaxAttacksPerSecond { get; set; } = 8;

    public double MaxAttackReach { get; set; } = 6;

    public bool HealthEnabled { get; set; } = true;

    public double MaxUnexpectedHeal { get; set; } = 25;

    public bool OrePatternEnabled { get; set; } = true;

    public int OrePatternWindowMinutes { get; set; } = 10;

    public int OrePatternMinimumSamples { get; set; } = 20;

    public double OrePatternRatio { get; set; } = 0.65;

    public bool MarketRateEnabled { get; set; } = true;

    public int MarketInteractionsPerMinute { get; set; } = 30;

    public static AntiCheatDetectorConfig Normalize(AntiCheatDetectorConfig value)
    {
        return new AntiCheatDetectorConfig
        {
            MovementSpeedEnabled = value.MovementSpeedEnabled,
            MaxHorizontalSpeed = ClampFinite(value.MaxHorizontalSpeed, 12, 0.5, 1000),
            MaxVerticalSpeed = ClampFinite(value.MaxVerticalSpeed, 18, 0.5, 1000),
            TeleportDistance = ClampFinite(value.TeleportDistance, 24, 4, 10000),
            HoverSeconds = Math.Clamp(value.HoverSeconds <= 0 ? 4 : value.HoverSeconds, 2, 600),
            FlightEnabled = value.FlightEnabled,
            NoClipEnabled = value.NoClipEnabled,
            FastBreakEnabled = value.FastBreakEnabled,
            FastBreakMultiplier = ClampFinite(value.FastBreakMultiplier, 0.35, 0.01, 1),
            FastBreakWindowSeconds = Math.Clamp(value.FastBreakWindowSeconds <= 0 ? 12 : value.FastBreakWindowSeconds, 3, 3600),
            FastBreakMinimumSamples = Math.Clamp(value.FastBreakMinimumSamples <= 0 ? 4 : value.FastBreakMinimumSamples, 2, 1000),
            AutomationEnabled = value.AutomationEnabled,
            MaxActionsPerSecond = Math.Clamp(value.MaxActionsPerSecond <= 0 ? 12 : value.MaxActionsPerSecond, 1, 1000),
            AutomationWindowSeconds = Math.Clamp(value.AutomationWindowSeconds <= 0 ? 10 : value.AutomationWindowSeconds, 3, 3600),
            AutomationMinimumSamples = Math.Clamp(value.AutomationMinimumSamples <= 0 ? 12 : value.AutomationMinimumSamples, 2, 5000),
            CombatEnabled = value.CombatEnabled,
            MaxAttacksPerSecond = Math.Clamp(value.MaxAttacksPerSecond <= 0 ? 8 : value.MaxAttacksPerSecond, 1, 1000),
            MaxAttackReach = ClampFinite(value.MaxAttackReach, 6, 2, 100),
            HealthEnabled = value.HealthEnabled,
            MaxUnexpectedHeal = ClampFinite(value.MaxUnexpectedHeal, 25, 1, 10000),
            OrePatternEnabled = value.OrePatternEnabled,
            OrePatternWindowMinutes = Math.Clamp(value.OrePatternWindowMinutes <= 0 ? 10 : value.OrePatternWindowMinutes, 1, 1440),
            OrePatternMinimumSamples = Math.Clamp(value.OrePatternMinimumSamples <= 0 ? 20 : value.OrePatternMinimumSamples, 5, 10000),
            OrePatternRatio = ClampFinite(value.OrePatternRatio, 0.65, 0.05, 1),
            MarketRateEnabled = value.MarketRateEnabled,
            MarketInteractionsPerMinute = Math.Clamp(value.MarketInteractionsPerMinute <= 0 ? 30 : value.MarketInteractionsPerMinute, 1, 10000)
        };
    }

    private static double ClampFinite(double value, double fallback, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = fallback;
        return Math.Clamp(value, min, max);
    }
}

internal sealed class AntiCheatWhitelistRule
{
    public bool Enabled { get; set; } = true;

    public bool Bypass { get; set; } = true;

    public string Id { get; set; } = string.Empty;

    public string PlayerUid { get; set; } = string.Empty;

    public string PlayerName { get; set; } = string.Empty;

    public string Role { get; set; } = string.Empty;

    public List<string> Groups { get; set; } = [];

    public List<string> Detectors { get; set; } = [];

    public List<string> Contexts { get; set; } = [];

    public DateTimeOffset? ExpiresAtUtc { get; set; }

    public double SpeedMultiplier { get; set; } = 1;

    public double ActionRateMultiplier { get; set; } = 1;

    public string Reason { get; set; } = string.Empty;

    public string CreatedBy { get; set; } = string.Empty;

    public static AntiCheatWhitelistRule Normalize(AntiCheatWhitelistRule value)
    {
        return new AntiCheatWhitelistRule
        {
            Enabled = value.Enabled,
            Bypass = value.Bypass,
            Id = value.Id?.Trim() ?? string.Empty,
            PlayerUid = value.PlayerUid?.Trim() ?? string.Empty,
            PlayerName = value.PlayerName?.Trim() ?? string.Empty,
            Role = value.Role?.Trim() ?? string.Empty,
            Groups = NormalizeStrings(value.Groups),
            Detectors = NormalizeStrings(value.Detectors),
            Contexts = NormalizeStrings(value.Contexts),
            ExpiresAtUtc = value.ExpiresAtUtc,
            SpeedMultiplier = ClampFinite(value.SpeedMultiplier, 1, 1, 20),
            ActionRateMultiplier = ClampFinite(value.ActionRateMultiplier, 1, 1, 20),
            Reason = value.Reason?.Trim() ?? string.Empty,
            CreatedBy = value.CreatedBy?.Trim() ?? string.Empty
        };
    }

    private static List<string> NormalizeStrings(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(static item => !string.IsNullOrWhiteSpace(item))
            .Select(static item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(128)
            .ToList();
    }

    private static double ClampFinite(double value, double fallback, double min, double max)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            value = fallback;
        return Math.Clamp(value, min, max);
    }
}
