using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class LauncherPreferencesService : ILauncherPreferencesService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    public LauncherPreferences Load()
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        if (!File.Exists(LauncherPathHelper.PreferencesFilePath))
        {
            var defaults = Normalize(LauncherPathHelper.BuildDefaults());
            WorkspacePathHelper.SetWorkspaceRoot(defaults.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(defaults);
            return defaults;
        }

        try
        {
            var rawJson = File.ReadAllText(LauncherPathHelper.PreferencesFilePath);
            var parsed = JsonSerializer.Deserialize<LauncherPreferences>(rawJson, JsonOptions) ?? LauncherPathHelper.BuildDefaults();
            var normalized = Normalize(parsed);
            WorkspacePathHelper.SetWorkspaceRoot(normalized.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(normalized);
            return normalized;
        }
        catch
        {
            var fallback = Normalize(LauncherPathHelper.BuildDefaults());
            WorkspacePathHelper.SetWorkspaceRoot(fallback.WorkspaceRoot);
            LauncherPathHelper.EnsureBaseDirectories(fallback);
            return fallback;
        }
    }

    public void Save(LauncherPreferences preferences)
    {
        Directory.CreateDirectory(LauncherPathHelper.AppRoot);

        var normalized = Normalize(preferences);
        WorkspacePathHelper.SetWorkspaceRoot(normalized.WorkspaceRoot);
        LauncherPathHelper.EnsureBaseDirectories(normalized);

        var json = JsonSerializer.Serialize(normalized, JsonOptions);
        File.WriteAllText(LauncherPathHelper.PreferencesFilePath, json);
    }

    private static LauncherPreferences Normalize(LauncherPreferences source)
    {
        var defaults = LauncherPathHelper.BuildDefaults();
        var workspaceRoot = LauncherPathHelper.GetWorkspaceRootOrDefault(source.WorkspaceRoot);
        var serverDirectory = LauncherPathHelper.GetServerDirectory(workspaceRoot);
        var profileDirectory = LauncherPathHelper.GetProfileDirectory(workspaceRoot);
        var saveDirectory = LauncherPathHelper.GetSaveDirectory(workspaceRoot);
        var qqBotDirectory = LauncherPathHelper.GetQqBotDirectory(workspaceRoot);

        return new LauncherPreferences
        {
            IsOnboardingCompleted = source.IsOnboardingCompleted,
            Language = string.IsNullOrWhiteSpace(source.Language)
                ? CultureInfo.CurrentUICulture.Name
                : source.Language.Trim(),
            ThemeMode = Enum.IsDefined(source.ThemeMode) ? source.ThemeMode : ThemeMode.System,
            WorkspaceRoot = workspaceRoot,
            ServerDirectory = serverDirectory,
            ProfileDirectory = profileDirectory,
            SaveDirectory = saveDirectory,
            QqBotDirectory = qqBotDirectory,
            ServerDownloadCatalogUrl = NormalizeHttpUrlOrDefault(source.ServerDownloadCatalogUrl, defaults.ServerDownloadCatalogUrl),
            EnableChunkedDownloads = source.EnableChunkedDownloads,
            DownloadChunkCount = Math.Clamp(source.DownloadChunkCount <= 0 ? defaults.DownloadChunkCount : source.DownloadChunkCount, 1, 32),
            DefaultLaunchProfileId = string.IsNullOrWhiteSpace(source.DefaultLaunchProfileId)
                ? string.Empty
                : source.DefaultLaunchProfileId.Trim(),
            DefaultLaunchSaveFile = NormalizeFilePathOrEmpty(source.DefaultLaunchSaveFile),
            QuickCommands = NormalizeQuickCommands(source.QuickCommands),
            StartWithWindows = source.StartWithWindows,
            CloseToTrayOnExit = source.CloseToTrayOnExit,
            StartHiddenOnLaunch = source.StartHiddenOnLaunch,
            AutoStartServerOnLaunch = source.AutoStartServerOnLaunch,
            AutoStartServerProfileId = string.IsNullOrWhiteSpace(source.AutoStartServerProfileId)
                ? string.Empty
                : source.AutoStartServerProfileId.Trim(),
            AutoStartOpenServerQueryOnLaunch = source.AutoStartOpenServerQueryOnLaunch,
            AutoStartRobotOnLaunch = source.AutoStartRobotOnLaunch,
            AutoStartFrpOnLaunch = source.AutoStartFrpOnLaunch,
            AutoStartThirdPartyFrpcOnLaunch = source.AutoStartThirdPartyFrpcOnLaunch,
            OpenServerQuery = NormalizeOpenServerQuery(source.OpenServerQuery),
            Robot = NormalizeRobot(source.Robot, qqBotDirectory),
            Frp = NormalizeFrp(source.Frp)
        };
    }

    private static List<string> NormalizeQuickCommands(IEnumerable<string>? source)
    {
        var commands = new List<string>();
        foreach (var command in source ?? [])
        {
            var normalized = command?.Trim();
            if (string.IsNullOrWhiteSpace(normalized))
            {
                continue;
            }

            commands.Add(normalized);
        }

        return commands;
    }

    private static OpenServerQuerySettings NormalizeOpenServerQuery(OpenServerQuerySettings? source)
    {
        source ??= new OpenServerQuerySettings();
        var endpoints = NormalizeOpenServerQueryEndpoints(source.Endpoints);
        if (endpoints.Count == 0 &&
            (!string.IsNullOrWhiteSpace(source.EndpointHost) || !string.IsNullOrWhiteSpace(source.EndpointToken)))
        {
            endpoints.Add(new OpenServerQueryEndpointConfig
            {
                ServerHost = source.EndpointHost.Trim(),
                Token = source.EndpointToken.Trim(),
                Enabled = true
            });
        }

        var firstEndpoint = endpoints.FirstOrDefault();
        return new OpenServerQuerySettings
        {
            Enabled = source.Enabled,
            ListenPrefix = NormalizeListenPrefix(source.ListenPrefix),
            AllowInsecureHttp = source.AllowInsecureHttp,
            RequestTimeoutSec = Math.Clamp(source.RequestTimeoutSec, 3, 60),
            IncludeServerInfo = source.IncludeServerInfo,
            IncludePlayers = source.IncludePlayers,
            IncludePlayerEvents = source.IncludePlayerEvents,
            IncludeChats = source.IncludeChats,
            IncludeNotifications = source.IncludeNotifications,
            IncludeMapData = source.IncludeMapData,
            Endpoints = endpoints,
            EndpointHost = firstEndpoint?.ServerHost ?? string.Empty,
            EndpointToken = firstEndpoint?.Token ?? string.Empty
        };
    }

    private static List<OpenServerQueryEndpointConfig> NormalizeOpenServerQueryEndpoints(IEnumerable<OpenServerQueryEndpointConfig>? endpoints)
    {
        var result = new List<OpenServerQueryEndpointConfig>();
        foreach (var endpoint in endpoints ?? [])
        {
            var host = endpoint.ServerHost?.Trim() ?? string.Empty;
            var token = endpoint.Token?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(host) && string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            result.Add(new OpenServerQueryEndpointConfig
            {
                ServerHost = host,
                Token = token,
                Enabled = endpoint.Enabled
            });
        }

        return result;
    }

    private static RobotIntegrationSettings NormalizeRobot(RobotIntegrationSettings? source, string defaultQqBotDirectory)
    {
        source ??= new RobotIntegrationSettings();

        var dbPath = string.IsNullOrWhiteSpace(source.DatabasePath)
            ? Path.Combine(defaultQqBotDirectory, "vs2qq.db")
            : NormalizeFilePathOrEmpty(source.DatabasePath);
        if (string.IsNullOrWhiteSpace(dbPath))
        {
            dbPath = Path.Combine(defaultQqBotDirectory, "vs2qq.db");
        }

        var dbDirectory = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrWhiteSpace(dbDirectory))
        {
            Directory.CreateDirectory(dbDirectory);
        }

        var wsUrl = string.IsNullOrWhiteSpace(source.OneBotWsUrl)
            ? "ws://127.0.0.1:3001/"
            : source.OneBotWsUrl.Trim();
        if (!wsUrl.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) &&
            !wsUrl.StartsWith("wss://", StringComparison.OrdinalIgnoreCase))
        {
            wsUrl = "ws://127.0.0.1:3001/";
        }

        return new RobotIntegrationSettings
        {
            OneBotWsUrl = wsUrl,
            AccessToken = source.AccessToken?.Trim() ?? string.Empty,
            BoundGroupIdsText = NormalizeQqIdText(source.BoundGroupIdsText),
            ReconnectIntervalSec = Math.Clamp(source.ReconnectIntervalSec, 1, 120),
            DatabasePath = dbPath,
            PollIntervalSec = Math.Clamp(source.PollIntervalSec, 0.2, 30),
            DefaultEncoding = string.IsNullOrWhiteSpace(source.DefaultEncoding) ? "utf-8" : source.DefaultEncoding.Trim(),
            FallbackEncoding = string.IsNullOrWhiteSpace(source.FallbackEncoding) ? "gbk" : source.FallbackEncoding.Trim(),
            SuperUsersText = source.SuperUsersText?.Trim() ?? string.Empty,
            OsqPollIntervalSec = Math.Clamp(source.OsqPollIntervalSec, 3, 300),
            OsqRequestTimeoutSec = Math.Clamp(source.OsqRequestTimeoutSec, 3, 60)
        };
    }

    private static string NormalizeQqIdText(string? value)
    {
        var lines = (value ?? string.Empty)
            .Split(['\r', '\n', '\t', ',', ';', '，', '；', ' '], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .Where(item => long.TryParse(item, out var qq) && qq > 0)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return lines.Count == 0 ? string.Empty : string.Join(Environment.NewLine, lines);
    }

    private static FrpIntegrationSettings NormalizeFrp(FrpIntegrationSettings? source)
    {
        source ??= new FrpIntegrationSettings();
        var mode = Enum.IsDefined(source.ThirdPartyFrpcLaunchMode)
            ? source.ThirdPartyFrpcLaunchMode
            : ThirdPartyFrpcLaunchMode.ConfigFile;
        var thirdPartyDefault = mode == ThirdPartyFrpcLaunchMode.CommandOnly
            ? FrpIntegrationSettings.DefaultThirdPartyFrpcCommand
            : FrpIntegrationSettings.DefaultThirdPartyFrpcConfigCommand;

        return new FrpIntegrationSettings
        {
            FrpCommand = string.IsNullOrWhiteSpace(source.FrpCommand)
                ? FrpIntegrationSettings.DefaultFrpCommand
                : source.FrpCommand.Trim(),
            ThirdPartyFrpcLaunchMode = mode,
            ThirdPartyFrpcCommand = string.IsNullOrWhiteSpace(source.ThirdPartyFrpcCommand)
                ? thirdPartyDefault
                : source.ThirdPartyFrpcCommand.Trim()
        };
    }

    private static string NormalizeListenPrefix(string? value)
    {
        var raw = string.IsNullOrWhiteSpace(value)
            ? "http://127.0.0.1:18089/"
            : value.Trim();
        if (!raw.EndsWith('/'))
        {
            raw += "/";
        }

        if (!Uri.TryCreate(raw, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
            string.IsNullOrWhiteSpace(uri.Host))
        {
            return "http://127.0.0.1:18089/";
        }

        var prefix = uri.GetLeftPart(UriPartial.Path);
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    private static string NormalizeFilePathOrEmpty(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeHttpUrlOrDefault(string? value, string defaultValue)
    {
        var raw = string.IsNullOrWhiteSpace(value) ? defaultValue : value.Trim();
        return Uri.TryCreate(raw, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
               !string.IsNullOrWhiteSpace(uri.Host)
            ? uri.ToString()
            : defaultValue;
    }
}
