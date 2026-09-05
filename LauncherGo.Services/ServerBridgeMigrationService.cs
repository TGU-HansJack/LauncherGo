using Microsoft.Data.Sqlite;
using LauncherGo.Abstractions.Services;
using System.Text.Json.Nodes;

namespace LauncherGo.Services;

/// <summary>One-time cleanup of legacy OSQ files. The old HTTP endpoint is never started.</summary>
public sealed class ServerBridgeMigrationService : IServerBridgeMigrationService
{
    private readonly ILauncherPreferencesService _preferencesService;
    private readonly IInstanceProfileService _profileService;
    private readonly IServerBridgeService _serverBridgeService;
    private readonly IInstanceServerConfigService _serverConfigService;

    public ServerBridgeMigrationService(
        ILauncherPreferencesService preferencesService,
        IInstanceProfileService profileService,
        IServerBridgeService serverBridgeService,
        IInstanceServerConfigService serverConfigService)
    {
        _preferencesService = preferencesService;
        _profileService = profileService;
        _serverBridgeService = serverBridgeService;
        _serverConfigService = serverConfigService;
    }

    public async Task<bool> MigrateAsync(CancellationToken cancellationToken = default)
    {
        WorkspacePathHelper.EnsureWorkspace();
        var settingsPath = Path.Combine(WorkspacePathHelper.RobotRoot, "openserverquery-settings.json");
        var hadLegacy = File.Exists(settingsPath);
        if (hadLegacy)
        {
            try { File.Delete(settingsPath); } catch { }
        }

        foreach (var path in Directory.EnumerateFiles(WorkspacePathHelper.RobotRoot, "osq_snapshots*", SearchOption.TopDirectoryOnly)
                     .Concat(Directory.EnumerateFiles(WorkspacePathHelper.RobotRoot, "osq_forward_state*", SearchOption.TopDirectoryOnly)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try { File.Delete(path); } catch { }
        }

        var databases = Directory.EnumerateFiles(WorkspacePathHelper.RobotRoot, "*.db", SearchOption.TopDirectoryOnly).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var configuredDatabase = _preferencesService.Load().Robot.DatabasePath;
        if (!string.IsNullOrWhiteSpace(configuredDatabase) && File.Exists(configuredDatabase)) databases.Add(Path.GetFullPath(configuredDatabase));
        foreach (var database in databases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database }.ToString());
                await connection.OpenAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND (name LIKE 'osq_snapshots%' OR name LIKE 'osq_forward_state%');";
                var tables = new List<string>();
                await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
                    while (await reader.ReadAsync(cancellationToken)) tables.Add(reader.GetString(0));
                foreach (var table in tables)
                {
                    await using var drop = connection.CreateCommand();
                    drop.CommandText = $"DROP TABLE IF EXISTS [{table.Replace("]", "]]")}]";
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            catch { }
        }

        foreach (var profile in _profileService.GetProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var legacyConfig in new[]
                     {
                         Path.Combine(profile.DirectoryPath, "ModConfig", "openserverquery.json"),
                         Path.Combine(profile.DirectoryPath, "ModConfig", "launchergocommandbridge.json")
                     })
            {
                if (File.Exists(legacyConfig)) try { File.Delete(legacyConfig); } catch { }
            }

            var legacyMod = Path.Combine(WorkspacePathHelper.GetProfileModsPath(profile.DirectoryPath), "launchergocommandbridge");
            if (Directory.Exists(legacyMod)) try { Directory.Delete(legacyMod, recursive: true); } catch { }
            try
            {
                var root = JsonNode.Parse(await _serverConfigService.LoadRawJsonAsync(profile, cancellationToken)) as JsonObject;
                if (root?["WorldConfig"]?["DisabledMods"] is JsonArray disabledMods)
                {
                    var retained = disabledMods.Where(x => x is not null)
                        .Select(x => x!.GetValue<string>())
                        .Where(x => !x.Equals("launchergocommandbridge", StringComparison.OrdinalIgnoreCase) && !x.StartsWith("launchergocommandbridge@", StringComparison.OrdinalIgnoreCase))
                        .ToArray();
                    disabledMods.Clear();
                    foreach (var value in retained) disabledMods.Add(value);
                    await _serverConfigService.SaveRawJsonAsync(profile, root.ToJsonString(), cancellationToken);
                }
            }
            catch { }
            await _serverBridgeService.LoadSettingsAsync(profile, cancellationToken);
        }

        return hadLegacy;
    }
}
