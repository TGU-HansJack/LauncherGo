namespace LauncherGo.Domains.Models;

/// <summary>
/// LauncherGo AntiCheat server-mod configuration.
/// The defaults deliberately run in monitor-only mode.
/// </summary>
public sealed class AntiCheatSettings
{
    public bool Enabled { get; init; }

    public bool MonitorOnly { get; init; } = true;

    public AntiCheatActionSettings Actions { get; init; } = new();

    public AntiCheatDetectorSettings Detectors { get; init; } = new();

    public IReadOnlyList<AntiCheatWhitelistRule> Whitelist { get; init; } = [];
}

public sealed class AntiCheatActionSettings
{
    public int WarningScore { get; init; } = 3;

    public int KickScore { get; init; } = 8;

    public int BanScore { get; init; } = 16;

    public int ScoreDecaySeconds { get; init; } = 120;

    public int AlertCooldownSeconds { get; init; } = 30;

    public bool WarnAdministrators { get; init; } = true;

    public bool KickEnabled { get; init; }

    public bool BanEnabled { get; init; }

    public bool PunishStatisticalDetections { get; init; }
}

public sealed class AntiCheatDetectorSettings
{
    public bool MovementSpeedEnabled { get; init; } = true;

    public double MaxHorizontalSpeed { get; init; } = 12;

    public double MaxVerticalSpeed { get; init; } = 18;

    public double TeleportDistance { get; init; } = 24;

    public int HoverSeconds { get; init; } = 4;

    public bool FlightEnabled { get; init; } = true;

    public bool NoClipEnabled { get; init; } = true;

    public bool FastBreakEnabled { get; init; } = true;

    public double FastBreakMultiplier { get; init; } = 0.35;

    public int FastBreakWindowSeconds { get; init; } = 12;

    public int FastBreakMinimumSamples { get; init; } = 4;

    public bool AutomationEnabled { get; init; } = true;

    public int MaxActionsPerSecond { get; init; } = 12;

    public int AutomationWindowSeconds { get; init; } = 10;

    public int AutomationMinimumSamples { get; init; } = 12;

    public bool CombatEnabled { get; init; } = true;

    public int MaxAttacksPerSecond { get; init; } = 8;

    public double MaxAttackReach { get; init; } = 6;

    public bool HealthEnabled { get; init; } = true;

    public double MaxUnexpectedHeal { get; init; } = 25;

    public bool OrePatternEnabled { get; init; } = true;

    public int OrePatternWindowMinutes { get; init; } = 10;

    public int OrePatternMinimumSamples { get; init; } = 20;

    public double OrePatternRatio { get; init; } = 0.65;

    public bool MarketRateEnabled { get; init; } = true;

    public int MarketInteractionsPerMinute { get; init; } = 30;
}

/// <summary>
/// A detector-scoped compatibility exception. A rule matching one detector does
/// not disable the remaining detectors for the player.
/// </summary>
public sealed class AntiCheatWhitelistRule
{
    public bool Enabled { get; init; } = true;

    /// <summary>
    /// True skips matching detectors. False keeps the detector active and only
    /// applies the configured threshold multipliers.
    /// </summary>
    public bool Bypass { get; init; } = true;

    public string Id { get; init; } = string.Empty;

    public string PlayerUid { get; init; } = string.Empty;

    public string PlayerName { get; init; } = string.Empty;

    public string Role { get; init; } = string.Empty;

    public IReadOnlyList<string> Groups { get; init; } = [];

    public IReadOnlyList<string> Detectors { get; init; } = [];

    public IReadOnlyList<string> Contexts { get; init; } = [];

    public DateTimeOffset? ExpiresAtUtc { get; init; }

    public double SpeedMultiplier { get; init; } = 1;

    public double ActionRateMultiplier { get; init; } = 1;

    public string Reason { get; init; } = string.Empty;

    public string CreatedBy { get; init; } = string.Empty;
}
