using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     实例模组服务默认实现
/// </summary>
public class InstanceModService(IInstanceServerConfigService serverConfigService) : IInstanceModService
{
    private static readonly HashSet<string> BuiltInDependencyIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "game",
        "creative",
        "survival",
        "vssurvivalmod",
        "vsessentials"
    };

    /// <inheritdoc />
    public async Task<IReadOnlyList<ModEntry>> GetModsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        Directory.CreateDirectory(modsPath);

        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var entries = new List<ModEntry>();

        foreach (var file in Directory.EnumerateFiles(modsPath, "*.zip", SearchOption.TopDirectoryOnly))
            entries.Add(ReadModFromZip(file, disabledSet, modConfigPath));

        foreach (var directory in Directory.EnumerateDirectories(modsPath, "*", SearchOption.TopDirectoryOnly))
            entries.Add(ReadModFromDirectory(directory, disabledSet, modConfigPath));

        var enabledModIds = entries
            .Where(static mod => !mod.IsDisabled)
            .Select(static mod => mod.ModId)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var normalized = entries.Select(mod =>
        {
            var issues = new List<string>(mod.DependencyIssues);
            foreach (var dependency in mod.Dependencies)
            {
                if (BuiltInDependencyIds.Contains(dependency.ModId)) continue;
                if (enabledModIds.Contains(dependency.ModId)) continue;

                issues.Add(
                    $"缺少依赖: {dependency.ModId}" +
                    (string.IsNullOrWhiteSpace(dependency.Version) ? string.Empty : $"@{dependency.Version}"));
            }

            var status = issues.Count > 0 ? "MissingDependency" : mod.Status;
            return new ModEntry
            {
                ModId = mod.ModId,
                Version = mod.Version,
                FilePath = mod.FilePath,
                ConfigPath = mod.ConfigPath,
                Status = status,
                IsDisabled = mod.IsDisabled,
                Dependencies = mod.Dependencies,
                DependencyIssues = issues
            };
        }).OrderBy(static mod => mod.ModId, StringComparer.OrdinalIgnoreCase).ToList();

        return normalized;
    }

    /// <inheritdoc />
    public async Task<ModEntry> ImportModZipAsync(
        InstanceProfile profile,
        string zipPath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(zipPath) || !File.Exists(zipPath))
            throw new InvalidOperationException("Mod ZIP 文件不存在。");
        if (!Path.GetExtension(zipPath).Equals(".zip", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("仅支持导入 ZIP 格式 Mod。");

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);

        var fileName = WorkspacePathHelper.SanitizeFileName(Path.GetFileName(zipPath));
        var destinationPath = Path.Combine(modsPath, fileName);
        File.Copy(zipPath, destinationPath, overwrite: true);

        var disabledSet = await LoadDisabledModSetAsync(profile, cancellationToken);
        var modConfigPath = Path.Combine(WorkspacePathHelper.ResolveProfileDataPath(profile.DirectoryPath), "ModConfig");
        return ReadModFromZip(destinationPath, disabledSet, modConfigPath);
    }

    /// <inheritdoc />
    public async Task SetModEnabledAsync(
        InstanceProfile profile,
        string modId,
        string version,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
        var root = JsonNode.Parse(rawJson) as JsonObject
                   ?? throw new InvalidOperationException("配置格式错误。");

        var disabledArray = GetOrCreateDisabledModsArray(root);
        var modVersionKey = $"{modId}@{version}";

        var values = disabledArray
            .Where(static item => item is not null)
            .Select(static item => item!.GetValue<string>())
            .ToList();
        values.RemoveAll(value =>
            value.Equals(modId, StringComparison.OrdinalIgnoreCase) ||
            value.Equals(modVersionKey, StringComparison.OrdinalIgnoreCase));

        if (!enabled) values.Add(modVersionKey);

        disabledArray.Clear();
        foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            disabledArray.Add(value);

        await serverConfigService.SaveRawJsonAsync(
            profile,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            cancellationToken);
    }

    /// <inheritdoc />
    public async Task<int> DeleteModsAsync(
        InstanceProfile profile,
        IReadOnlyCollection<ModEntry> mods,
        CancellationToken cancellationToken = default)
    {
        if (mods.Count == 0) return 0;

        var modsRoot = EnsureDirectoryPrefix(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath));
        var deleted = 0;
        var deletedModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var deletedVersionKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods.Where(static mod => mod is not null))
        {
            cancellationToken.ThrowIfCancellationRequested();

            string fullPath;
            try
            {
                fullPath = Path.GetFullPath(mod.FilePath);
            }
            catch
            {
                continue;
            }

            if (!IsWithinDirectory(fullPath, modsRoot))
                continue;

            if (File.Exists(fullPath))
            {
                File.Delete(fullPath);
            }
            else if (Directory.Exists(fullPath))
            {
                Directory.Delete(fullPath, recursive: true);
            }
            else
            {
                continue;
            }

            deleted++;
            deletedModIds.Add(mod.ModId);
            deletedVersionKeys.Add($"{mod.ModId}@{mod.Version}");
        }

        if (deleted > 0)
            await RemoveDeletedModsFromDisabledListAsync(
                profile,
                deletedModIds,
                deletedVersionKeys,
                cancellationToken);

        return deleted;
    }

    private static ModEntry ReadModFromZip(string zipPath, HashSet<string> disabledSet, string modConfigPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(zipPath);
            var modInfo = archive.Entries.FirstOrDefault(entry =>
                entry.FullName.EndsWith("modinfo.json", StringComparison.OrdinalIgnoreCase));
            if (modInfo is null)
                return BuildFallbackEntry(zipPath, "InvalidMetadata", disabledSet, modConfigPath);

            using var stream = modInfo.Open();
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return BuildEntryFromModInfo(json, zipPath, disabledSet, modConfigPath);
        }
        catch
        {
            return BuildFallbackEntry(zipPath, "InvalidMetadata", disabledSet, modConfigPath);
        }
    }

    private static ModEntry ReadModFromDirectory(string directoryPath, HashSet<string> disabledSet, string modConfigPath)
    {
        try
        {
            var modInfoPath = Path.Combine(directoryPath, "modinfo.json");
            if (!File.Exists(modInfoPath))
                return BuildFallbackEntry(directoryPath, "InvalidMetadata", disabledSet, modConfigPath);

            var json = File.ReadAllText(modInfoPath);
            return BuildEntryFromModInfo(json, directoryPath, disabledSet, modConfigPath);
        }
        catch
        {
            return BuildFallbackEntry(directoryPath, "InvalidMetadata", disabledSet, modConfigPath);
        }
    }

    private static ModEntry BuildEntryFromModInfo(
        string modInfoJson,
        string filePath,
        HashSet<string> disabledSet,
        string modConfigPath)
    {
        var node = JsonNode.Parse(modInfoJson) as JsonObject;
        if (node is null)
            return BuildFallbackEntry(filePath, "InvalidMetadata", disabledSet, modConfigPath);

        var modId = node["modid"]?.GetValue<string>() ?? Path.GetFileNameWithoutExtension(filePath);
        var version = node["version"]?.GetValue<string>() ?? "unknown";
        var dependencies = ReadDependencies(node["dependencies"]);
        var disabled = disabledSet.Contains(modId) || disabledSet.Contains($"{modId}@{version}");

        return new ModEntry
        {
            ModId = modId,
            Version = version,
            FilePath = filePath,
            ConfigPath = ResolveModConfigPath(modConfigPath, modId),
            Status = "OK",
            IsDisabled = disabled,
            Dependencies = dependencies,
            DependencyIssues = []
        };
    }

    private static ModEntry BuildFallbackEntry(
        string filePath,
        string status,
        HashSet<string> disabledSet,
        string modConfigPath)
    {
        var fallbackId = Path.GetFileNameWithoutExtension(filePath);
        return new ModEntry
        {
            ModId = fallbackId,
            Version = "unknown",
            FilePath = filePath,
            ConfigPath = ResolveModConfigPath(modConfigPath, fallbackId),
            Status = status,
            IsDisabled = disabledSet.Contains(fallbackId),
            Dependencies = [],
            DependencyIssues = []
        };
    }

    private static IReadOnlyList<ModDependency> ReadDependencies(JsonNode? dependenciesNode)
    {
        var dependencies = new List<ModDependency>();
        switch (dependenciesNode)
        {
            case JsonObject dependencyObject:
                foreach (var pair in dependencyObject)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key)) continue;
                    dependencies.Add(new ModDependency
                    {
                        ModId = pair.Key,
                        Version = pair.Value?.GetValue<string>()
                    });
                }

                break;
            case JsonArray dependenciesArray:
                foreach (var dependencyNode in dependenciesArray)
                {
                    if (dependencyNode is not JsonObject dependencyItem) continue;
                    var modId = dependencyItem["modid"]?.GetValue<string>();
                    if (string.IsNullOrWhiteSpace(modId)) continue;

                    dependencies.Add(new ModDependency
                    {
                        ModId = modId,
                        Version = dependencyItem["version"]?.GetValue<string>()
                    });
                }

                break;
        }

        return dependencies;
    }

    private static string ResolveModConfigPath(string modConfigPath, string modId)
    {
        try
        {
            var fullModConfigPath = Path.GetFullPath(modConfigPath);
            if (!Directory.Exists(fullModConfigPath))
            {
                return fullModConfigPath;
            }

            var candidates = Directory
                .EnumerateFileSystemEntries(fullModConfigPath, "*", SearchOption.TopDirectoryOnly)
                .Where(path => IsModConfigMatch(path, modId))
                .OrderBy(path => Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)
                .ToList();

            return candidates.Count switch
            {
                0 => fullModConfigPath,
                1 => candidates[0],
                _ => string.Join(" | ", candidates)
            };
        }
        catch
        {
            return modConfigPath;
        }
    }

    private static bool IsModConfigMatch(string candidatePath, string modId)
    {
        var candidateName = Path.GetFileNameWithoutExtension(candidatePath);
        if (string.IsNullOrWhiteSpace(candidateName)) return false;

        var normalizedCandidate = NormalizeConfigToken(candidateName);
        var normalizedModId = NormalizeConfigToken(modId);
        if (normalizedCandidate.Equals(normalizedModId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return normalizedCandidate.Contains(normalizedModId, StringComparison.OrdinalIgnoreCase) ||
               normalizedModId.Contains(normalizedCandidate, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeConfigToken(string value)
    {
        return new string(value
            .Where(static ch => char.IsLetterOrDigit(ch))
            .ToArray());
    }

    private async Task<HashSet<string>> LoadDisabledModSetAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject;
            var disabledArray = root?["WorldConfig"]?["DisabledMods"] as JsonArray;
            if (disabledArray is null) return [];

            return disabledArray
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return [];
        }
    }

    private static JsonArray GetOrCreateDisabledModsArray(JsonObject root)
    {
        if (root["WorldConfig"] is not JsonObject worldConfig)
        {
            worldConfig = new JsonObject();
            root["WorldConfig"] = worldConfig;
        }

        if (worldConfig["DisabledMods"] is JsonArray disabledMods) return disabledMods;

        disabledMods = new JsonArray();
        worldConfig["DisabledMods"] = disabledMods;
        return disabledMods;
    }

    private async Task RemoveDeletedModsFromDisabledListAsync(
        InstanceProfile profile,
        HashSet<string> deletedModIds,
        HashSet<string> deletedVersionKeys,
        CancellationToken cancellationToken)
    {
        if (deletedModIds.Count == 0) return;

        try
        {
            var rawJson = await serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject
                       ?? throw new InvalidOperationException("配置格式错误。");

            var disabledArray = GetOrCreateDisabledModsArray(root);
            var originalValues = disabledArray
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .ToList();

            var cleaned = originalValues
                .Where(value =>
                {
                    if (deletedModIds.Contains(value)) return false;
                    return !deletedVersionKeys.Contains(value);
                })
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (cleaned.Count == originalValues.Count) return;

            disabledArray.Clear();
            foreach (var value in cleaned)
                disabledArray.Add(value);

            await serverConfigService.SaveRawJsonAsync(
                profile,
                root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
        }
        catch
        {
            // 删除模组已完成；配置清理失败时不阻断主流程。
        }
    }

    private static string EnsureDirectoryPrefix(string path)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath + Path.DirectorySeparatorChar;
    }

    private static bool IsWithinDirectory(string candidatePath, string directoryPrefix)
    {
        var fullCandidate = Path.GetFullPath(candidatePath);
        return fullCandidate.StartsWith(directoryPrefix, StringComparison.OrdinalIgnoreCase);
    }
}

