using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace LauncherGoAntiCheat;

/// <summary>
/// Server-authoritative behavior analysis for Vintage Story 1.22.x.
/// Client-only presentation features are intentionally not treated as proof of
/// cheating; the mod only evaluates state the server can observe.
/// </summary>
public sealed class LauncherGoAntiCheatSystem : ModSystem
{
    private const string ModConfigFileName = "launchergoanticheat.json";
    private const string EvidenceDirectoryName = "LauncherGoAntiCheat";
    private const string LogPrefix = "[LauncherGoAntiCheat]";
    private const int MovementSampleMilliseconds = 250;
    private const int InitialMovementGraceMilliseconds = 5000;
    private const int TeleportGraceMilliseconds = 2500;
    private const int RespawnGraceMilliseconds = 5000;
    private const int KnockbackGraceMilliseconds = 1500;
    private const int FindingMinimumIntervalMilliseconds = 900;
    private const int FallCheckDelayMilliseconds = 750;
    private const int MaxEvidenceQueue = 4096;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SafeConsolePlayerNameRegex =
        new("^[A-Za-z0-9_\\-]{1,64}$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly object _evidenceLock = new();
    private readonly Dictionary<string, PlayerDetectionState> _states = new(StringComparer.OrdinalIgnoreCase);
    private ICoreServerAPI? _api;
    private AntiCheatConfig _config = new();
    private long _tickListenerId;
    private bool _isDisposed;

    public override bool ShouldLoad(EnumAppSide forSide)
    {
        return forSide == EnumAppSide.Server;
    }

    public override double ExecuteOrder()
    {
        // Run after the normal player physics systems have updated positions.
        return 0.8;
    }

    public override void StartServerSide(ICoreServerAPI api)
    {
        _api = api;
        _config = LoadConfig();

        api.ChatCommands.Create("anticheat")
            .WithDescription("LauncherGo AntiCheat administration")
            .RequiresPrivilege(Privilege.controlserver)
            .WithArgs(api.ChatCommands.Parsers.OptionalAll("args"))
            .HandleWith(HandleAntiCheatCommand);

        api.Event.PlayerJoin += OnPlayerJoin;
        api.Event.PlayerNowPlaying += OnPlayerNowPlaying;
        api.Event.PlayerRespawn += OnPlayerRespawn;
        api.Event.PlayerDisconnect += OnPlayerDisconnect;
        api.Event.PlayerDeath += OnPlayerDeath;
        api.Event.PlayerSwitchGameMode += OnPlayerSwitchGameMode;
        api.Event.DidBreakBlock += OnDidBreakBlock;
        api.Event.HandInteract += OnHandInteract;
        api.Event.OnPlayerInteractEntity += OnPlayerInteractEntity;

        _tickListenerId = api.Event.RegisterGameTickListener(
            OnGameTick,
            OnGameTickError,
            MovementSampleMilliseconds,
            500);

        api.Server.Logger.Notification(
            "{0} loaded. Enabled={1}, MonitorOnly={2}, WhitelistRules={3}",
            LogPrefix,
            _config.Enabled,
            _config.MonitorOnly,
            _config.Whitelist.Count);
    }

    public override void Dispose()
    {
        if (_isDisposed)
            return;

        _isDisposed = true;
        if (_api is not null)
        {
            _api.Event.PlayerJoin -= OnPlayerJoin;
            _api.Event.PlayerNowPlaying -= OnPlayerNowPlaying;
            _api.Event.PlayerRespawn -= OnPlayerRespawn;
            _api.Event.PlayerDisconnect -= OnPlayerDisconnect;
            _api.Event.PlayerDeath -= OnPlayerDeath;
            _api.Event.PlayerSwitchGameMode -= OnPlayerSwitchGameMode;
            _api.Event.DidBreakBlock -= OnDidBreakBlock;
            _api.Event.HandInteract -= OnHandInteract;
            _api.Event.OnPlayerInteractEntity -= OnPlayerInteractEntity;
            if (_tickListenerId != 0)
                _api.Event.UnregisterGameTickListener(_tickListenerId);
        }

        foreach (var state in _states.Values)
        {
            if (state.InWorldActionHandler is null)
                continue;

            var player = _api?.World.PlayerByUid(state.PlayerUid) as IServerPlayer;
            if (player is not null)
                player.InWorldAction -= state.InWorldActionHandler;
        }

        _states.Clear();
    }

    private void OnPlayerJoin(IServerPlayer player)
    {
        var state = GetOrCreateState(player);
        if (player.Entity is not null)
            state.ResetMovement(player.Entity, NowMs(), InitialMovementGraceMilliseconds);
    }

    private void OnPlayerNowPlaying(IServerPlayer player)
    {
        var state = GetOrCreateState(player);
        AttachInWorldActionHandler(player, state);
        if (player.Entity is not null)
            state.ResetMovement(player.Entity, NowMs(), InitialMovementGraceMilliseconds);
    }

    private void OnPlayerRespawn(IServerPlayer player)
    {
        var state = GetOrCreateState(player);
        if (player.Entity is not null)
            state.ResetMovement(player.Entity, NowMs(), RespawnGraceMilliseconds);
    }

    private void OnPlayerDeath(IServerPlayer player, DamageSource damageSource)
    {
        if (_states.TryGetValue(player.PlayerUID, out var state))
        {
            state.HoverSeconds = 0;
            state.PendingFall = null;
            state.MovementGraceUntilMs = NowMs() + RespawnGraceMilliseconds;
        }
    }

    private void OnPlayerDisconnect(IServerPlayer player)
    {
        if (!_states.Remove(player.PlayerUID, out var state))
            return;

        if (state.InWorldActionHandler is not null)
            player.InWorldAction -= state.InWorldActionHandler;
    }

    private void OnPlayerSwitchGameMode(IServerPlayer player)
    {
        if (_states.TryGetValue(player.PlayerUID, out var state) && player.Entity is not null)
            state.ResetMovement(player.Entity, NowMs(), 1500);
    }

    private void OnDidBreakBlock(IServerPlayer byPlayer, int oldBlockId, BlockSelection blockSel)
    {
        if (!_config.Enabled || byPlayer.Entity is null || blockSel?.Position is null)
            return;

        if (byPlayer.WorldData.CurrentGameMode != EnumGameMode.Survival)
            return;

        var now = NowMs();
        var state = GetOrCreateState(byPlayer);
        var block = _api?.World.GetBlock(oldBlockId);
        if (block is null || block.Id == 0)
            return;

        var key = BlockKey(blockSel.Position);
        if (_config.Detectors.FastBreakEnabled &&
            state.BreakStartedAtMs.Remove(key, out var startedAt))
        {
            var elapsedSeconds = Math.Max(0.001, (now - startedAt) / 1000d);
            var expectedSeconds = EstimateBreakSeconds(byPlayer, block, blockSel);
            var contexts = GetContexts(byPlayer, now);
            var policy = ResolvePolicy(byPlayer, DetectorIds.BlockFastBreak, contexts);
            var allowedRatio = _config.Detectors.FastBreakMultiplier /
                               Math.Max(1, policy.ActionRateMultiplier);
            if (!policy.Bypass &&
                expectedSeconds >= 0.25 &&
                elapsedSeconds < expectedSeconds * allowedRatio)
            {
                state.FastBreakFindings.Enqueue(now);
                TrimQueue(state.FastBreakFindings, now - _config.Detectors.FastBreakWindowSeconds * 1000L);
                if (state.FastBreakFindings.Count >= _config.Detectors.FastBreakMinimumSamples)
                {
                    Report(
                        byPlayer,
                        DetectorIds.BlockFastBreak,
                        severity: 3,
                        statistical: false,
                        $"break interval below server-calculated resistance ({elapsedSeconds:F3}s < {expectedSeconds:F3}s)",
                        new
                        {
                            block = block.Code?.ToShortString() ?? "unknown",
                            elapsedSeconds,
                            expectedSeconds,
                            samples = state.FastBreakFindings.Count
                        },
                        contexts);
                    state.FastBreakFindings.Clear();
                }
            }
        }

        if (_config.Detectors.OrePatternEnabled && IsMiningSample(block))
        {
            var isOre = IsOreBlock(block);
            state.MiningSamples.Enqueue(new MiningSample(
                now,
                isOre,
                block.Code?.ToShortString() ?? "unknown"));
            TrimQueue(state.MiningSamples,
                now - _config.Detectors.OrePatternWindowMinutes * 60_000L);

            if (state.MiningSamples.Count >= _config.Detectors.OrePatternMinimumSamples)
            {
                var oreCount = state.MiningSamples.Count(static sample => sample.IsOre);
                var ratio = oreCount / (double)state.MiningSamples.Count;
                var contexts = GetContexts(byPlayer, now);
                var policy = ResolvePolicy(byPlayer, DetectorIds.MiningOrePattern, contexts);
                if (!policy.Bypass && ratio >= _config.Detectors.OrePatternRatio)
                {
                    Report(
                        byPlayer,
                        DetectorIds.MiningOrePattern,
                        severity: 1,
                        statistical: true,
                        $"ore ratio {ratio:P1} over {_config.Detectors.OrePatternWindowMinutes} minute window",
                        new
                        {
                            samples = state.MiningSamples.Count,
                            oreCount,
                            ratio,
                            windowMinutes = _config.Detectors.OrePatternWindowMinutes
                        },
                        contexts);
                    // Keep a short tail so a long vein does not flood logs.
                    while (state.MiningSamples.Count > _config.Detectors.OrePatternMinimumSamples / 2)
                        state.MiningSamples.Dequeue();
                }
            }
        }
    }

    private void OnHandInteract(
        IServerPlayer player,
        EnumHandInteractNw interaction,
        float secondsPassed,
        ref EnumHandling handling)
    {
        if (!_config.Enabled || player.Entity is null ||
            player.WorldData.CurrentGameMode != EnumGameMode.Survival)
            return;

        if (interaction != EnumHandInteractNw.StartHeldItemUse)
            return;

        var now = NowMs();
        var contexts = GetContexts(player, now);
        var category = ClassifyAction(player);
        RecordAction(player, category, now, contexts);
    }

    private void OnPlayerInteractEntity(
        Entity entity,
        IPlayer byPlayer,
        ItemSlot slot,
        Vec3d hitPosition,
        int mode,
        ref EnumHandling handling)
    {
        if (!_config.Enabled || byPlayer is not IServerPlayer player || player.Entity is null)
            return;

        if (entity is not EntityTradingHumanoid)
            return;

        if (!_config.Detectors.MarketRateEnabled)
            return;

        var now = NowMs();
        var state = GetOrCreateState(player);
        var contexts = GetContexts(player, now);
        var policy = ResolvePolicy(player, DetectorIds.MarketRate, contexts);
        if (policy.Bypass)
            return;

        state.MarketInteractions.Enqueue(now);
        TrimQueue(state.MarketInteractions, now - 60_000);
        if (state.MarketInteractions.Count > _config.Detectors.MarketInteractionsPerMinute * policy.ActionRateMultiplier)
        {
            Report(
                player,
                DetectorIds.MarketRate,
                severity: 1,
                statistical: true,
                $"trader interaction rate {state.MarketInteractions.Count}/min",
                new { samples = state.MarketInteractions.Count },
                contexts);
            while (state.MarketInteractions.Count > _config.Detectors.MarketInteractionsPerMinute / 2)
                state.MarketInteractions.Dequeue();
        }
    }

    private void OnInWorldAction(
        IServerPlayer player,
        EnumEntityAction action,
        bool on,
        ref EnumHandling handling)
    {
        // Client action packets are useful evidence for automation cadence, but
        // never grant or revoke permission by themselves.
        if (!_config.Enabled || !on || player.Entity is null ||
            player.WorldData.CurrentGameMode != EnumGameMode.Survival)
            return;

        if (action is EnumEntityAction.LeftMouseDown or EnumEntityAction.InWorldLeftMouseDown)
        {
            var now = NowMs();
            var contexts = GetContexts(player, now);
            if (player.CurrentEntitySelection?.Entity is { } target)
            {
                RecordAttack(player, target, now, contexts);
                return;
            }

            var blockSelection = player.CurrentBlockSelection;
            if (blockSelection?.Position is not null)
            {
                var block = _api?.World.BlockAccessor.GetBlock(blockSelection.Position);
                if (block is { Id: > 0, Resistance: > 0 })
                {
                    var state = GetOrCreateState(player);
                    state.BreakStartedAtMs.TryAdd(BlockKey(blockSelection.Position), now);
                    TrimOldBreakStarts(state, now);
                }
            }

            RecordAction(player, "interaction", now, contexts);
        }
    }

    private void OnGameTick(float elapsedSeconds)
    {
        if (_isDisposed || !_config.Enabled || _api is null)
            return;

        var now = NowMs();
        foreach (var player in _api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (player.ConnectionState != EnumClientState.Playing || player.Entity is null)
                continue;

            try
            {
                EvaluatePlayer(player, GetOrCreateState(player), now);
            }
            catch (Exception ex)
            {
                _api.Server.Logger.Warning(
                    "{0} detector evaluation failed for {1}: {2}",
                    LogPrefix,
                    player.PlayerName,
                    ex.Message);
            }
        }
    }

    private void OnGameTickError(Exception exception)
    {
        _api?.Server.Logger.Warning("{0} tick listener failed: {1}", LogPrefix, exception.Message);
    }

    private void EvaluatePlayer(IServerPlayer player, PlayerDetectionState state, long now)
    {
        var entity = player.Entity;
        if (entity is null)
            return;

        if (state.LastPosition is null || state.LastDimension != entity.Pos.Dimension)
        {
            state.ResetMovement(entity, now, 1000);
            CheckHealth(player, state, now);
            return;
        }

        var dt = (now - state.LastSampleMs) / 1000d;
        if (dt < 0.05 || dt > 3)
        {
            state.ResetMovement(entity, now, 1000);
            CheckHealth(player, state, now);
            return;
        }

        CheckPendingFall(player, state, now);
        CheckHealth(player, state, now);

        var contexts = GetContexts(player, now);
        var current = entity.Pos.XYZ;
        var previous = state.LastPosition;
        var dx = current.X - previous!.X;
        var dy = current.Y - previous.Y;
        var dz = current.Z - previous.Z;
        var horizontalDistance = Math.Sqrt(dx * dx + dz * dz);
        var totalDistance = Math.Sqrt(horizontalDistance * horizontalDistance + dy * dy);

        if (entity.Teleporting || entity.IsTeleport)
        {
            state.ResetMovement(entity, now, TeleportGraceMilliseconds);
            return;
        }

        var gameMode = player.WorldData.CurrentGameMode;
        if (gameMode is EnumGameMode.Creative or EnumGameMode.Spectator ||
            player.WorldData.FreeMove || player.WorldData.NoClip || entity.MountedOn is not null)
        {
            state.ResetMovement(entity, now);
            return;
        }

        var speedPolicy = ResolvePolicy(player, DetectorIds.MovementSpeed, contexts);
        var teleportPolicy = ResolvePolicy(player, DetectorIds.MovementTeleport, contexts);
        var movementMultiplier = GetMovementMultiplier(player, entity);
        var latencyMultiplier = GetLatencyMultiplier(player);
        var effectiveSpeedMultiplier = speedPolicy.SpeedMultiplier * movementMultiplier * latencyMultiplier;
        var maxHorizontal = _config.Detectors.MaxHorizontalSpeed * effectiveSpeedMultiplier;
        var maxVertical = _config.Detectors.MaxVerticalSpeed * effectiveSpeedMultiplier;
        var horizontalSpeed = horizontalDistance / dt;
        var verticalSpeed = Math.Abs(dy) / dt;

        if (totalDistance > _config.Detectors.TeleportDistance * teleportPolicy.SpeedMultiplier)
        {
            if (!teleportPolicy.Bypass)
            {
                Report(
                    player,
                    DetectorIds.MovementTeleport,
                    severity: 4,
                    statistical: false,
                    $"displacement {totalDistance:F2} blocks in {dt:F2}s without a server teleport state",
                    new { distance = totalDistance, dt, from = previous, to = current },
                    contexts);
            }

            // Avoid counting the same discontinuity as speed and flight too.
            state.ResetMovement(entity, now, TeleportGraceMilliseconds);
            return;
        }

        if (now >= state.MovementGraceUntilMs && !speedPolicy.Bypass &&
            _config.Detectors.MovementSpeedEnabled && horizontalSpeed > maxHorizontal)
        {
            Report(
                player,
                DetectorIds.MovementSpeed,
                severity: 2,
                statistical: false,
                $"horizontal speed {horizontalSpeed:F2} blocks/s exceeds {maxHorizontal:F2}",
                new { horizontalSpeed, maxHorizontal, dt, ping = player.Ping },
                contexts);
        }

        if (now >= state.MovementGraceUntilMs && !speedPolicy.Bypass &&
            _config.Detectors.MovementSpeedEnabled && verticalSpeed > maxVertical &&
            !entity.Swimming && !entity.FeetInLiquid && !entity.Controls.IsClimbing)
        {
            Report(
                player,
                DetectorIds.MovementVertical,
                severity: 2,
                statistical: false,
                $"vertical speed {verticalSpeed:F2} blocks/s exceeds {maxVertical:F2}",
                new { verticalSpeed, maxVertical, dt },
                contexts);
        }

        EvaluateFlight(player, state, entity, now, dt, horizontalDistance, dy, contexts);
        EvaluateNoClip(player, state, entity, now, horizontalDistance, contexts);
        EvaluateFall(player, state, entity, now, contexts);

        state.LastPosition = current.Clone();
        state.LastDimension = entity.Pos.Dimension;
        state.LastSampleMs = now;
        TrimOldBreakStarts(state, now);
    }

    private void EvaluateFlight(
        IServerPlayer player,
        PlayerDetectionState state,
        EntityPlayer entity,
        long now,
        double dt,
        double horizontalDistance,
        double verticalDelta,
        HashSet<string> contexts)
    {
        if (!_config.Detectors.FlightEnabled || now < state.MovementGraceUntilMs)
        {
            state.HoverSeconds = 0;
            return;
        }

        var policy = ResolvePolicy(player, DetectorIds.MovementFlight, contexts);
        if (policy.Bypass)
        {
            state.HoverSeconds = 0;
            return;
        }

        var legitimateDetachedMode = entity.Swimming ||
                                     entity.FeetInLiquid ||
                                     entity.Controls.IsClimbing ||
                                     entity.Controls.Gliding;
        if (!legitimateDetachedMode &&
            (entity.Controls.IsFlying || entity.Controls.DetachedMode) &&
            !player.WorldData.FreeMove)
        {
            Report(
                player,
                DetectorIds.MovementFlight,
                severity: 4,
                statistical: false,
                "client flight/detached control active without server free-move permission",
                new
                {
                    entity.Controls.IsFlying,
                    entity.Controls.DetachedMode,
                    entity.OnGround,
                    entity.Swimming
                },
                contexts);
        }

        var canHover = !entity.OnGround &&
                       !legitimateDetachedMode &&
                       entity.MountedOn is null &&
                       now >= state.KnockbackGraceUntilMs &&
                       Math.Abs(verticalDelta / Math.Max(dt, 0.001)) < 0.22 &&
                       horizontalDistance / Math.Max(dt, 0.001) < 8;
        if (!canHover)
        {
            state.HoverSeconds = 0;
            return;
        }

        state.HoverSeconds += dt;
        if (state.HoverSeconds >= _config.Detectors.HoverSeconds)
        {
            Report(
                player,
                DetectorIds.MovementFlight,
                severity: 2,
                statistical: true,
                $"airborne hover remained stable for {state.HoverSeconds:F1}s",
                new
                {
                    hoverSeconds = state.HoverSeconds,
                    verticalSpeed = verticalDelta / Math.Max(dt, 0.001)
                },
                contexts);
            state.HoverSeconds = 0;
        }
    }

    private void EvaluateNoClip(
        IServerPlayer player,
        PlayerDetectionState state,
        EntityPlayer entity,
        long now,
        double horizontalDistance,
        HashSet<string> contexts)
    {
        if (!_config.Detectors.NoClipEnabled || now < state.MovementGraceUntilMs)
        {
            state.SolidCollisionSamples = 0;
            return;
        }

        var policy = ResolvePolicy(player, DetectorIds.MovementNoClip, contexts);
        if (policy.Bypass)
        {
            state.SolidCollisionSamples = 0;
            return;
        }

        if (entity.Controls.NoClip && !player.WorldData.NoClip)
        {
            Report(
                player,
                DetectorIds.MovementNoClip,
                severity: 4,
                statistical: false,
                "client no-clip control active without server no-clip permission",
                new
                {
                    clientNoClip = entity.Controls.NoClip,
                    serverNoClip = player.WorldData.NoClip
                },
                contexts);
        }

        if (horizontalDistance < 0.15 || entity.OnGround && entity.CollidedHorizontally)
        {
            state.SolidCollisionSamples = 0;
            return;
        }

        var colliding = _api?.World.CollisionTester.IsColliding(
            _api.World.BlockAccessor,
            entity.CollisionBox,
            entity.Pos.XYZ,
            alsoCheckTouch: false) == true;
        state.SolidCollisionSamples = colliding ? state.SolidCollisionSamples + 1 : 0;
        if (state.SolidCollisionSamples >= 3)
        {
            Report(
                player,
                DetectorIds.MovementNoClip,
                severity: 3,
                statistical: false,
                "player collision box remained inside solid terrain while moving",
                new
                {
                    samples = state.SolidCollisionSamples,
                    position = entity.Pos.XYZ
                },
                contexts);
            state.SolidCollisionSamples = 0;
        }
    }

    private void EvaluateFall(
        IServerPlayer player,
        PlayerDetectionState state,
        EntityPlayer entity,
        long now,
        HashSet<string> contexts)
    {
        var fallExemptNow = entity.Swimming ||
                            entity.FeetInLiquid ||
                            entity.Controls.IsClimbing ||
                            entity.Controls.Gliding ||
                            entity.MountedOn is not null ||
                            now < state.MovementGraceUntilMs ||
                            now < state.KnockbackGraceUntilMs;

        if (!entity.OnGround)
        {
            if (!state.WasAirborne)
            {
                state.WasAirborne = true;
                state.AirbornePeakY = entity.Pos.Y;
                state.HealthAtAirborneStart = state.LastHealth;
                state.FallExempt = fallExemptNow;
            }
            else
            {
                state.AirbornePeakY = Math.Max(state.AirbornePeakY, entity.Pos.Y);
                state.FallExempt |= fallExemptNow;
            }

            return;
        }

        if (!state.WasAirborne)
            return;

        var distance = state.AirbornePeakY - entity.Pos.Y;
        var healthBeforeLanding = state.HealthAtAirborneStart;
        if (!state.FallExempt && distance >= 12 && !float.IsNaN(healthBeforeLanding))
        {
            state.PendingFall = new PendingFallCheck(
                now + FallCheckDelayMilliseconds,
                distance,
                healthBeforeLanding);
        }

        state.WasAirborne = false;
        state.FallExempt = false;
    }

    private void CheckPendingFall(IServerPlayer player, PlayerDetectionState state, long now)
    {
        if (state.PendingFall is not { } pending || now < pending.CheckAtMs)
            return;

        state.PendingFall = null;
        var health = player.Entity?.GetBehavior<EntityBehaviorHealth>();
        if (health is null || health.Health < pending.HealthBeforeLanding - 0.05f)
            return;

        var contexts = GetContexts(player, now);
        var policy = ResolvePolicy(player, DetectorIds.MovementNoFall, contexts);
        if (policy.Bypass)
            return;

        Report(
            player,
            DetectorIds.MovementNoFall,
            severity: 1,
            statistical: true,
            $"no observed health loss after a {pending.Distance:F1}-block fall",
            new
            {
                fallDistance = pending.Distance,
                healthBefore = pending.HealthBeforeLanding,
                healthAfter = health.Health
            },
            contexts);
    }

    private void CheckHealth(IServerPlayer player, PlayerDetectionState state, long now)
    {
        if (!_config.Detectors.HealthEnabled || player.Entity is null)
            return;

        var health = player.Entity.GetBehavior<EntityBehaviorHealth>();
        if (health is null || health.MaxHealth <= 0)
            return;

        var contexts = GetContexts(player, now);
        if (health.Health > health.MaxHealth + 0.05f)
        {
            Report(
                player,
                DetectorIds.HealthMaximum,
                severity: 2,
                statistical: true,
                $"health {health.Health:F2} exceeds authoritative max {health.MaxHealth:F2}",
                new { health = health.Health, maxHealth = health.MaxHealth },
                contexts);
        }

        if (!float.IsNaN(state.LastHealth))
        {
            var delta = health.Health - state.LastHealth;
            if (delta < -0.05f)
                state.KnockbackGraceUntilMs = Math.Max(state.KnockbackGraceUntilMs, now + KnockbackGraceMilliseconds);

            if (delta > _config.Detectors.MaxUnexpectedHeal)
            {
                Report(
                    player,
                    DetectorIds.HealthHealRate,
                    severity: 1,
                    statistical: true,
                    $"health increased by {delta:F2} inside one sample",
                    new
                    {
                        previousHealth = state.LastHealth,
                        health = health.Health,
                        delta
                    },
                    contexts);
            }
        }

        state.LastHealth = health.Health;
        state.LastMaxHealth = health.MaxHealth;
    }

    private void RecordAction(
        IServerPlayer player,
        string category,
        long now,
        HashSet<string> contexts)
    {
        if (!_config.Detectors.AutomationEnabled)
            return;

        var detector = DetectorIds.Automation(category);
        var policy = ResolvePolicy(player, detector, contexts);
        if (policy.Bypass)
            return;

        var state = GetOrCreateState(player);
        if (!state.ActionTimesByCategory.TryGetValue(category, out var samples))
        {
            samples = new Queue<long>();
            state.ActionTimesByCategory[category] = samples;
        }

        samples.Enqueue(now);
        TrimQueue(samples, now - _config.Detectors.AutomationWindowSeconds * 1000L);
        while (samples.Count > MaxEvidenceQueue)
            samples.Dequeue();

        var oneSecondCount = samples.Count(timestamp => timestamp >= now - 1000);
        var allowedRate = _config.Detectors.MaxActionsPerSecond * policy.ActionRateMultiplier;
        if (oneSecondCount > allowedRate)
        {
            Report(
                player,
                detector,
                severity: 1,
                statistical: true,
                $"{category} action rate {oneSecondCount}/s exceeds {allowedRate:F1}/s",
                new { category, oneSecondCount, allowedRate },
                contexts);
        }

        if (samples.Count < _config.Detectors.AutomationMinimumSamples)
            return;

        var recent = samples.TakeLast(_config.Detectors.AutomationMinimumSamples).ToArray();
        var intervals = recent.Zip(recent.Skip(1), static (left, right) => (double)(right - left)).ToArray();
        if (intervals.Length < 4)
            return;

        var mean = intervals.Average();
        if (mean < 80 || mean > 3000)
            return;

        var variance = intervals.Sum(value => (value - mean) * (value - mean)) / intervals.Length;
        var coefficientOfVariation = Math.Sqrt(variance) / mean;
        if (coefficientOfVariation < 0.025)
        {
            Report(
                player,
                detector,
                severity: 1,
                statistical: true,
                $"highly periodic {category} inputs (CV={coefficientOfVariation:F4})",
                new
                {
                    category,
                    samples = recent.Length,
                    meanIntervalMs = mean,
                    coefficientOfVariation
                },
                contexts);
        }
    }

    private void RecordAttack(
        IServerPlayer player,
        Entity target,
        long now,
        HashSet<string> contexts)
    {
        if (!_config.Detectors.CombatEnabled || player.Entity is null)
            return;

        var reachPolicy = ResolvePolicy(player, DetectorIds.CombatReach, contexts);
        var distance = player.Entity.Pos.DistanceTo(target.Pos);
        var maxReach = _config.Detectors.MaxAttackReach * reachPolicy.SpeedMultiplier;
        if (!reachPolicy.Bypass && distance > maxReach)
        {
            Report(
                player,
                DetectorIds.CombatReach,
                severity: 3,
                statistical: false,
                $"target distance {distance:F2} exceeds configured reach {maxReach:F2}",
                new
                {
                    distance,
                    maxReach,
                    target = target.Code?.ToShortString() ?? "unknown",
                    target.EntityId
                },
                contexts);
        }

        var state = GetOrCreateState(player);
        state.Attacks.Enqueue(new AttackSample(now, target.EntityId));
        TrimQueue(state.Attacks, now - 1000);
        while (state.Attacks.Count > 256)
            state.Attacks.Dequeue();

        var ratePolicy = ResolvePolicy(player, DetectorIds.CombatRate, contexts);
        var allowedRate = _config.Detectors.MaxAttacksPerSecond * ratePolicy.ActionRateMultiplier;
        if (!ratePolicy.Bypass && state.Attacks.Count > allowedRate)
        {
            Report(
                player,
                DetectorIds.CombatRate,
                severity: 1,
                statistical: true,
                $"attack input rate {state.Attacks.Count}/s exceeds {allowedRate:F1}/s",
                new { attacks = state.Attacks.Count, allowedRate },
                contexts);
        }

        var uniqueTargets = state.Attacks.Select(static sample => sample.TargetEntityId).Distinct().Count();
        var targetPolicy = ResolvePolicy(player, DetectorIds.CombatMultiTarget, contexts);
        if (!targetPolicy.Bypass && uniqueTargets >= 4)
        {
            Report(
                player,
                DetectorIds.CombatMultiTarget,
                severity: 1,
                statistical: true,
                $"attacked {uniqueTargets} distinct targets inside one second",
                new { attacks = state.Attacks.Count, uniqueTargets },
                contexts);
        }
    }

    private void Report(
        IServerPlayer player,
        string detector,
        int severity,
        bool statistical,
        string reason,
        object evidence,
        HashSet<string> contexts)
    {
        var state = GetOrCreateState(player);
        var now = NowMs();
        var policy = ResolvePolicy(player, detector, contexts);
        if (policy.Bypass)
            return;

        if (state.LastFindingByDetector.TryGetValue(detector, out var lastFinding) &&
            now - lastFinding < FindingMinimumIntervalMilliseconds)
        {
            return;
        }

        state.LastFindingByDetector[detector] = now;
        DecayScores(state, now);
        state.AlertScore += Math.Max(1, severity);
        if (!statistical || _config.Actions.PunishStatisticalDetections)
            state.EnforcementScore += Math.Max(1, severity);

        var eventId = Guid.NewGuid().ToString("N");
        var payload = new
        {
            schema = 1,
            eventId,
            timestampUtc = DateTimeOffset.UtcNow,
            playerUid = player.PlayerUID,
            playerName = player.PlayerName,
            detector,
            confidence = statistical ? "statistical" : "server-authoritative",
            severity,
            alertScore = Math.Round(state.AlertScore, 2),
            enforcementScore = Math.Round(state.EnforcementScore, 2),
            reason,
            contexts = contexts.OrderBy(static context => context).ToArray(),
            matchedWhitelistRules = policy.MatchedRuleIds,
            monitorOnly = _config.MonitorOnly,
            evidence
        };
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        WriteEvidenceLine(json);

        if (state.AlertScore >= _config.Actions.WarningScore &&
            now - state.LastAdministratorAlertMs >= _config.Actions.AlertCooldownSeconds * 1000L)
        {
            state.LastAdministratorAlertMs = now;
            _api?.Server.Logger.Warning("{0} ALERT {1}", LogPrefix, json);
            if (_config.Actions.WarnAdministrators)
                NotifyAdministrators(player, detector, reason, state.AlertScore, statistical);
        }

        if (_config.MonitorOnly)
            return;

        ApplyPunishment(player, state, detector, statistical);
    }

    private void ApplyPunishment(
        IServerPlayer player,
        PlayerDetectionState state,
        string detector,
        bool statistical)
    {
        if (statistical && !_config.Actions.PunishStatisticalDetections)
            return;

        if (_config.Actions.BanEnabled &&
            !state.BanIssued &&
            state.EnforcementScore >= _config.Actions.BanScore)
        {
            state.BanIssued = true;
            var commandIssued = SafeConsolePlayerNameRegex.IsMatch(player.PlayerName);
            if (commandIssued)
            {
                _api?.InjectConsole(
                    $"/ban {player.PlayerName} LauncherGoAntiCheat:{SanitizeReasonToken(detector)}");
            }

            WriteActionEvidence(player, detector, commandIssued ? "ban" : "ban-fallback-kick");
            player.Disconnect("LauncherGo AntiCheat: account blocked after repeated server-authoritative violations.");
            return;
        }

        if (_config.Actions.KickEnabled &&
            !state.KickIssued &&
            state.EnforcementScore >= _config.Actions.KickScore)
        {
            state.KickIssued = true;
            WriteActionEvidence(player, detector, "kick");
            player.Disconnect("LauncherGo AntiCheat: disconnected after repeated server-authoritative violations.");
        }
    }

    private void NotifyAdministrators(
        IServerPlayer suspect,
        string detector,
        string reason,
        double score,
        bool statistical)
    {
        if (_api is null)
            return;

        var confidence = statistical ? "统计信号" : "服务端高置信";
        var message = $"[AntiCheat] {suspect.PlayerName} | {detector} | {confidence} | score={score:F1} | {reason}";
        foreach (var player in _api.World.AllOnlinePlayers.OfType<IServerPlayer>())
        {
            if (player.HasPrivilege(Privilege.controlserver))
            {
                player.SendMessage(
                    GlobalConstants.GeneralChatGroup,
                    message,
                    EnumChatType.Notification);
            }
        }
    }

    private void WriteActionEvidence(IServerPlayer player, string detector, string action)
    {
        var payload = JsonSerializer.Serialize(new
        {
            schema = 1,
            eventId = Guid.NewGuid().ToString("N"),
            timestampUtc = DateTimeOffset.UtcNow,
            playerUid = player.PlayerUID,
            playerName = player.PlayerName,
            detector,
            action
        }, JsonOptions);
        WriteEvidenceLine(payload);
        _api?.Server.Logger.Warning("{0} ACTION {1}", LogPrefix, payload);
    }

    private void WriteEvidenceLine(string json)
    {
        try
        {
            var directory = Path.Combine(GamePaths.DataPath, "ModData", EvidenceDirectoryName);
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, $"alerts-{DateTime.UtcNow:yyyy-MM-dd}.jsonl");
            lock (_evidenceLock)
            {
                File.AppendAllText(path, json + Environment.NewLine);
            }
        }
        catch (Exception ex)
        {
            _api?.Server.Logger.Warning("{0} failed to persist evidence: {1}", LogPrefix, ex.Message);
        }
    }

    private void DecayScores(PlayerDetectionState state, long now)
    {
        if (state.LastScoreUpdateMs <= 0)
        {
            state.LastScoreUpdateMs = now;
            return;
        }

        var elapsedSeconds = Math.Max(0, (now - state.LastScoreUpdateMs) / 1000d);
        var decay = elapsedSeconds / _config.Actions.ScoreDecaySeconds;
        state.AlertScore = Math.Max(0, state.AlertScore - decay);
        state.EnforcementScore = Math.Max(0, state.EnforcementScore - decay);
        state.LastScoreUpdateMs = now;
    }

    private HashSet<string> GetContexts(IServerPlayer player, long now)
    {
        var contexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var entity = player.Entity;
        if (entity is null)
            return contexts;

        if (entity.MountedOn is not null) contexts.Add("mounted");
        if (entity.Teleporting || entity.IsTeleport) contexts.Add("teleport");
        if (player.WorldData.FreeMove) contexts.Add("freemove");
        if (player.WorldData.NoClip) contexts.Add("noclip");
        if (entity.Swimming || entity.FeetInLiquid) contexts.Add("swimming");
        if (entity.Controls.IsClimbing) contexts.Add("climbing");
        if (entity.Controls.Gliding) contexts.Add("gliding");
        if (player.Ping >= 0.5f) contexts.Add("highping");
        if (_states.TryGetValue(player.PlayerUID, out var state) && now < state.KnockbackGraceUntilMs)
            contexts.Add("knockback");
        if (player.WorldData.CurrentGameMode == EnumGameMode.Creative) contexts.Add("creative");
        if (player.WorldData.CurrentGameMode == EnumGameMode.Spectator) contexts.Add("spectator");
        return contexts;
    }

    private DetectorPolicy ResolvePolicy(
        IServerPlayer player,
        string detector,
        HashSet<string> contexts)
    {
        return AntiCheatPolicy.Resolve(_config, player, detector, contexts, DateTimeOffset.UtcNow);
    }

    private PlayerDetectionState GetOrCreateState(IServerPlayer player)
    {
        if (_states.TryGetValue(player.PlayerUID, out var state))
            return state;

        state = new PlayerDetectionState
        {
            PlayerUid = player.PlayerUID,
            LastScoreUpdateMs = NowMs()
        };
        _states[player.PlayerUID] = state;
        AttachInWorldActionHandler(player, state);
        if (player.Entity is not null)
            state.ResetMovement(player.Entity, NowMs(), InitialMovementGraceMilliseconds);
        return state;
    }

    private void AttachInWorldActionHandler(IServerPlayer player, PlayerDetectionState state)
    {
        if (state.InWorldActionHandler is not null)
            return;

        OnEntityAction handler = (
            EnumEntityAction action,
            bool on,
            ref EnumHandling handling) => OnInWorldAction(player, action, on, ref handling);
        state.InWorldActionHandler = handler;
        player.InWorldAction += handler;
    }

    private double EstimateBreakSeconds(IServerPlayer player, Block block, BlockSelection blockSel)
    {
        if (_api is null)
            return 0;

        try
        {
            var resistance = Math.Max(0, block.GetResistance(_api.World.BlockAccessor, blockSel.Position));
            var slot = player.InventoryManager.ActiveHotbarSlot;
            var stack = slot?.Itemstack;
            var miningSpeed = stack is null
                ? 1f
                : stack.Collectible.GetMiningSpeed(stack, blockSel, block, player);
            return resistance / Math.Max(0.05, miningSpeed);
        }
        catch
        {
            return Math.Max(0, block.Resistance);
        }
    }

    private static string ClassifyAction(IServerPlayer player)
    {
        var blockCode = player.CurrentBlockSelection?.Position is { } position
            ? player.Entity?.World.BlockAccessor.GetBlock(position)?.Code?.ToShortString() ?? string.Empty
            : string.Empty;
        var itemCode = player.InventoryManager.ActiveHotbarSlot?.Itemstack?.Collectible?.Code?.ToShortString()
                       ?? string.Empty;
        var combined = $"{blockCode} {itemCode}".ToLowerInvariant();

        if (ContainsAny(combined, "anvil", "smith", "hammer")) return "forging";
        if (ContainsAny(combined, "knapping", "flint")) return "knapping";
        if (ContainsAny(combined, "clayform", "pottery")) return "pottery";
        if (ContainsAny(combined, "firepit", "oven", "cooking")) return "cooking";
        if (combined.Contains("bellows", StringComparison.Ordinal)) return "bellows";
        if (ContainsAny(combined, "fishing", "fishingrod")) return "fishing";
        if (ContainsAny(combined, "poultice", "bandage", "healing")) return "healing";
        if (ContainsAny(combined, "meal", "food", "bread", "fruit")) return "eating";
        return "interaction";
    }

    private static bool ContainsAny(string value, params string[] needles)
    {
        return needles.Any(needle => value.Contains(needle, StringComparison.Ordinal));
    }

    private static bool IsMiningSample(Block block)
    {
        var code = block.Code?.Path?.ToLowerInvariant() ?? string.Empty;
        return block.Resistance >= 0.5f &&
               ContainsAny(code, "ore", "rock", "stone", "granite", "andesite", "basalt");
    }

    private static bool IsOreBlock(Block block)
    {
        var code = block.Code?.Path?.ToLowerInvariant() ?? string.Empty;
        return code.Contains("ore", StringComparison.Ordinal) &&
               !code.Contains("oreless", StringComparison.Ordinal);
    }

    private static double GetMovementMultiplier(IServerPlayer player, EntityPlayer entity)
    {
        var statMultiplier = entity.Stats?.GetBlended("walkspeed") ?? 1f;
        var worldMultiplier = player.WorldData.MoveSpeedMultiplier;
        var controlMultiplier = entity.Controls.MovespeedMultiplier;
        var value = Math.Max(1, Math.Max(statMultiplier, Math.Max(worldMultiplier, controlMultiplier)));
        // Server-side stat mods are honored, but a runaway stat cannot disable
        // the detector completely. Larger exceptions belong in a scoped rule.
        return Math.Clamp(value, 1, 4);
    }

    private static double GetLatencyMultiplier(IServerPlayer player)
    {
        if (float.IsNaN(player.Ping) || player.Ping <= 0.25f)
            return 1;
        return 1 + Math.Min(player.Ping, 1.5f) * 0.5;
    }

    private static void TrimOldBreakStarts(PlayerDetectionState state, long now)
    {
        if (state.BreakStartedAtMs.Count == 0)
            return;

        foreach (var key in state.BreakStartedAtMs
                     .Where(pair => now - pair.Value > 60_000)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            state.BreakStartedAtMs.Remove(key);
        }

        if (state.BreakStartedAtMs.Count <= 256)
            return;

        foreach (var key in state.BreakStartedAtMs
                     .OrderBy(static pair => pair.Value)
                     .Take(state.BreakStartedAtMs.Count - 256)
                     .Select(static pair => pair.Key)
                     .ToList())
        {
            state.BreakStartedAtMs.Remove(key);
        }
    }

    private static void TrimQueue(Queue<long> queue, long minimumTimestamp)
    {
        while (queue.Count > 0 && queue.Peek() < minimumTimestamp)
            queue.Dequeue();
    }

    private static void TrimQueue(Queue<AttackSample> queue, long minimumTimestamp)
    {
        while (queue.Count > 0 && queue.Peek().TimestampMs < minimumTimestamp)
            queue.Dequeue();
    }

    private static void TrimQueue(Queue<MiningSample> queue, long minimumTimestamp)
    {
        while (queue.Count > 0 && queue.Peek().TimestampMs < minimumTimestamp)
            queue.Dequeue();
    }

    private static string BlockKey(BlockPos position)
    {
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{position.X}:{position.Y}:{position.Z}:{position.dimension}");
    }

    private long NowMs()
    {
        return _api?.World.ElapsedMilliseconds ?? Environment.TickCount64;
    }

    private static string SanitizeReasonToken(string value)
    {
        var chars = value.Where(static character =>
            char.IsLetterOrDigit(character) || character is '.' or '_' or '-').Take(64).ToArray();
        return chars.Length == 0 ? "violation" : new string(chars);
    }

    private TextCommandResult HandleAntiCheatCommand(TextCommandCallingArgs args)
    {
        var raw = (args[0] as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = (args.LastArg as string ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(raw))
            raw = args.RawArgs?.PopAll() ?? string.Empty;

        var parts = raw.Split([' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts[0].Equals("status", StringComparison.OrdinalIgnoreCase))
        {
            return TextCommandResult.Success(
                $"LauncherGo AntiCheat: enabled={_config.Enabled}, monitorOnly={_config.MonitorOnly}, " +
                $"onlineStates={_states.Count}, whitelistRules={_config.Whitelist.Count}");
        }

        switch (parts[0].ToLowerInvariant())
        {
            case "reload":
                _config = LoadConfig();
                return TextCommandResult.Success(
                    $"AntiCheat configuration reloaded. enabled={_config.Enabled}, rules={_config.Whitelist.Count}");
            case "enable":
                _config.Enabled = true;
                SaveConfig(_config);
                return TextCommandResult.Success("AntiCheat enabled. Current punishment mode is unchanged.");
            case "disable":
                _config.Enabled = false;
                SaveConfig(_config);
                return TextCommandResult.Success("AntiCheat disabled.");
            case "monitor":
                if (parts.Length < 2 || !TryParseOnOff(parts[1], out var monitorOnly))
                    return TextCommandResult.Error("Usage: /anticheat monitor on|off");
                _config.MonitorOnly = monitorOnly;
                SaveConfig(_config);
                return TextCommandResult.Success($"Monitor-only mode is now {(monitorOnly ? "on" : "off")}.");
            case "whitelist":
                return HandleWhitelistCommand(args, parts);
            default:
                return TextCommandResult.Error(
                    "Usage: /anticheat status|reload|enable|disable|monitor on|off|whitelist ...");
        }
    }

    private TextCommandResult HandleWhitelistCommand(
        TextCommandCallingArgs args,
        IReadOnlyList<string> parts)
    {
        if (parts.Count < 2 || parts[1].Equals("list", StringComparison.OrdinalIgnoreCase))
        {
            if (_config.Whitelist.Count == 0)
                return TextCommandResult.Success("AntiCheat whitelist is empty.");

            var lines = _config.Whitelist.Take(30).Select(rule =>
                $"{rule.Id}: {(rule.Bypass ? "bypass" : "limit")} " +
                $"player={FirstNonEmpty(rule.PlayerName, rule.PlayerUid, "-")} " +
                $"role={FirstNonEmpty(rule.Role, "-")} " +
                $"detectors={string.Join(',', rule.Detectors)} " +
                $"expires={rule.ExpiresAtUtc?.ToString("O") ?? "never"} " +
                $"reason={FirstNonEmpty(rule.Reason, "-")}");
            return TextCommandResult.Success(string.Join(Environment.NewLine, lines));
        }

        var action = parts[1].ToLowerInvariant();
        if (action == "remove")
        {
            if (parts.Count < 3)
                return TextCommandResult.Error("Usage: /anticheat whitelist remove <ruleId>");

            var removed = _config.Whitelist.RemoveAll(rule =>
                rule.Id.Equals(parts[2], StringComparison.OrdinalIgnoreCase));
            if (removed == 0)
                return TextCommandResult.Error("Whitelist rule not found.");

            SaveConfig(_config);
            return TextCommandResult.Success($"Removed {removed} whitelist rule(s).");
        }

        if (action is not ("add" or "limit"))
        {
            return TextCommandResult.Error(
                "Usage: /anticheat whitelist list|add <player> <detectors> [minutes] [reason]|" +
                "limit <player> <detectors> <speedMultiplier> <actionMultiplier> [minutes] [reason]|remove <ruleId>");
        }

        var isLimit = action == "limit";
        var minimumCount = isLimit ? 6 : 4;
        if (parts.Count < minimumCount)
        {
            return TextCommandResult.Error(isLimit
                ? "Usage: /anticheat whitelist limit <player> <detectorsCsv> <speedMultiplier> <actionMultiplier> [minutes] [reason]"
                : "Usage: /anticheat whitelist add <player> <detectorsCsv> [minutes] [reason]");
        }

        if (!TryResolvePlayer(parts[2], out var playerUid, out var playerName))
            return TextCommandResult.Error("Player not found in online or server player data.");

        var detectors = parts[3]
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(64)
            .ToList();
        if (detectors.Count == 0)
            return TextCommandResult.Error("At least one detector id is required. Use movement.* for a category.");

        var nextIndex = 4;
        var speedMultiplier = 1d;
        var actionMultiplier = 1d;
        if (isLimit)
        {
            if (!double.TryParse(parts[nextIndex++], NumberStyles.Float, CultureInfo.InvariantCulture, out speedMultiplier) ||
                !double.TryParse(parts[nextIndex++], NumberStyles.Float, CultureInfo.InvariantCulture, out actionMultiplier) ||
                speedMultiplier < 1 || actionMultiplier < 1)
            {
                return TextCommandResult.Error("Multipliers must be numbers greater than or equal to 1.");
            }

            speedMultiplier = Math.Clamp(speedMultiplier, 1, 20);
            actionMultiplier = Math.Clamp(actionMultiplier, 1, 20);
        }

        var minutes = 0;
        if (parts.Count > nextIndex && int.TryParse(parts[nextIndex], out var parsedMinutes))
        {
            minutes = Math.Clamp(parsedMinutes, 0, 525600);
            nextIndex++;
        }

        var createdBy = args.Caller.Player?.PlayerName ?? "console";
        var reason = parts.Count > nextIndex
            ? string.Join(' ', parts.Skip(nextIndex)).Trim()
            : "admin compatibility rule";
        var rule = new AntiCheatWhitelistRule
        {
            Enabled = true,
            Bypass = !isLimit,
            Id = Guid.NewGuid().ToString("N")[..8],
            PlayerUid = playerUid,
            PlayerName = playerName,
            Detectors = detectors,
            ExpiresAtUtc = minutes > 0 ? DateTimeOffset.UtcNow.AddMinutes(minutes) : null,
            SpeedMultiplier = speedMultiplier,
            ActionRateMultiplier = actionMultiplier,
            Reason = reason,
            CreatedBy = createdBy
        };
        _config.Whitelist.Add(rule);
        _config = AntiCheatConfig.Normalize(_config);
        SaveConfig(_config);
        return TextCommandResult.Success(
            $"Whitelist rule {rule.Id} added for {playerName} ({playerUid}); " +
            $"mode={(rule.Bypass ? "bypass" : "limit")}, detectors={string.Join(',', detectors)}.");
    }

    private bool TryResolvePlayer(string nameOrUid, out string playerUid, out string playerName)
    {
        playerUid = string.Empty;
        playerName = string.Empty;
        if (_api is null || string.IsNullOrWhiteSpace(nameOrUid))
            return false;

        var online = _api.World.AllOnlinePlayers.FirstOrDefault(player =>
            player.PlayerUID.Equals(nameOrUid, StringComparison.OrdinalIgnoreCase) ||
            player.PlayerName.Equals(nameOrUid, StringComparison.OrdinalIgnoreCase));
        if (online is not null)
        {
            playerUid = online.PlayerUID;
            playerName = online.PlayerName;
            return true;
        }

        try
        {
            var data = _api.PlayerData.GetPlayerDataByUid(nameOrUid) ??
                       _api.PlayerData.GetPlayerDataByLastKnownName(nameOrUid);
            if (data is null || string.IsNullOrWhiteSpace(data.PlayerUID))
                return false;

            playerUid = data.PlayerUID;
            playerName = data.LastKnownPlayername ?? nameOrUid;
            return true;
        }
        catch
        {
            try
            {
                var data = _api.PlayerData.GetPlayerDataByLastKnownName(nameOrUid);
                if (data is null || string.IsNullOrWhiteSpace(data.PlayerUID))
                    return false;
                playerUid = data.PlayerUID;
                playerName = data.LastKnownPlayername ?? nameOrUid;
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    private AntiCheatConfig LoadConfig()
    {
        var path = GetConfigPath();
        if (!File.Exists(path))
        {
            var defaults = AntiCheatConfig.Normalize(new AntiCheatConfig());
            SaveConfig(defaults);
            return defaults;
        }

        try
        {
            var json = File.ReadAllText(path);
            return AntiCheatConfig.Normalize(
                JsonSerializer.Deserialize<AntiCheatConfig>(json, ConfigJsonOptions));
        }
        catch (Exception ex)
        {
            _api?.Server.Logger.Warning(
                "{0} invalid config at {1}; keeping safe disabled defaults: {2}",
                LogPrefix,
                path,
                ex.Message);
            return AntiCheatConfig.Normalize(new AntiCheatConfig());
        }
    }

    private void SaveConfig(AntiCheatConfig config)
    {
        var path = GetConfigPath();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var normalized = AntiCheatConfig.Normalize(config);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(normalized, ConfigJsonOptions));
            File.Move(tempPath, path, true);
        }
        finally
        {
            try
            {
                if (File.Exists(tempPath))
                    File.Delete(tempPath);
            }
            catch
            {
                // A stale temp file is harmless and must not hide the save result.
            }
        }
    }

    private static string GetConfigPath()
    {
        return Path.Combine(GamePaths.ModConfig, ModConfigFileName);
    }

    private static bool TryParseOnOff(string value, out bool result)
    {
        if (value.Equals("on", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("true", StringComparison.OrdinalIgnoreCase) ||
            value == "1")
        {
            result = true;
            return true;
        }

        if (value.Equals("off", StringComparison.OrdinalIgnoreCase) ||
            value.Equals("false", StringComparison.OrdinalIgnoreCase) ||
            value == "0")
        {
            result = false;
            return true;
        }

        result = false;
        return false;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        return values.FirstOrDefault(static value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;
    }
}
