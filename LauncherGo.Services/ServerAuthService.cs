using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     ServerAuth 服务默认实现
/// </summary>
public sealed class ServerAuthService : IServerAuthService
{
    private const string AuthModId = "serverauth";
    private const string AuthModVersion = "1.0.0";
    private const string AuthModFolderName = "serverauth";
    private const string AuthModZipName = "serverauth.zip";
    private const string AuthModDllName = "serverauth.dll";
    private const string SettingsRelativePath = "ModConfig/serverauth.json";
    private const string StoreRelativePath = "ServerAuth/players.json";

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly JsonSerializerOptions JsonWriteOptions = new()
    {
        WriteIndented = true
    };

    private readonly IInstanceServerConfigService _serverConfigService;

    public ServerAuthService(IInstanceServerConfigService serverConfigService)
    {
        _serverConfigService = serverConfigService;
    }

    /// <inheritdoc />
    public async Task<ServerAuthSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var settingsPath = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);

        if (!File.Exists(settingsPath))
        {
            var defaults = NormalizeSettings(new ServerAuthSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(settingsPath, cancellationToken);
            var parsed = JsonSerializer.Deserialize<ServerAuthSettings>(json, JsonReadOptions) ?? new ServerAuthSettings();
            return NormalizeSettings(parsed);
        }
        catch
        {
            var defaults = NormalizeSettings(new ServerAuthSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }
    }

    /// <inheritdoc />
    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        ServerAuthSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = NormalizeSettings(settings);
        var settingsPath = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
        var json = JsonSerializer.Serialize(normalized, JsonWriteOptions);
        await File.WriteAllTextAsync(settingsPath, json, cancellationToken);
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<ServerAuthPlayerSummary>> GetPlayersAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var store = LoadStore(profile);
        var players = store.Players
            .Select(static player => new ServerAuthPlayerSummary
            {
                PlayerUid = player.PlayerUid,
                PlayerName = player.PlayerName,
                NormalizedPlayerName = player.NormalizedPlayerName,
                RegisteredIp = player.RegisteredIp,
                RegisteredAtUtc = player.RegisteredAtUtc,
                LastIp = player.LastIp,
                LastLoginAtUtc = player.LastLoginAtUtc,
                PasswordResetRequired = player.PasswordResetRequired,
                HasPassword = !string.IsNullOrWhiteSpace(player.PasswordHash),
                DiscourseExternalId = player.DiscourseExternalId,
                DiscourseUsername = player.DiscourseUsername,
                DiscourseEmail = player.DiscourseEmail
            })
            .OrderBy(static player => player.PlayerName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static player => player.PlayerUid, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return Task.FromResult<IReadOnlyList<ServerAuthPlayerSummary>>(players);
    }

    /// <inheritdoc />
    public Task<bool> ClearPasswordAsync(
        InstanceProfile profile,
        string playerUidOrName,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(playerUidOrName))
            return Task.FromResult(false);

        var store = LoadStore(profile);
        var player = FindPlayer(store, playerUidOrName);
        if (player is null)
            return Task.FromResult(false);

        player.PasswordHash = string.Empty;
        player.PasswordResetRequired = true;
        store.Sessions = store.Sessions
            .Where(session => !session.PlayerUid.Equals(player.PlayerUid, StringComparison.OrdinalIgnoreCase))
            .ToList();
        SaveStore(profile, store);
        return Task.FromResult(true);
    }

    /// <inheritdoc />
    public async Task EnsureAuthModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);

        var zipPath = Path.Combine(modsPath, AuthModZipName);
        TryDeleteFile(zipPath);

        var destination = Path.Combine(modsPath, AuthModFolderName);
        var sourceRoot = ResolveEmbeddedAuthSourceRoot();

        SyncDirectory(sourceRoot, destination);
        await RemoveAuthModFromDisabledListAsync(profile, cancellationToken);
    }

    /// <inheritdoc />
    public Task<bool> GetAuthModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        var folderPath = Path.Combine(modsPath, AuthModFolderName);
        var zipPath = Path.Combine(modsPath, AuthModZipName);
        var enabled = Directory.Exists(folderPath) || File.Exists(zipPath);
        return Task.FromResult(enabled);
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
                continue;

            TryDeleteFile(targetFile);
        }

        var directories = Directory.EnumerateDirectories(destinationPath, "*", SearchOption.AllDirectories)
            .OrderByDescending(static path => path.Length)
            .ToList();
        foreach (var directory in directories)
        {
            if (Directory.EnumerateFileSystemEntries(directory).Any())
                continue;
            TryDeleteDirectory(directory);
        }
    }

    private static void TryCopyFile(string sourcePath, string targetPath)
    {
        try
        {
            File.Copy(sourcePath, targetPath, true);
        }
        catch (UnauthorizedAccessException) when (ShouldSkipLockedTarget(targetPath))
        {
            // 运行中的服务器会锁定 dll，此时保持现有文件并继续。
        }
        catch (IOException) when (ShouldSkipLockedTarget(targetPath))
        {
            // 运行中的服务器会锁定 dll，此时保持现有文件并继续。
        }
    }

    private static void TryDeleteFile(string path)
    {
        if (!File.Exists(path))
            return;

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
            return;

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
        if (!File.Exists(targetPath))
            return false;

        var fileName = Path.GetFileName(targetPath);
        return fileName.Equals(AuthModDllName, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeRelativePath(string relativePath)
    {
        return relativePath
            .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar)
            .TrimStart(Path.DirectorySeparatorChar);
    }

    private static string ResolveEmbeddedAuthSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", AuthModFolderName);
        if (Directory.Exists(primary))
            return primary;

        var fallback = Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "LauncherGo.Services",
                "EmbeddedMods",
                AuthModFolderName));
        if (Directory.Exists(fallback))
            return fallback;

        throw new InvalidOperationException(
            $"未找到内置认证模组文件，请先重新构建启动器。查找路径：{primary}；{fallback}");
    }

    private async Task RemoveAuthModFromDisabledListAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            if (JsonNode.Parse(rawJson) is not JsonObject root)
                return;
            if (root["WorldConfig"] is not JsonObject worldConfig)
                return;
            if (worldConfig["DisabledMods"] is not JsonArray disabledMods)
                return;

            var beforeCount = disabledMods.Count;
            var cleanupSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                AuthModId,
                $"{AuthModId}@{AuthModVersion}"
            };

            var remain = disabledMods
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .Where(item => !cleanupSet.Contains(item))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (remain.Count == beforeCount)
                return;

            disabledMods.Clear();
            foreach (var item in remain)
                disabledMods.Add(item);

            await _serverConfigService.SaveRawJsonAsync(
                profile,
                root.ToJsonString(JsonWriteOptions),
                cancellationToken);
        }
        catch
        {
            // 清理禁用项失败不阻断主流程
        }
    }

    private static ServerAuthSettings NormalizeSettings(ServerAuthSettings settings)
    {
        var discourse = settings.Discourse ?? new ServerAuthDiscourseSettings();
        return new ServerAuthSettings
        {
            Enabled = settings.Enabled,
            LoginTimeoutSeconds = Math.Clamp(settings.LoginTimeoutSeconds <= 0 ? 60 : settings.LoginTimeoutSeconds, 10, 600),
            RememberSessionMinutes = Math.Clamp(settings.RememberSessionMinutes < 0 ? 30 : settings.RememberSessionMinutes, 0, 1440),
            Discourse = new ServerAuthDiscourseSettings
            {
                Enabled = discourse.Enabled,
                BaseUrl = NormalizeUrl(discourse.BaseUrl),
                SharedSecret = discourse.SharedSecret?.Trim() ?? string.Empty,
                PublicCallbackBaseUrl = NormalizeUrl(discourse.PublicCallbackBaseUrl, "http://127.0.0.1:18092/"),
                ListenPrefix = NormalizeUrl(discourse.ListenPrefix, "http://127.0.0.1:18092/")
            }
        };
    }

    private static string NormalizeUrl(string? value, string fallback = "")
    {
        var candidate = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(candidate))
            return fallback;

        return candidate.EndsWith('/') ? candidate : candidate + "/";
    }

    private static string GetSettingsPath(InstanceProfile profile)
    {
        return Path.Combine(profile.DirectoryPath, SettingsRelativePath);
    }

    private static string GetStorePath(InstanceProfile profile)
    {
        return Path.Combine(profile.DirectoryPath, StoreRelativePath);
    }

    private static PlayerStore LoadStore(InstanceProfile profile)
    {
        var storePath = GetStorePath(profile);
        if (!File.Exists(storePath))
            return new PlayerStore();

        try
        {
            var json = File.ReadAllText(storePath);
            return JsonSerializer.Deserialize<PlayerStore>(json, JsonReadOptions) ?? new PlayerStore();
        }
        catch
        {
            return new PlayerStore();
        }
    }

    private static void SaveStore(InstanceProfile profile, PlayerStore store)
    {
        var storePath = GetStorePath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(storePath)!);
        var tempPath = storePath + ".tmp";
        var json = JsonSerializer.Serialize(store, JsonWriteOptions);
        File.WriteAllText(tempPath, json);
        File.Move(tempPath, storePath, true);
    }

    private static PlayerRecord? FindPlayer(PlayerStore store, string playerUidOrName)
    {
        var normalized = NormalizePlayerName(playerUidOrName);
        return store.Players.FirstOrDefault(player =>
            player.PlayerUid.Equals(playerUidOrName, StringComparison.OrdinalIgnoreCase) ||
            player.PlayerName.Equals(playerUidOrName, StringComparison.OrdinalIgnoreCase) ||
            player.NormalizedPlayerName.Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizePlayerName(string? value)
    {
        return value?.Trim().ToLowerInvariant() ?? string.Empty;
    }

    private sealed class PlayerStore
    {
        public List<PlayerRecord> Players { get; set; } = [];
        public List<SessionRecord> Sessions { get; set; } = [];
    }

    private sealed class PlayerRecord
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string PlayerName { get; set; } = string.Empty;
        public string NormalizedPlayerName { get; set; } = string.Empty;
        public string RegisteredIp { get; set; } = string.Empty;
        public DateTimeOffset RegisteredAtUtc { get; set; }
        public string LastIp { get; set; } = string.Empty;
        public DateTimeOffset? LastLoginAtUtc { get; set; }
        public string PasswordHash { get; set; } = string.Empty;
        public bool PasswordResetRequired { get; set; }
        public string DiscourseExternalId { get; set; } = string.Empty;
        public string DiscourseUsername { get; set; } = string.Empty;
        public string DiscourseEmail { get; set; } = string.Empty;
    }

    private sealed class SessionRecord
    {
        public string PlayerUid { get; set; } = string.Empty;
        public string IpAddress { get; set; } = string.Empty;
        public DateTimeOffset CreatedAtUtc { get; set; }
        public DateTimeOffset ExpiresAtUtc { get; set; }
    }
}

