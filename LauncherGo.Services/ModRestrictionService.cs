using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     Persists client mod restrictions and synchronizes Vintage Story's native
///     pre-load blacklist/whitelist handshake fields.
/// </summary>
public sealed class ModRestrictionService(
    IInstanceServerConfigService serverConfigService,
    IInstanceModService instanceModService) : IModRestrictionService
{
    private const string RestrictionModId = "launchergorestriction";
    private const string RestrictionModVersion = "1.0.0";
    private const string RestrictionModFolderName = "launchergorestriction";
    private const string RestrictionModZipName = "launchergorestriction.zip";
    private const string RestrictionModDllName = "launchergorestriction.dll";
    private const string SettingsRelativePath = "ModConfig/launchergorestriction.json";

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ModRestrictionSettings> LoadAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settingsPath = GetSettingsPath(profile);
        if (File.Exists(settingsPath))
        {
            try
            {
                var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                var parsed = JsonSerializer.Deserialize<ModRestrictionSettings>(json, JsonReadOptions);
                if (parsed is not null)
                {
                    return Normalize(parsed);
                }
            }
            catch (JsonException)
            {
                // Fall back to the native server config when the managed file is malformed.
            }
            catch (IOException)
            {
                // Fall back to the native server config when the managed file cannot be read.
            }
        }

        var migrated = await TryLoadNativeSettingsAsync(profile, cancellationToken);
        if (migrated is not null)
        {
            return Normalize(migrated);
        }

        return new ModRestrictionSettings
        {
            BlacklistEnabled = false,
            ForceWhitelistEnabled = true,
            WhitelistModIds = await GetDefaultWhitelistAsync(profile, cancellationToken),
            BlacklistModIds = []
        };
    }

    public async Task SaveAsync(
        InstanceProfile profile,
        ModRestrictionSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = Normalize(settings);
        Validate(normalized);

        var settingsPath = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var json = JsonSerializer.Serialize(normalized, JsonWriteOptions);
        await File.WriteAllTextAsync(settingsPath, json, cancellationToken);

        await SyncNativeServerConfigAsync(profile, normalized, cancellationToken);
        await EnsureRestrictionModDeployedAsync(profile, cancellationToken);
    }

    public string GetSettingsPath(InstanceProfile profile)
    {
        return Path.Combine(
            WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath),
            SettingsRelativePath.Replace('/', Path.DirectorySeparatorChar));
    }

    private async Task<ModRestrictionSettings?> TryLoadNativeSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            if (JsonNode.Parse(rawJson) is not JsonObject root)
            {
                return null;
            }

            var hasBlacklist = root["ModIdBlackList"] is JsonArray;
            var hasWhitelist = root["ModIdWhiteList"] is JsonArray;
            if (!hasBlacklist && !hasWhitelist)
            {
                return null;
            }

            return new ModRestrictionSettings
            {
                BlacklistEnabled = hasBlacklist,
                ForceWhitelistEnabled = hasWhitelist,
                BlacklistModIds = ReadModIds(root["ModIdBlackList"]),
                WhitelistModIds = ReadModIds(root["ModIdWhiteList"])
            };
        }
        catch
        {
            return null;
        }
    }

    private async Task<List<string>> GetDefaultWhitelistAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var mods = await instanceModService.GetModsAsync(profile, cancellationToken);
            return mods
                .Where(static mod => !mod.IsDisabled)
                .Select(static mod => NormalizeModId(mod.ModId))
                .Where(static id => !string.IsNullOrWhiteSpace(id))
                .Where(id => !id.Equals(RestrictionModId, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    private async Task SyncNativeServerConfigAsync(
        InstanceProfile profile,
        ModRestrictionSettings settings,
        CancellationToken cancellationToken)
    {
        var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");

        root["ModIdBlackList"] = settings.BlacklistEnabled
            ? BuildModIdArray(settings.BlacklistModIds)
            : null;
        root["ModIdWhiteList"] = settings.ForceWhitelistEnabled
            ? BuildNativeWhitelistArray(settings.WhitelistModIds)
            : null;

        await serverConfigService.SaveRawJsonAsync(
            profile,
            root.ToJsonString(JsonWriteOptions),
            cancellationToken);
    }

    private async Task EnsureRestrictionModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);
        TryDeleteFile(Path.Combine(modsPath, RestrictionModZipName));

        var sourceRoot = ResolveEmbeddedRestrictionSourceRoot();
        var destinationRoot = Path.Combine(modsPath, RestrictionModFolderName);
        SyncDirectory(sourceRoot, destinationRoot);
        await RemoveRestrictionModFromDisabledListAsync(profile, cancellationToken);
    }

    private async Task RemoveRestrictionModFromDisabledListAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");
        var worldConfig = root["WorldConfig"] as JsonObject ?? new JsonObject();
        root["WorldConfig"] = worldConfig;
        var disabledMods = worldConfig["DisabledMods"] as JsonArray ?? new JsonArray();
        worldConfig["DisabledMods"] = disabledMods;

        var remaining = disabledMods
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .Where(static item =>
                !item.Equals(RestrictionModId, StringComparison.OrdinalIgnoreCase) &&
                !item.StartsWith($"{RestrictionModId}@", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        disabledMods.Clear();
        foreach (var item in remaining)
        {
            disabledMods.Add(item);
        }

        await serverConfigService.SaveRawJsonAsync(
            profile,
            root.ToJsonString(JsonWriteOptions),
            cancellationToken);
    }

    private static ModRestrictionSettings Normalize(ModRestrictionSettings settings)
    {
        return new ModRestrictionSettings
        {
            BlacklistEnabled = settings.BlacklistEnabled,
            ForceWhitelistEnabled = settings.ForceWhitelistEnabled,
            WhitelistModIds = NormalizeModIds(settings.WhitelistModIds),
            BlacklistModIds = NormalizeModIds(settings.BlacklistModIds)
        };
    }

    private static List<string> NormalizeModIds(IEnumerable<string>? values)
    {
        return (values ?? [])
            .Select(NormalizeModId)
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string NormalizeModId(string? value)
    {
        var normalized = value?.Trim().Trim('"', '\'', ',', ';') ?? string.Empty;
        var versionSeparator = normalized.IndexOf('@');
        if (versionSeparator > 0)
        {
            normalized = normalized[..versionSeparator];
        }

        return normalized.Trim().ToLowerInvariant();
    }

    private static void Validate(ModRestrictionSettings settings)
    {
        if (settings.BlacklistModIds.Contains(RestrictionModId, StringComparer.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"不能将 {RestrictionModId} 加入黑名单。");
        }

        if (!settings.BlacklistEnabled || !settings.ForceWhitelistEnabled)
        {
            return;
        }

        var conflicts = settings.WhitelistModIds
            .Intersect(settings.BlacklistModIds, StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (conflicts.Count > 0)
        {
            throw new InvalidOperationException($"黑白名单 Mod ID 不能重复：{string.Join(", ", conflicts)}");
        }
    }

    private static List<string> ReadModIds(JsonNode? node)
    {
        if (node is not JsonArray values)
        {
            return [];
        }

        return NormalizeModIds(values
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>()));
    }

    private static JsonArray BuildModIdArray(IEnumerable<string> values)
    {
        var result = new JsonArray();
        foreach (var value in values)
        {
            result.Add(value);
        }

        return result;
    }

    private static JsonArray BuildNativeWhitelistArray(IReadOnlyCollection<string> values)
    {
        // Keep the required universal mod active after the client applies the policy.
        // A real mod ID also enables an intentionally empty whitelist without the
        // broad substring matching caused by Vintage Story's "game" sentinel.
        return BuildModIdArray(values
            .Append(RestrictionModId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(static id => id, StringComparer.OrdinalIgnoreCase));
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, directory)));
        }

        var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
        var sourceRelativePaths = sourceFiles
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(sourcePath, file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in sourceFiles)
        {
            var destination = Path.Combine(destinationPath, Path.GetRelativePath(sourcePath, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            TryCopyFile(file, destination);
        }

        foreach (var file in Directory.EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories))
        {
            if (!sourceRelativePaths.Contains(NormalizeRelativePath(Path.GetRelativePath(destinationPath, file))))
            {
                TryDeleteFile(file);
            }
        }
    }

    private static string ResolveEmbeddedRestrictionSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", RestrictionModFolderName);
        if (Directory.Exists(primary))
        {
            return primary;
        }

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LauncherGo.Services",
            "EmbeddedMods",
            RestrictionModFolderName));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException(
            $"未找到内置 LauncherGo Restriction 模组，请重新构建启动器。查找路径：{primary}；{fallback}");
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
    }

    private static void TryCopyFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Copy(sourcePath, targetPath, true);
        }
        catch (UnauthorizedAccessException) when (IsLockedRestrictionDll(targetPath))
        {
        }
        catch (IOException) when (IsLockedRestrictionDll(targetPath))
        {
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (IOException)
        {
        }
    }

    private static bool IsLockedRestrictionDll(string path)
    {
        return File.Exists(path) &&
               Path.GetFileName(path).Equals(RestrictionModDllName, StringComparison.OrdinalIgnoreCase);
    }
}
