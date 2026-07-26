using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
/// Deploys the server-only LauncherGo AntiCheat mod and owns its per-profile
/// configuration. The mod itself remains independent from LauncherGo binaries.
/// </summary>
public sealed class ServerAntiCheatService : IServerAntiCheatService
{
    private const string ModId = "launchergoanticheat";
    private const string ModVersion = "0.1.0";
    private const string ModFolderName = "launchergoanticheat";
    private const string ModZipName = "launchergoanticheat.zip";
    private const string ModDllName = "launchergoanticheat.dll";
    private const string SettingsRelativePath = "ModConfig/launchergoanticheat.json";

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly IInstanceServerConfigService _serverConfigService;

    public ServerAntiCheatService(IInstanceServerConfigService serverConfigService)
    {
        _serverConfigService = serverConfigService;
    }

    public async Task<AntiCheatSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var defaults = NormalizeSettings(new AntiCheatSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var parsed = JsonSerializer.Deserialize<AntiCheatSettings>(json, JsonReadOptions)
                         ?? throw new JsonException("配置根节点不能为 null。");
            return NormalizeSettings(parsed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                $"反作弊配置格式错误，原文件已保留：{path}",
                ex);
        }
        catch (NotSupportedException ex)
        {
            throw new InvalidOperationException(
                $"反作弊配置包含不支持的内容，原文件已保留：{path}",
                ex);
        }
    }

    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        AntiCheatSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeSettings(settings);
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var tempPath = $"{path}.{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(normalized, JsonWriteOptions) + Environment.NewLine;
        try
        {
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, path, true);
        }
        finally
        {
            TryDeleteFile(tempPath);
        }
    }

    public async Task EnsureAntiCheatModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default,
        bool enableMod = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);

        TryDeleteFile(Path.Combine(modsPath, ModZipName));
        var destination = Path.Combine(modsPath, ModFolderName);
        SyncDirectory(ResolveEmbeddedSourceRoot(), destination);

        // Keep a valid, readable config beside the profile even when the mod
        // has not been configured yet. This also gives the generic Mod UI a
        // stable path for editing whitelist rules.
        await LoadSettingsAsync(profile, cancellationToken);
        await SetAntiCheatModEnabledAsync(profile, enableMod, cancellationToken);
    }

    public async Task<bool> GetAntiCheatModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsModPresent(profile))
            return false;

        try
        {
            var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            return !IsDisabled(JsonNode.Parse(rawJson) as JsonObject);
        }
        catch
        {
            return false;
        }
    }

    public async Task SetAntiCheatModEnabledAsync(
        InstanceProfile profile,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");
        var disabledMods = GetOrCreateDisabledModsArray(root);

        var remain = disabledMods
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .Where(item => !IsDisabledKey(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!enabled)
            remain.Add($"{ModId}@{ModVersion}");

        disabledMods.Clear();
        foreach (var item in remain.Distinct(StringComparer.OrdinalIgnoreCase))
            disabledMods.Add(item);

        await _serverConfigService.SaveRawJsonAsync(
            profile,
            root.ToJsonString(JsonWriteOptions),
            cancellationToken);
    }

    internal static AntiCheatSettings NormalizeSettings(AntiCheatSettings settings)
    {
        var actions = settings.Actions ?? new AntiCheatActionSettings();
        var detectors = settings.Detectors ?? new AntiCheatDetectorSettings();
        var warningScore = Math.Clamp(actions.WarningScore <= 0 ? 3 : actions.WarningScore, 1, 1000);
        var kickScore = Math.Max(
            warningScore,
            Math.Clamp(actions.KickScore <= 0 ? 8 : actions.KickScore, 1, 2000));
        var banScore = Math.Max(
            kickScore,
            Math.Clamp(actions.BanScore <= 0 ? 16 : actions.BanScore, 1, 5000));
        var whitelist = (settings.Whitelist ?? [])
            .Where(static rule => rule is not null)
            .Select(NormalizeWhitelistRule)
            .Take(2048)
            .ToList();

        return new AntiCheatSettings
        {
            Enabled = settings.Enabled,
            MonitorOnly = settings.MonitorOnly,
            Actions = new AntiCheatActionSettings
            {
                WarningScore = warningScore,
                KickScore = kickScore,
                BanScore = banScore,
                ScoreDecaySeconds = Math.Clamp(actions.ScoreDecaySeconds <= 0 ? 120 : actions.ScoreDecaySeconds, 10, 86400),
                AlertCooldownSeconds = Math.Clamp(actions.AlertCooldownSeconds <= 0 ? 30 : actions.AlertCooldownSeconds, 1, 3600),
                WarnAdministrators = actions.WarnAdministrators,
                KickEnabled = actions.KickEnabled,
                BanEnabled = actions.BanEnabled,
                PunishStatisticalDetections = actions.PunishStatisticalDetections
            },
            Detectors = NormalizeDetectors(detectors),
            Whitelist = whitelist
        };
    }

    private static AntiCheatDetectorSettings NormalizeDetectors(AntiCheatDetectorSettings value)
    {
        return new AntiCheatDetectorSettings
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

    private static AntiCheatWhitelistRule NormalizeWhitelistRule(AntiCheatWhitelistRule rule)
    {
        return new AntiCheatWhitelistRule
        {
            Enabled = rule.Enabled,
            Bypass = rule.Bypass,
            Id = rule.Id?.Trim() ?? string.Empty,
            PlayerUid = rule.PlayerUid?.Trim() ?? string.Empty,
            PlayerName = rule.PlayerName?.Trim() ?? string.Empty,
            Role = rule.Role?.Trim() ?? string.Empty,
            Groups = NormalizeStrings(rule.Groups),
            Detectors = NormalizeStrings(rule.Detectors),
            Contexts = NormalizeStrings(rule.Contexts),
            ExpiresAtUtc = rule.ExpiresAtUtc,
            SpeedMultiplier = ClampFinite(rule.SpeedMultiplier, 1, 1, 20),
            ActionRateMultiplier = ClampFinite(rule.ActionRateMultiplier, 1, 1, 20),
            Reason = rule.Reason?.Trim() ?? string.Empty,
            CreatedBy = rule.CreatedBy?.Trim() ?? string.Empty
        };
    }

    private static IReadOnlyList<string> NormalizeStrings(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
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

    private static string GetSettingsPath(InstanceProfile profile) =>
        Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), SettingsRelativePath);

    private static bool IsModPresent(InstanceProfile profile)
    {
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        return Directory.Exists(Path.Combine(modsPath, ModFolderName)) ||
               File.Exists(Path.Combine(modsPath, ModZipName));
    }

    private static JsonArray GetOrCreateDisabledModsArray(JsonObject root)
    {
        if (root["WorldConfig"] is not JsonObject worldConfig)
        {
            worldConfig = new JsonObject();
            root["WorldConfig"] = worldConfig;
        }

        if (worldConfig["DisabledMods"] is JsonArray disabledMods)
            return disabledMods;

        disabledMods = new JsonArray();
        worldConfig["DisabledMods"] = disabledMods;
        return disabledMods;
    }

    private static bool IsDisabled(JsonObject? root)
    {
        if (root?["WorldConfig"] is not JsonObject worldConfig ||
            worldConfig["DisabledMods"] is not JsonArray disabledMods)
            return false;

        return disabledMods
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .Any(IsDisabledKey);
    }

    private static bool IsDisabledKey(string value)
    {
        return value.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
               value.StartsWith($"{ModId}@", StringComparison.OrdinalIgnoreCase);
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
        var sourceSet = sourceFiles
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(sourcePath, file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var sourceFile in sourceFiles)
        {
            var relative = Path.GetRelativePath(sourcePath, sourceFile);
            var destination = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            TryCopyFile(sourceFile, destination);
        }

        foreach (var destinationFile in Directory.EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(destinationPath, destinationFile));
            if (!sourceSet.Contains(relative))
                TryDeleteFile(destinationFile);
        }
    }

    private static void TryCopyFile(string sourcePath, string destinationPath)
    {
        try
        {
            File.Copy(sourcePath, destinationPath, true);
        }
        catch (IOException) when (Path.GetFileName(destinationPath).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
        {
            // A running server may hold the assembly. Keep the loaded copy.
        }
        catch (UnauthorizedAccessException) when (Path.GetFileName(destinationPath).Equals(ModDllName, StringComparison.OrdinalIgnoreCase))
        {
            // A running server may hold the assembly. Keep the loaded copy.
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
            // Do not break startup because an old package is locked.
        }
        catch (UnauthorizedAccessException)
        {
            // Do not break startup because an old package is locked.
        }
    }

    private static string NormalizeRelativePath(string path) =>
        path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);

    private static string ResolveEmbeddedSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", ModFolderName);
        if (Directory.Exists(primary))
            return primary;

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..",
            "LauncherGo.Services", "EmbeddedMods", ModFolderName));
        if (Directory.Exists(fallback))
            return fallback;

        throw new InvalidOperationException(
            $"未找到内置反作弊模组文件，请先重新构建启动器。查找路径：{primary}；{fallback}");
    }
}
