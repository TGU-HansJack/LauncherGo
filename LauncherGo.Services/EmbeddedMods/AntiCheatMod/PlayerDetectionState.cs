using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace LauncherGoAntiCheat;

internal sealed class PlayerDetectionState
{
    public string PlayerUid { get; init; } = string.Empty;

    public Vec3d? LastPosition { get; set; }

    public int LastDimension { get; set; }

    public long LastSampleMs { get; set; }

    public long MovementGraceUntilMs { get; set; }

    public long KnockbackGraceUntilMs { get; set; }

    public double HoverSeconds { get; set; }

    public int SolidCollisionSamples { get; set; }

    public bool WasAirborne { get; set; }

    public bool FallExempt { get; set; }

    public double AirbornePeakY { get; set; }

    public float HealthAtAirborneStart { get; set; }

    public PendingFallCheck? PendingFall { get; set; }

    public float LastHealth { get; set; } = float.NaN;

    public float LastMaxHealth { get; set; } = float.NaN;

    public double AlertScore { get; set; }

    public double EnforcementScore { get; set; }

    public long LastScoreUpdateMs { get; set; }

    public long LastAdministratorAlertMs { get; set; }

    public bool KickIssued { get; set; }

    public bool BanIssued { get; set; }

    public Dictionary<string, long> LastFindingByDetector { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, long> BreakStartedAtMs { get; } = new(StringComparer.Ordinal);

    public Queue<long> FastBreakFindings { get; } = new();

    public Dictionary<string, Queue<long>> ActionTimesByCategory { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Queue<AttackSample> Attacks { get; } = new();

    public Queue<MiningSample> MiningSamples { get; } = new();

    public Queue<long> MarketInteractions { get; } = new();

    public OnEntityAction? InWorldActionHandler { get; set; }

    public void ResetMovement(EntityPlayer entity, long nowMs, int graceMilliseconds = 0)
    {
        LastPosition = entity.Pos.XYZ.Clone();
        LastDimension = entity.Pos.Dimension;
        LastSampleMs = nowMs;
        MovementGraceUntilMs = Math.Max(MovementGraceUntilMs, nowMs + graceMilliseconds);
        HoverSeconds = 0;
        SolidCollisionSamples = 0;
        WasAirborne = false;
        FallExempt = false;
        PendingFall = null;
    }
}

internal readonly record struct PendingFallCheck(
    long CheckAtMs,
    double Distance,
    float HealthBeforeLanding);

internal readonly record struct AttackSample(long TimestampMs, long TargetEntityId);

internal readonly record struct MiningSample(long TimestampMs, bool IsOre, string BlockCode);
