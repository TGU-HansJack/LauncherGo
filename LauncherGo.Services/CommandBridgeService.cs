using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;

namespace LauncherGo.Services;

/// <summary>
///     Local-only transport for the in-server command bridge. The protocol deliberately has no remote endpoint.
/// </summary>
public sealed class CommandBridgeService : ICommandBridgeService
{
    private const string ModId = "launchergocommandbridge";
    private const string ModVersion = "1.0.1";
    private const string ModFolderName = "launchergocommandbridge";
    private const string ModDllName = "commandbridge.dll";
    private const string SettingsRelativePath = "ModConfig/launchergocommandbridge.json";
    private const int DefaultPort = 19090;
    private const int MinimumPort = 1024;
    private const int MaximumPort = 65535;
    private const int MinimumTimeoutMilliseconds = 500;
    private const int MaximumTimeoutMilliseconds = 30000;
    private const int MinimumCommandLength = 256;
    private const int MaximumCommandLength = 16384;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    // TCP uses one JSON document per line; persisted configuration remains indented for operators.
    private static readonly JsonSerializerOptions WireJsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IInstanceServerConfigService _serverConfigService;

    public CommandBridgeService(IInstanceServerConfigService serverConfigService)
    {
        _serverConfigService = serverConfigService;
    }

    public async Task<CommandBridgeSettings> LoadSettingsAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        if (!File.Exists(path))
        {
            var defaults = NormalizeSettings(profile, new CommandBridgeSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }

        try
        {
            var json = await File.ReadAllTextAsync(path, cancellationToken);
            var settings = JsonSerializer.Deserialize<CommandBridgeSettings>(json, JsonOptions)
                           ?? new CommandBridgeSettings();
            var normalized = NormalizeSettings(profile, settings);
            if (!string.Equals(settings.AccessToken, normalized.AccessToken, StringComparison.Ordinal) ||
                settings.Port != normalized.Port ||
                settings.CommandTimeoutMilliseconds != normalized.CommandTimeoutMilliseconds ||
                settings.MaxCommandLength != normalized.MaxCommandLength)
            {
                await SaveSettingsAsync(profile, normalized, cancellationToken);
            }

            return normalized;
        }
        catch
        {
            var defaults = NormalizeSettings(profile, new CommandBridgeSettings());
            await SaveSettingsAsync(profile, defaults, cancellationToken);
            return defaults;
        }
    }

    public async Task SaveSettingsAsync(
        InstanceProfile profile,
        CommandBridgeSettings settings,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetSettingsPath(profile);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(NormalizeSettings(profile, settings), JsonOptions);
        await File.WriteAllTextAsync(path, json, cancellationToken);
    }

    public async Task EnsureCommandBridgeModDeployedAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default,
        bool enableMod = true)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var modsPath = WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath);
        Directory.CreateDirectory(modsPath);
        var destination = Path.Combine(modsPath, ModFolderName);
        SyncDirectory(ResolveEmbeddedSourceRoot(), destination);
        await SetCommandBridgeModEnabledAsync(profile, enableMod, cancellationToken);
    }

    public async Task<bool> GetCommandBridgeModEnabledAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Directory.Exists(Path.Combine(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath), ModFolderName)))
            return false;

        return !await IsModDisabledAsync(profile, cancellationToken);
    }

    public async Task SetCommandBridgeModEnabledAsync(
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
            .Where(static item => !IsModDisabledKey(item))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!enabled)
            remain.Add($"{ModId}@{ModVersion}");

        disabledMods.Clear();
        foreach (var item in remain.Distinct(StringComparer.OrdinalIgnoreCase))
            disabledMods.Add(item);

        await _serverConfigService.SaveRawJsonAsync(profile, root.ToJsonString(JsonOptions), cancellationToken);
    }

    public async Task<CommandBridgeRuntimeStatus> GetRuntimeStatusAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
        {
            return new CommandBridgeRuntimeStatus
            {
                State = CommandBridgeRuntimeState.Disabled,
                Message = "命令桥接未启用。",
                Port = settings.Port
            };
        }

        if (!await GetCommandBridgeModEnabledAsync(profile, cancellationToken))
        {
            return new CommandBridgeRuntimeStatus
            {
                State = CommandBridgeRuntimeState.NotDeployed,
                Message = "命令桥接模组未部署或已禁用。",
                Port = settings.Port
            };
        }

        try
        {
            var response = await SendRequestAsync(settings, new CommandBridgeRequest
            {
                Type = "ping",
                Token = settings.AccessToken
            }, cancellationToken);
            return response.Success
                ? new CommandBridgeRuntimeStatus
                {
                    State = CommandBridgeRuntimeState.Ready,
                    Message = "命令桥接已就绪。",
                    Port = settings.Port,
                    Version = response.BridgeVersion ?? string.Empty
                }
                : new CommandBridgeRuntimeStatus
                {
                    State = CommandBridgeRuntimeState.Unavailable,
                    Message = response.Error ?? "命令桥接拒绝了连接。",
                    Port = settings.Port
                };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is IOException or SocketException or TimeoutException or OperationCanceledException)
        {
            return new CommandBridgeRuntimeStatus
            {
                State = CommandBridgeRuntimeState.Unavailable,
                Message = "命令桥接当前不可达：" + ex.Message,
                Port = settings.Port
            };
        }
    }

    public async Task SendCommandAsync(
        InstanceProfile profile,
        string command,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
            throw new InvalidOperationException("命令桥接未启用。");

        var normalized = NormalizeCommand(command, settings.MaxCommandLength);
        var response = await SendRequestAsync(settings, new CommandBridgeRequest
        {
            Type = "command",
            Token = settings.AccessToken,
            Id = Guid.NewGuid().ToString("N"),
            Command = normalized
        }, cancellationToken);
        if (!response.Success)
            throw new InvalidOperationException(response.Error ?? "命令桥接未接受命令。");
    }

    public async Task RotateAccessTokenAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        var settings = await LoadSettingsAsync(profile, cancellationToken);
        if (!settings.Enabled)
            throw new InvalidOperationException("命令桥接未启用，无法热轮换访问令牌。");

        var replacementToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var replacementSettings = new CommandBridgeSettings
        {
            Enabled = settings.Enabled,
            Port = settings.Port,
            AccessToken = replacementToken,
            CommandTimeoutMilliseconds = settings.CommandTimeoutMilliseconds,
            MaxCommandLength = settings.MaxCommandLength,
            AllowRelayFallback = settings.AllowRelayFallback
        };

        var response = await SendRequestAsync(settings, new CommandBridgeRequest
        {
            Type = "rotate-token",
            Token = settings.AccessToken,
            NewToken = replacementToken
        }, cancellationToken);
        if (!response.Success)
        {
            if (response.Error?.Contains("Unsupported command bridge request", StringComparison.OrdinalIgnoreCase) == true)
            {
                throw new InvalidOperationException(
                    "当前运行中的命令桥接不支持令牌热轮换。请部署新版命令桥接并重启服务端一次；之后轮换无需重启。");
            }

            throw new InvalidOperationException(
                response.Error ?? "运行中的命令桥接未接受访问令牌热轮换。未修改本地令牌。");
        }

        try
        {
            await SaveSettingsAsync(profile, replacementSettings, cancellationToken);
        }
        catch (Exception saveException)
        {
            try
            {
                await SendRequestAsync(replacementSettings, new CommandBridgeRequest
                {
                    Type = "rotate-token",
                    Token = replacementToken,
                    NewToken = settings.AccessToken
                }, CancellationToken.None);
            }
            catch
            {
                // The primary failure below explains that manual recovery may be required.
            }

            throw new InvalidOperationException(
                "访问令牌热轮换后无法保存本地配置；已尝试恢复服务端令牌。请测试连接后再继续操作。",
                saveException);
        }
    }

    private static async Task<CommandBridgeResponse> SendRequestAsync(
        CommandBridgeSettings settings,
        CommandBridgeRequest request,
        CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromMilliseconds(settings.CommandTimeoutMilliseconds));
        using var client = new TcpClient(AddressFamily.InterNetwork);
        await client.ConnectAsync(IPAddress.Loopback, settings.Port, timeoutCts.Token);
        await using var stream = client.GetStream();
        using var writer = new StreamWriter(stream, new UTF8Encoding(false), leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
        await writer.WriteLineAsync(JsonSerializer.Serialize(request, WireJsonOptions).AsMemory(), timeoutCts.Token);
        var responseJson = await reader.ReadLineAsync(timeoutCts.Token);
        if (string.IsNullOrWhiteSpace(responseJson))
            throw new IOException("命令桥接未返回响应。");
        return JsonSerializer.Deserialize<CommandBridgeResponse>(responseJson, WireJsonOptions)
               ?? throw new IOException("命令桥接返回了无效响应。");
    }

    private static CommandBridgeSettings NormalizeSettings(InstanceProfile profile, CommandBridgeSettings settings)
    {
        return new CommandBridgeSettings
        {
            Enabled = settings.Enabled,
            Port = settings.Port is >= MinimumPort and <= MaximumPort
                ? settings.Port
                : GetDefaultPort(profile.Id),
            AccessToken = IsValidToken(settings.AccessToken)
                ? settings.AccessToken.Trim().ToLowerInvariant()
                : Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant(),
            CommandTimeoutMilliseconds = Math.Clamp(
                settings.CommandTimeoutMilliseconds <= 0 ? 5000 : settings.CommandTimeoutMilliseconds,
                MinimumTimeoutMilliseconds,
                MaximumTimeoutMilliseconds),
            MaxCommandLength = Math.Clamp(
                settings.MaxCommandLength <= 0 ? 4096 : settings.MaxCommandLength,
                MinimumCommandLength,
                MaximumCommandLength),
            AllowRelayFallback = settings.AllowRelayFallback
        };
    }

    private static bool IsValidToken(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().Length != 64)
            return false;
        return value.Trim().All(Uri.IsHexDigit);
    }

    private static int GetDefaultPort(string profileId)
    {
        var source = string.IsNullOrWhiteSpace(profileId) ? Guid.NewGuid().ToString("N") : profileId.Trim();
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(source));
        var offset = (hash[0] << 8 | hash[1]) % 20000;
        return Math.Clamp(DefaultPort + offset, MinimumPort, MaximumPort);
    }

    private static string NormalizeCommand(string? command, int maxLength)
    {
        var normalized = command?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("命令不能为空。");
        if (normalized.Length > maxLength)
            throw new InvalidOperationException($"命令长度不能超过 {maxLength} 个字符。");
        return normalized.StartsWith('/') ? normalized : "/" + normalized;
    }

    private static void SyncDirectory(string sourcePath, string destinationPath)
    {
        Directory.CreateDirectory(destinationPath);
        foreach (var directory in Directory.EnumerateDirectories(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, directory);
            Directory.CreateDirectory(Path.Combine(destinationPath, relative));
        }

        foreach (var sourceFile in Directory.EnumerateFiles(sourcePath, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(sourcePath, sourceFile);
            var target = Path.Combine(destinationPath, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            try
            {
                File.Copy(sourceFile, target, true);
            }
            catch (UnauthorizedAccessException) when (IsBridgeDll(target))
            {
                // The running server holds the loaded bridge dll. Its replacement is applied on next start.
            }
            catch (IOException) when (IsBridgeDll(target))
            {
                // The running server holds the loaded bridge dll. Its replacement is applied on next start.
            }
        }
    }

    private static string ResolveEmbeddedSourceRoot()
    {
        var primary = Path.Combine(AppContext.BaseDirectory, "EmbeddedMods", ModFolderName);
        if (Directory.Exists(primary))
            return primary;

        var fallback = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..",
            "..",
            "..",
            "..",
            "LauncherGo.Services",
            "EmbeddedMods",
            ModFolderName));
        if (Directory.Exists(fallback))
            return fallback;

        throw new InvalidOperationException($"未找到内置命令桥接模组文件：{primary}；{fallback}");
    }

    private async Task<bool> IsModDisabledAsync(InstanceProfile profile, CancellationToken cancellationToken)
    {
        try
        {
            var rawJson = await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken);
            var root = JsonNode.Parse(rawJson) as JsonObject;
            if (root?["WorldConfig"] is not JsonObject worldConfig ||
                worldConfig["DisabledMods"] is not JsonArray disabledMods)
            {
                return false;
            }

            return disabledMods
                .Where(static item => item is not null)
                .Select(static item => item!.GetValue<string>())
                .Any(static item => IsModDisabledKey(item));
        }
        catch
        {
            return false;
        }
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

    private static bool IsModDisabledKey(string item) =>
        item.Equals(ModId, StringComparison.OrdinalIgnoreCase) ||
        item.StartsWith(ModId + "@", StringComparison.OrdinalIgnoreCase);

    private static bool IsBridgeDll(string path) =>
        Path.GetFileName(path).Equals(ModDllName, StringComparison.OrdinalIgnoreCase);

    private static string GetSettingsPath(InstanceProfile profile) =>
        Path.Combine(profile.DirectoryPath, SettingsRelativePath);

    private sealed class CommandBridgeRequest
    {
        public string Type { get; init; } = string.Empty;
        public string Token { get; init; } = string.Empty;
        public string NewToken { get; init; } = string.Empty;
        public string Id { get; init; } = string.Empty;
        public string Command { get; init; } = string.Empty;
    }

    private sealed class CommandBridgeResponse
    {
        public bool Success { get; init; }
        public string? Error { get; init; }
        public string? BridgeVersion { get; init; }
    }
}
