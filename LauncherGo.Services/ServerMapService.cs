using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     ServerMap 内置地图模组部署服务。
/// </summary>
public sealed class ServerMapService : IServerMapService
{
    private const string MapModId = "servermap";
    private const string MapModVersion = "0.1.10";
    private const string MapModFolderName = "servermap";
    private const string MapModZipName = "servermap.zip";
    private const string SettingsRelativePath = "ModConfig/servermap.json";

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly IInstanceServerConfigService _serverConfigService;

    public ServerMapService(IInstanceServerConfigService serverConfigService)
    {
        _serverConfigService = serverConfigService;
    }

    public async Task EnsureMapModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);

        TryDeleteFile(Path.Combine(modsPath, MapModZipName));

        var destination = Path.Combine(modsPath, MapModFolderName);
        var sourceRoot = ResolveEmbeddedMapSourceRoot();
        SyncDirectory(sourceRoot, destination);
        EnsureNoLegacyMapBinaries(destination);

        await EnsureDefaultConfigAsync(profile, cancellationToken);
        await RemoveMapModFromDisabledListAsync(profile, cancellationToken);
    }

    public Task<bool> GetMapModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        var folderPath = Path.Combine(modsPath, MapModFolderName);
        var zipPath = Path.Combine(modsPath, MapModZipName);
        var enabled = Directory.Exists(folderPath) || File.Exists(zipPath);
        return Task.FromResult(enabled);
    }

    private async Task EnsureDefaultConfigAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        var settingsPath = Path.Combine(profile.DirectoryPath, SettingsRelativePath);
        if (File.Exists(settingsPath))
        {
            try
            {
                var existingJson = await File.ReadAllTextAsync(settingsPath, cancellationToken);
                if (JsonNode.Parse(existingJson) is JsonObject existingRoot)
                {
                    HardenConfig(existingRoot);
                    await File.WriteAllTextAsync(
                        settingsPath,
                        existingRoot.ToJsonString(JsonWriteOptions) + Environment.NewLine,
                        cancellationToken);
                }
            }
            catch
            {
                // 配置存在但暂时无法规范化时，不阻断模组部署。
            }

            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        JsonObject root;
        var legacyPath = Path.Combine(profile.DirectoryPath, "ModConfig", "livemap.json");
        if (File.Exists(legacyPath))
        {
            try
            {
                var legacyJson = await File.ReadAllTextAsync(legacyPath, cancellationToken);
                root = JsonNode.Parse(legacyJson) as JsonObject ?? BuildDefaultConfig();
            }
            catch
            {
                root = BuildDefaultConfig();
            }
        }
        else
        {
            root = BuildDefaultConfig();
        }

        HardenConfig(root);
        await File.WriteAllTextAsync(
            settingsPath,
            root.ToJsonString(JsonWriteOptions) + Environment.NewLine,
            cancellationToken);
    }

    private static JsonObject BuildDefaultConfig()
    {
        return new JsonObject
        {
            ["DebugMode"] = false,
            ["Httpd"] = new JsonObject
            {
                ["Enabled"] = true,
                ["Port"] = 8080,
                ["BindAddress"] = "127.0.0.1"
            },
            ["Web"] = new JsonObject
            {
                ["Url"] = "http://127.0.0.1:8080",
                ["ReadOnly"] = false,
                ["TileType"] = "webp",
                ["TileQuality"] = 100,
                ["FriendlyUrls"] = false
            },
            ["Zoom"] = new JsonObject
            {
                ["Default"] = 0,
                ["MaxIn"] = -3,
                ["MaxOut"] = 8
            },
            ["Ui"] = new JsonObject
            {
                ["LogoText"] = "ServerMap",
                ["SiteTitle"] = "Vintage Story ServerMap"
            },
            ["Layers"] = new JsonObject
            {
                ["Players"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["UpdateInterval"] = 1,
                    ["DefaultShowLayer"] = true
                },
                ["Spawn"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["UpdateInterval"] = 30,
                    ["DefaultShowLayer"] = true
                },
                ["Traders"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["UpdateInterval"] = 30,
                    ["DefaultShowLayer"] = true
                },
                ["Translocators"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["UpdateInterval"] = 30,
                    ["DefaultShowLayer"] = true
                },
                ["VSCartographer"] = new JsonObject
                {
                    ["Enabled"] = true,
                    ["UpdateInterval"] = 30,
                    ["DefaultShowLayer"] = true
                }
            },
            ["Render"] = new JsonObject
            {
                ["FullRenderOnSeasonChange"] = true,
                ["ChunkCacheSize"] = 1000,
                ["EnableIncrementalSaves"] = true
            }
        };
    }

    private static void HardenConfig(JsonObject root)
    {
        var httpd = GetOrCreateObject(root, "Httpd", "httpd");
        var bind = ReadString(httpd, "BindAddress", "bindAddress");
        if (string.IsNullOrWhiteSpace(bind) ||
            bind.Equals("*", StringComparison.OrdinalIgnoreCase) ||
            bind.Equals("0.0.0.0", StringComparison.OrdinalIgnoreCase) ||
            bind.Equals("::", StringComparison.OrdinalIgnoreCase))
        {
            httpd["BindAddress"] = "127.0.0.1";
        }

        if (!httpd.ContainsKey("Enabled") && !httpd.ContainsKey("enabled"))
        {
            httpd["Enabled"] = true;
        }

        if (!httpd.ContainsKey("Port") && !httpd.ContainsKey("port"))
        {
            httpd["Port"] = 8080;
        }

        var web = GetOrCreateObject(root, "Web", "web");
        if (string.IsNullOrWhiteSpace(ReadString(web, "Url", "url")))
        {
            web["Url"] = "http://127.0.0.1:8080";
        }

        var layers = GetOrCreateObject(root, "Layers", "layers");
        EnableVisibleLayer(layers, "Traders", "traders");
        EnableVisibleLayer(layers, "Translocators", "translocators");
        EnableVisibleLayer(layers, "VSCartographer", "vscartographer");
    }

    private static void EnableVisibleLayer(JsonObject layers, string canonicalName, string legacyName)
    {
        var layer = GetOrCreateObject(layers, canonicalName, legacyName);
        layer["Enabled"] = true;
        layer["DefaultShowLayer"] = true;
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string canonicalName, string legacyName)
    {
        if (root[canonicalName] is JsonObject canonical)
        {
            return canonical;
        }

        if (root[legacyName] is JsonObject legacy)
        {
            root.Remove(legacyName);
            root[canonicalName] = legacy;
            return legacy;
        }

        var created = new JsonObject();
        root[canonicalName] = created;
        return created;
    }

    private static string ReadString(JsonObject node, string canonicalName, string legacyName)
    {
        if (node[canonicalName] is JsonValue canonical &&
            canonical.TryGetValue<string>(out var canonicalValue))
        {
            return canonicalValue.Trim();
        }

        if (node[legacyName] is JsonValue legacy &&
            legacy.TryGetValue<string>(out var legacyValue))
        {
            return legacyValue.Trim();
        }

        return string.Empty;
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);

        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        var sourceFiles = Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories).ToList();
        var sourceRelativeSet = sourceFiles
            .Select(file => NormalizeRelativePath(Path.GetRelativePath(sourcePath, file)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var file in sourceFiles)
        {
            var relative = Path.GetRelativePath(sourcePath, file);
            var target = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            TryCopyFile(file, target);
        }

        foreach (var targetFile in Directory.EnumerateFiles(destinationPath, "*", SearchOption.AllDirectories))
        {
            var relative = NormalizeRelativePath(Path.GetRelativePath(destinationPath, targetFile));
            if (sourceRelativeSet.Contains(relative))
            {
                continue;
            }

            TryDeleteFile(targetFile);
        }

        var directories = Directory.EnumerateDirectories(destinationPath, "*", SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length)
            .ToList();
        foreach (var directory in directories)
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
            {
                continue;
            }

            TryDeleteDirectory(directory);
        }
    }

    private static void EnsureNoLegacyMapBinaries(string destinationPath)
    {
        var legacyFiles = new[]
            {
                "LiveMap.dll",
                "livemap.deps.json"
            }
            .Select(fileName => Path.Combine(destinationPath, fileName))
            .Where(File.Exists)
            .ToList();
        if (legacyFiles.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            "旧地图模组文件仍被保留，通常是服务器正在运行并锁定了 DLL。请先停止服务器，再重新点击“部署地图模组”。残留文件：" +
            string.Join("；", legacyFiles.Select(Path.GetFileName)));
    }

    private static void TryCopyFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Copy(sourcePath, targetPath, true);
        }
        catch (UnauthorizedAccessException) when (ShouldSkipLockedTarget(targetPath))
        {
            // 运行中的服务器可能锁定 dll，此时保持现有文件并继续。
        }
        catch (IOException) when (ShouldSkipLockedTarget(targetPath))
        {
            // 运行中的服务器可能锁定 dll，此时保持现有文件并继续。
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
            // 文件被占用时不阻断流程。
        }
        catch (IOException)
        {
            // 文件被占用时不阻断流程。
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        try
        {
            Directory.Delete(path, true);
        }
        catch (UnauthorizedAccessException)
        {
            // 目录中有被占用文件时不阻断流程。
        }
        catch (IOException)
        {
            // 目录中有被占用文件时不阻断流程。
        }
    }

    private static bool ShouldSkipLockedTarget(string targetPath)
    {
        return File.Exists(targetPath) &&
               Path.GetExtension(targetPath).Equals(".dll", StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string ResolveEmbeddedMapSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", MapModFolderName);
        if (Directory.Exists(primary))
        {
            return primary;
        }

        var fallback = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "LauncherGo.Services",
                "EmbeddedMods",
                MapModFolderName));
        if (Directory.Exists(fallback))
        {
            return fallback;
        }

        throw new InvalidOperationException(
            $"未找到内置地图模组文件，请先重新构建启动器。查找路径：{primary}；{fallback}");
    }

    private async Task RemoveMapModFromDisabledListAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            if (JsonNode.Parse(rawJson) is not JsonObject root)
            {
                return;
            }
            if (root["WorldConfig"] is not JsonObject worldConfig)
            {
                return;
            }
            if (worldConfig["DisabledMods"] is not JsonArray disabledMods)
            {
                return;
            }

            var beforeCount = disabledMods.Count;
            var cleanupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                MapModId,
                $"{MapModId}@{MapModVersion}"
            };

            var remain = disabledMods
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .Where(item => !cleanupSet.Contains(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (remain.Count == beforeCount)
            {
                return;
            }

            disabledMods.Clear();
            foreach (var item in remain)
            {
                disabledMods.Add(item);
            }

            await _serverConfigService.SaveRawJsonAsync(
                profile,
                root.ToJsonString(JsonWriteOptions),
                cancellationToken);
        }
        catch
        {
            // 清理禁用项失败不阻断主流程。
        }
    }
}
