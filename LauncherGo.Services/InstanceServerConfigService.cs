using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class InstanceServerConfigService(ILauncherPreferencesService preferencesService) : IInstanceServerConfigService
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> EnsureConfigLocks =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    public async Task<ServerCommonSettings> LoadServerSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        return new ServerCommonSettings
        {
            ServerName = ReadString(root["ServerName"], "Vintage Story Server"),
            ServerDescription = ReadNullableString(root["ServerDescription"]),
            ServerUrl = ReadNullableString(root["ServerUrl"]),
            Ip = ReadNullableString(root["Ip"]),
            Port = ReadInt(root["Port"], 42420),
            MaxClients = ReadInt(root["MaxClients"], 16),
            MaxClientsInQueue = ReadInt(root["MaxClientsInQueue"], 0),
            Password = ReadNullableString(root["Password"]),
            AdvertiseServer = ReadBool(root["AdvertiseServer"], false),
            WhitelistMode = ReadInt(root["WhitelistMode"], 0),
            Upnp = ReadBool(root["Upnp"], false),
            AllowPvP = ReadBool(root["AllowPvP"], true),
            AllowFireSpread = ReadBool(root["AllowFireSpread"], true),
            AllowFallingBlocks = ReadBool(root["AllowFallingBlocks"], true),
            PassTimeWhenEmpty = ReadBool(root["PassTimeWhenEmpty"], false),
            WarnClientsAfterAfkSeconds = ReadInt(root["WarnClientsAfterAfkSeconds"], 0),
            KickClientsAfterAfkSeconds = ReadInt(root["KickClientsAfterAfkSeconds"], 0),
            ClientConnectionTimeout = ReadInt(root["ClientConnectionTimeout"], 150),
            MaxChunkRadius = ReadInt(root["MaxChunkRadius"], 12),
            DieBelowDiskSpaceMb = ReadInt(root["DieBelowDiskSpaceMb"], 400),
            CorruptionProtection = ReadBool(root["CorruptionProtection"], true),
            RegenerateCorruptChunks = ReadBool(root["RegenerateCorruptChunks"], false),
            StartupCommands = ReadString(root["StartupCommands"], string.Empty),
            VerifyPlayerAuth = ReadBool(root["VerifyPlayerAuth"], true),
            ServerLanguage = ReadString(root["ServerLanguage"], ResolveDefaultServerLanguage()),
            DefaultRoleCode = ReadString(root["DefaultRoleCode"], "suplayer"),
            WelcomeMessage = ReadString(root["WelcomeMessage"], string.Empty)
        };
    }

    public async Task<WorldSettings> LoadWorldSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");

        var mapSizeY = ReadNullableInt(worldConfig["MapSizeY"]) ?? ReadNullableInt(worldRules["worldHeight"]);
        return new WorldSettings
        {
            Seed = ReadString(worldConfig["Seed"], "123456789"),
            WorldName = ReadString(worldConfig["WorldName"], "A new world"),
            SaveFileLocation = ReadString(worldConfig["SaveFileLocation"], ResolveCurrentSaveFilePath(profile)),
            PlayStyle = ReadString(worldConfig["PlayStyle"], "surviveandbuild"),
            WorldType = ReadString(worldConfig["WorldType"], "standard"),
            WorldHeight = mapSizeY ?? 256
        };
    }

    public async Task<IReadOnlyList<WorldRuleValue>> LoadWorldRulesAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");

        return WorldRuleCatalog.DefaultRules
            .Select(rule => new WorldRuleValue
            {
                Definition = rule,
                Value = ReadFlexibleString(worldRules[rule.Key])
                        ?? ReadRuleFallbackValue(rule.Key, root, worldConfig)
                        ?? rule.DefaultValue
            })
            .ToList();
    }

    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerCommonSettings serverSettings,
        WorldSettings worldSettings,
        IReadOnlyList<WorldRuleValue> rules,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);

        root["ServerName"] = string.IsNullOrWhiteSpace(serverSettings.ServerName)
            ? "Vintage Story Server"
            : serverSettings.ServerName.Trim();
        root["ServerDescription"] = string.IsNullOrWhiteSpace(serverSettings.ServerDescription)
            ? null
            : serverSettings.ServerDescription.Trim();
        root["ServerUrl"] = string.IsNullOrWhiteSpace(serverSettings.ServerUrl)
            ? null
            : serverSettings.ServerUrl.Trim();
        root["Ip"] = string.IsNullOrWhiteSpace(serverSettings.Ip) ? null : serverSettings.Ip.Trim();
        root["Port"] = Math.Clamp(serverSettings.Port, 1, 65535);
        root["MaxClients"] = Math.Max(1, serverSettings.MaxClients);
        root["MaxClientsInQueue"] = Math.Max(0, serverSettings.MaxClientsInQueue);
        root["Password"] = string.IsNullOrWhiteSpace(serverSettings.Password) ? null : serverSettings.Password;
        root["AdvertiseServer"] = serverSettings.AdvertiseServer;
        root["WhitelistMode"] = Math.Clamp(serverSettings.WhitelistMode, 0, 2);
        root["Upnp"] = serverSettings.Upnp;
        root["AllowPvP"] = serverSettings.AllowPvP;
        root["AllowFireSpread"] = serverSettings.AllowFireSpread;
        root["AllowFallingBlocks"] = serverSettings.AllowFallingBlocks;
        root["PassTimeWhenEmpty"] = serverSettings.PassTimeWhenEmpty;
        root["WarnClientsAfterAfkSeconds"] = Math.Max(0, serverSettings.WarnClientsAfterAfkSeconds);
        root["KickClientsAfterAfkSeconds"] = Math.Max(0, serverSettings.KickClientsAfterAfkSeconds);
        root["ClientConnectionTimeout"] = Math.Max(1, serverSettings.ClientConnectionTimeout);
        root["MaxChunkRadius"] = Math.Max(1, serverSettings.MaxChunkRadius);
        root["DieBelowDiskSpaceMb"] = Math.Max(-1, serverSettings.DieBelowDiskSpaceMb);
        root["CorruptionProtection"] = serverSettings.CorruptionProtection;
        root["RegenerateCorruptChunks"] = serverSettings.RegenerateCorruptChunks;
        root["StartupCommands"] = string.IsNullOrWhiteSpace(serverSettings.StartupCommands)
            ? string.Empty
            : serverSettings.StartupCommands.Trim();
        root["VerifyPlayerAuth"] = serverSettings.VerifyPlayerAuth;
        root["ServerLanguage"] = string.IsNullOrWhiteSpace(serverSettings.ServerLanguage)
            ? ResolveDefaultServerLanguage()
            : serverSettings.ServerLanguage.Trim();
        root["DefaultRoleCode"] = string.IsNullOrWhiteSpace(serverSettings.DefaultRoleCode)
            ? "suplayer"
            : serverSettings.DefaultRoleCode.Trim();
        root["WelcomeMessage"] = string.IsNullOrWhiteSpace(serverSettings.WelcomeMessage)
            ? string.Empty
            : serverSettings.WelcomeMessage;
        root["ModPaths"] = BuildDefaultModPaths(profile);

        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        worldConfig["Seed"] = string.IsNullOrWhiteSpace(worldSettings.Seed) ? "123456789" : worldSettings.Seed.Trim();
        worldConfig["WorldName"] = string.IsNullOrWhiteSpace(worldSettings.WorldName) ? "A new world" : worldSettings.WorldName.Trim();
        worldConfig["SaveFileLocation"] = string.IsNullOrWhiteSpace(worldSettings.SaveFileLocation)
            ? ResolveCurrentSaveFilePath(profile)
            : Path.GetFullPath(worldSettings.SaveFileLocation.Trim());
        worldConfig["PlayStyle"] = string.IsNullOrWhiteSpace(worldSettings.PlayStyle) ? "surviveandbuild" : worldSettings.PlayStyle.Trim();
        worldConfig["WorldType"] = string.IsNullOrWhiteSpace(worldSettings.WorldType) ? "standard" : worldSettings.WorldType.Trim();
        worldConfig["MapSizeY"] = Math.Clamp(worldSettings.WorldHeight ?? 256, 64, 2048);

        var worldRules = GetOrCreateObject(worldConfig, "WorldConfiguration");
        if (worldSettings.WorldHeight.HasValue)
        {
            worldRules["worldHeight"] = Math.Clamp(worldSettings.WorldHeight.Value, 64, 2048);
        }

        foreach (var rule in rules)
        {
            var normalizedValue = rule.Value?.Trim();
            if (string.IsNullOrWhiteSpace(normalizedValue))
            {
                continue;
            }

            if (rule.Definition.Key.Equals("worldWidth", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldWidth))
            {
                root["MapSizeX"] = worldWidth;
                worldRules[rule.Definition.Key] = worldWidth;
                continue;
            }

            if (rule.Definition.Key.Equals("worldLength", StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var worldLength))
            {
                root["MapSizeZ"] = worldLength;
                worldRules[rule.Definition.Key] = worldLength;
                continue;
            }

            if (rule.Definition.Type == WorldRuleType.Boolean &&
                bool.TryParse(normalizedValue, out var boolValue))
            {
                worldRules[rule.Definition.Key] = boolValue;
            }
            else if (rule.Definition.Type == WorldRuleType.Number &&
                     int.TryParse(normalizedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
            {
                worldRules[rule.Definition.Key] = intValue;
            }
            else
            {
                worldRules[rule.Definition.Key] = normalizedValue;
            }
        }

        await SaveRootAsync(profile, root, cancellationToken);
    }

    public async Task<string> LoadRawJsonAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var root = await LoadRootAsync(profile, cancellationToken);
        return root.ToJsonString(JsonWriteOptions);
    }

    public async Task SaveRawJsonAsync(
        InstanceProfile profile,
        string json,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("JSON 内容为空。");
        }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"JSON 语法错误：{ex.Message}", ex);
        }

        if (node is not JsonObject root)
        {
            throw new InvalidOperationException("配置根节点必须是 JSON 对象。");
        }

        if (root["WorldConfig"] is not JsonObject)
        {
            throw new InvalidOperationException("配置必须包含 WorldConfig 对象。");
        }

        NormalizeImportedConfigPaths(profile, root);
        await SaveRootAsync(profile, root, cancellationToken);
    }

    public async Task ImportRawJsonAsync(
        InstanceProfile profile,
        string jsonFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(jsonFilePath))
        {
            throw new InvalidOperationException("导入配置文件路径不能为空。");
        }

        var fullPath = Path.GetFullPath(jsonFilePath.Trim());
        if (!File.Exists(fullPath))
        {
            throw new InvalidOperationException($"导入配置文件不存在：{fullPath}");
        }

        if (!Path.GetExtension(fullPath).Equals(".json", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅支持导入 JSON 配置文件。");
        }

        var rawJson = await File.ReadAllTextAsync(fullPath, cancellationToken);
        await SaveRawJsonAsync(profile, rawJson, cancellationToken);
    }

    private async Task<JsonObject> LoadRootAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        var configPath = LauncherWorkspacePathHelper.ProfileConfigPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);

        await EnsureGeneratedConfigAsync(profile, cancellationToken);

        if (!File.Exists(configPath))
        {
            return BuildDefaultRoot(profile);
        }

        var parsedRoot = await TryParseRootAsync(configPath, cancellationToken);
        if (parsedRoot is not null)
        {
            return parsedRoot;
        }

        throw new InvalidOperationException($"配置文件无法解析，已保留原文件未覆盖：{configPath}");
    }

    private async Task EnsureGeneratedConfigAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var configPath = LauncherWorkspacePathHelper.ProfileConfigPath(profile);
        if (File.Exists(configPath))
        {
            return;
        }

        var lockKey = string.IsNullOrWhiteSpace(profile.DirectoryPath) ? profile.Id : profile.DirectoryPath;
        var gate = EnsureConfigLocks.GetOrAdd(lockKey, _ => new SemaphoreSlim(1, 1));

        await gate.WaitAsync(cancellationToken);
        try
        {
            if (File.Exists(configPath))
            {
                return;
            }

            await Task.Run(() =>
            {
                try
                {
                    var preferences = preferencesService.Load();
                    var installPath = LauncherWorkspacePathHelper.ServerInstallPath(preferences, profile.Version);
                    if (File.Exists(Path.Combine(installPath, "VintagestoryServer.exe")))
                    {
                        ServerConfigBootstrapper.EnsureGenerated(installPath, profile);
                    }
                }
                catch
                {
                    // 配置页仍然可以用内置默认值打开。
                }
            }, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    private static async Task<JsonObject?> TryParseRootAsync(string configPath, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = new FileStream(configPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
            return node as JsonObject;
        }
        catch
        {
            return null;
        }
    }

    private static async Task SaveRootAsync(InstanceProfile profile, JsonObject root, CancellationToken cancellationToken)
    {
        var configPath = LauncherWorkspacePathHelper.ProfileConfigPath(profile);
        await ServerConfigFileIO.WriteAllTextAtomicAsync(
            configPath,
            root.ToJsonString(JsonWriteOptions),
            cancellationToken);
    }

    private static JsonObject BuildDefaultRoot(InstanceProfile profile)
    {
        var root = new JsonObject
        {
            ["ServerName"] = "Vintage Story Server",
            ["ServerDescription"] = null,
            ["ServerUrl"] = null,
            ["Ip"] = null,
            ["Port"] = 42420,
            ["MaxClients"] = 16,
            ["MaxClientsInQueue"] = 0,
            ["Password"] = null,
            ["AdvertiseServer"] = false,
            ["WhitelistMode"] = 0,
            ["Upnp"] = false,
            ["AllowPvP"] = true,
            ["AllowFireSpread"] = true,
            ["AllowFallingBlocks"] = true,
            ["PassTimeWhenEmpty"] = false,
            ["WarnClientsAfterAfkSeconds"] = 0,
            ["KickClientsAfterAfkSeconds"] = 0,
            ["ClientConnectionTimeout"] = 150,
            ["MaxChunkRadius"] = 12,
            ["DieBelowDiskSpaceMb"] = 400,
            ["CorruptionProtection"] = true,
            ["RegenerateCorruptChunks"] = false,
            ["StartupCommands"] = string.Empty,
            ["VerifyPlayerAuth"] = true,
            ["ServerLanguage"] = ResolveDefaultServerLanguage(),
            ["DefaultRoleCode"] = "suplayer",
            ["WelcomeMessage"] = string.Empty,
            ["ModPaths"] = BuildDefaultModPaths(profile)
        };

        var worldConfig = new JsonObject
        {
            ["Seed"] = "123456789",
            ["WorldName"] = "A new world",
            ["SaveFileLocation"] = ResolveCurrentSaveFilePath(profile),
            ["PlayStyle"] = "surviveandbuild",
            ["WorldType"] = "standard",
            ["MapSizeY"] = 256,
            ["WorldConfiguration"] = new JsonObject
            {
                ["gameMode"] = "survival",
                ["allowMap"] = true,
                ["allowCoordinateHud"] = true,
                ["colorAccurateWorldmap"] = false,
                ["allowLandClaiming"] = true,
                ["worldWidth"] = 1024000,
                ["worldLength"] = 1024000,
                ["worldEdge"] = "blocked",
                ["snowAccum"] = true
            }
        };

        root["WorldConfig"] = worldConfig;
        return root;
    }

    private static void NormalizeImportedConfigPaths(InstanceProfile profile, JsonObject root)
    {
        root["ModPaths"] = BuildDefaultModPaths(profile);
        var worldConfig = GetOrCreateObject(root, "WorldConfig");
        worldConfig["SaveFileLocation"] = ResolveCurrentSaveFilePath(profile);
    }

    private static JsonArray BuildDefaultModPaths(InstanceProfile profile)
    {
        var modPaths = new JsonArray { "Mods" };
        var profileModsPath = LauncherWorkspacePathHelper.NormalizePath(Path.Combine(profile.DirectoryPath, "Mods"));
        if (!string.IsNullOrWhiteSpace(profileModsPath))
        {
            modPaths.Add(profileModsPath);
        }

        return modPaths;
    }

    private static string ResolveCurrentSaveFilePath(InstanceProfile profile)
    {
        var activeSaveFile = LauncherWorkspacePathHelper.NormalizePath(profile.ActiveSaveFile);
        var saveRoot = LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory);
        if (!string.IsNullOrWhiteSpace(activeSaveFile) &&
            LauncherWorkspacePathHelper.IsSameOrChildPath(activeSaveFile, saveRoot))
        {
            return activeSaveFile;
        }

        if (!string.IsNullOrWhiteSpace(saveRoot))
        {
            return Path.Combine(saveRoot, "default.vcdbs");
        }

        return Path.Combine(profile.DirectoryPath, "Saves", "default.vcdbs");
    }

    private static JsonObject GetOrCreateObject(JsonObject root, string propertyName)
    {
        if (root[propertyName] is JsonObject obj)
        {
            return obj;
        }

        var created = new JsonObject();
        root[propertyName] = created;
        return created;
    }

    private static string ReadString(JsonNode? node, string defaultValue)
    {
        return node?.GetValue<string>() ?? defaultValue;
    }

    private static string? ReadNullableString(JsonNode? node)
    {
        return node is null ? null : node.GetValue<string?>();
    }

    private static int ReadInt(JsonNode? node, int defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var value))
        {
            return value;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value))
        {
            return value;
        }

        return defaultValue;
    }

    private static int? ReadNullableInt(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        if (node.GetValueKind() == JsonValueKind.Number &&
            node is JsonValue numericValue &&
            numericValue.TryGetValue<int>(out var numeric))
        {
            return numeric;
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            int.TryParse(node.GetValue<string>(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numeric))
        {
            return numeric;
        }

        return null;
    }

    private static bool ReadBool(JsonNode? node, bool defaultValue)
    {
        if (node is null)
        {
            return defaultValue;
        }

        if (node.GetValueKind() == JsonValueKind.True || node.GetValueKind() == JsonValueKind.False)
        {
            return node.GetValue<bool>();
        }

        if (node.GetValueKind() == JsonValueKind.String &&
            bool.TryParse(node.GetValue<string>(), out var parsed))
        {
            return parsed;
        }

        return defaultValue;
    }

    private static string? ReadFlexibleString(JsonNode? node)
    {
        if (node is null)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>(),
            JsonValueKind.True => bool.TrueString.ToLowerInvariant(),
            JsonValueKind.False => bool.FalseString.ToLowerInvariant(),
            JsonValueKind.Number => node.ToString(),
            _ => node.ToJsonString()
        };
    }

    private static string? ReadRuleFallbackValue(string key, JsonObject root, JsonObject worldConfig)
    {
        return key switch
        {
            "worldWidth" => ReadFlexibleString(root["MapSizeX"]) ?? ReadFlexibleString(worldConfig["MapSizeX"]),
            "worldLength" => ReadFlexibleString(root["MapSizeZ"]) ?? ReadFlexibleString(worldConfig["MapSizeZ"]),
            "colorAccurateWorldmap" => ReadFlexibleString(worldConfig["colorAccurateWorldmap"]),
            _ => null
        };
    }

    private static string ResolveDefaultServerLanguage()
    {
        return CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "zh-cn" : "en";
    }
}
