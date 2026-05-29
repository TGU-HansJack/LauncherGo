using System.IO.Compression;
using System.Text.Json;
using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class InstanceProfileService(ILauncherPreferencesService preferencesService) : IInstanceProfileService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly object _gate = new();

    public IReadOnlyList<string> GetInstalledVersions()
    {
        var preferences = LoadPreferences();
        var versions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (Directory.Exists(LauncherWorkspacePathHelper.InstalledServerRoot(preferences)))
        {
            foreach (var directory in Directory.EnumerateDirectories(LauncherWorkspacePathHelper.InstalledServerRoot(preferences)))
            {
                if (File.Exists(Path.Combine(directory, "VintagestoryServer.exe")))
                {
                    versions.Add(Path.GetFileName(directory));
                }
            }
        }

        if (Directory.Exists(preferences.ServerDirectory))
        {
            foreach (var packagePath in Directory.EnumerateFiles(preferences.ServerDirectory, "vs_server_win-x64_*.zip", SearchOption.TopDirectoryOnly))
            {
                var version = LauncherWorkspacePathHelper.TryExtractVersionFromPackageName(Path.GetFileName(packagePath));
                if (!string.IsNullOrWhiteSpace(version))
                {
                    versions.Add(version);
                }
            }
        }

        return versions
            .OrderByDescending(version => version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public IReadOnlyList<InstanceProfile> GetProfiles()
    {
        var preferences = LoadPreferences();
        lock (_gate)
        {
            var index = ReadIndex(preferences);
            var changed = false;
            foreach (var profile in index.Profiles)
            {
                changed |= NormalizeProfile(preferences, profile);
            }

            if (changed)
            {
                WriteIndex(preferences, index);
            }

            return index.Profiles
                .OrderByDescending(profile => profile.LastUpdatedUtc)
                .Select(Clone)
                .ToList();
        }
    }

    public InstanceProfile? GetProfileById(string profileId)
    {
        if (string.IsNullOrWhiteSpace(profileId))
        {
            return null;
        }

        return GetProfiles()
            .FirstOrDefault(profile => profile.Id.Equals(profileId.Trim(), StringComparison.OrdinalIgnoreCase));
    }

    public InstanceProfile CreateProfile(string profileName, string version)
    {
        if (string.IsNullOrWhiteSpace(profileName))
        {
            throw new InvalidOperationException("档案名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("请先选择服务端版本。");
        }

        var preferences = LoadPreferences();
        var selectedVersion = version.Trim();
        var installPath = EnsureVersionInstalled(selectedVersion);

        lock (_gate)
        {
            var profileId = Guid.NewGuid().ToString("N");
            var profile = new InstanceProfile
            {
                Id = profileId,
                Name = profileName.Trim(),
                Version = selectedVersion,
                DirectoryPath = LauncherWorkspacePathHelper.ProfileDataPath(preferences, profileId),
                SaveDirectory = LauncherWorkspacePathHelper.ProfileSaveDirectory(preferences, profileId),
                ActiveSaveFile = LauncherWorkspacePathHelper.ProfileDefaultSaveFile(preferences, profileId),
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };

            Directory.CreateDirectory(profile.DirectoryPath);
            Directory.CreateDirectory(profile.SaveDirectory);
            Directory.CreateDirectory(Path.Combine(profile.DirectoryPath, "Mods"));
            Directory.CreateDirectory(Path.Combine(profile.DirectoryPath, "Logs"));

            ServerConfigBootstrapper.EnsureGenerated(installPath, profile);

            var index = ReadIndex(preferences);
            index.Profiles.Add(profile);
            WriteIndex(preferences, index);

            return Clone(profile);
        }
    }

    public InstanceProfile ImportProfile(string directoryPath)
    {
        if (string.IsNullOrWhiteSpace(directoryPath))
        {
            throw new InvalidOperationException("请选择档案目录。");
        }

        var fullPath = Path.GetFullPath(directoryPath.Trim());
        if (!Directory.Exists(fullPath))
        {
            throw new InvalidOperationException($"档案目录不存在：{fullPath}");
        }

        if (!File.Exists(Path.Combine(fullPath, "serverconfig.json")))
        {
            throw new InvalidOperationException("所选目录不是有效服务端档案目录，缺少 serverconfig.json。");
        }

        var preferences = LoadPreferences();
        lock (_gate)
        {
            var index = ReadIndex(preferences);
            var existing = index.Profiles.FirstOrDefault(profile =>
                LauncherWorkspacePathHelper.NormalizePath(profile.DirectoryPath)
                    .Equals(LauncherWorkspacePathHelper.NormalizePath(fullPath), StringComparison.OrdinalIgnoreCase));
            if (existing is not null)
            {
                return Clone(existing);
            }

            var profileId = Guid.NewGuid().ToString("N");
            var version = GetInstalledVersions().FirstOrDefault() ?? string.Empty;
            var saveDirectory = Path.Combine(fullPath, "Saves");
            var activeSaveFile = ResolveImportedActiveSaveFile(fullPath, saveDirectory);
            var profile = new InstanceProfile
            {
                Id = profileId,
                Name = Path.GetFileName(fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)),
                Version = version,
                DirectoryPath = fullPath,
                SaveDirectory = Path.GetDirectoryName(activeSaveFile) ?? saveDirectory,
                ActiveSaveFile = activeSaveFile,
                CreatedAtUtc = DateTimeOffset.UtcNow,
                LastUpdatedUtc = DateTimeOffset.UtcNow
            };

            Directory.CreateDirectory(profile.SaveDirectory);
            ServerConfigBootstrapper.ApplySaveLocation(Path.Combine(profile.DirectoryPath, "serverconfig.json"), profile.ActiveSaveFile);

            index.Profiles.Add(profile);
            WriteIndex(preferences, index);
            return Clone(profile);
        }
    }

    public void UpdateProfile(InstanceProfile profile)
    {
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            throw new InvalidOperationException("档案 ID 不能为空。");
        }

        var preferences = LoadPreferences();
        lock (_gate)
        {
            var index = ReadIndex(preferences);
            var existing = index.Profiles.FirstOrDefault(item =>
                item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                throw new InvalidOperationException("未找到要更新的档案。");
            }

            existing.Name = string.IsNullOrWhiteSpace(profile.Name) ? existing.Name : profile.Name.Trim();
            existing.Version = profile.Version;
            existing.DirectoryPath = profile.DirectoryPath;
            existing.SaveDirectory = profile.SaveDirectory;
            existing.ActiveSaveFile = profile.ActiveSaveFile;
            existing.LastUpdatedUtc = profile.LastUpdatedUtc == default ? DateTimeOffset.UtcNow : profile.LastUpdatedUtc;
            NormalizeProfile(preferences, existing);
            WriteIndex(preferences, index);
        }
    }

    public int DeleteProfiles(IReadOnlyCollection<string> profileIds, bool deleteData)
    {
        if (profileIds.Count == 0)
        {
            return 0;
        }

        var idSet = profileIds
            .Where(id => !string.IsNullOrWhiteSpace(id))
            .Select(id => id.Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (idSet.Count == 0)
        {
            return 0;
        }

        var preferences = LoadPreferences();
        List<InstanceProfile> deleting;
        lock (_gate)
        {
            var index = ReadIndex(preferences);
            deleting = index.Profiles
                .Where(profile => idSet.Contains(profile.Id))
                .Select(Clone)
                .ToList();
            if (deleting.Count == 0)
            {
                return 0;
            }

            index.Profiles = index.Profiles
                .Where(profile => !idSet.Contains(profile.Id))
                .ToList();
            WriteIndex(preferences, index);
        }

        if (deleteData)
        {
            foreach (var profile in deleting)
            {
                TryDeleteOwnedDirectory(profile.DirectoryPath, preferences.ProfileDirectory);
                TryDeleteOwnedDirectory(profile.SaveDirectory, preferences.SaveDirectory);
            }
        }

        return deleting.Count;
    }

    public string GetDefaultSaveFilePath(string profileId)
    {
        var preferences = LoadPreferences();
        var profile = GetProfileById(profileId);
        if (profile is not null)
        {
            return Path.Combine(ResolveProfileSaveDirectory(preferences, profile), "default.vcdbs");
        }

        return LauncherWorkspacePathHelper.ProfileDefaultSaveFile(preferences, profileId);
    }

    public string EnsureVersionInstalled(string version)
    {
        if (string.IsNullOrWhiteSpace(version))
        {
            throw new InvalidOperationException("服务端版本不能为空。");
        }

        var preferences = LoadPreferences();
        var installPath = LauncherWorkspacePathHelper.ServerInstallPath(preferences, version.Trim());
        var serverExe = Path.Combine(installPath, "VintagestoryServer.exe");
        if (File.Exists(serverExe))
        {
            return installPath;
        }

        var packagePath = FindPackagePath(preferences, version.Trim());
        if (string.IsNullOrWhiteSpace(packagePath))
        {
            throw new InvalidOperationException($"未找到版本 {version} 的服务端压缩包，请先下载或导入。");
        }

        var tempRoot = Path.Combine(
            LauncherWorkspacePathHelper.TempRoot(preferences),
            $"install-{LauncherWorkspacePathHelper.SanitizeFileName(version)}-{Guid.NewGuid():N}");
        var extractRoot = Path.Combine(tempRoot, "extract");
        Directory.CreateDirectory(extractRoot);

        try
        {
            ZipFile.ExtractToDirectory(packagePath, extractRoot, overwriteFiles: true);
            var extractedExe = Directory
                .EnumerateFiles(extractRoot, "VintagestoryServer.exe", SearchOption.AllDirectories)
                .FirstOrDefault();
            if (string.IsNullOrWhiteSpace(extractedExe))
            {
                throw new InvalidOperationException("压缩包内未找到 VintagestoryServer.exe。");
            }

            var extractedServerDirectory = Path.GetDirectoryName(extractedExe)
                ?? throw new InvalidOperationException("无法识别服务端目录。");

            Directory.CreateDirectory(Path.GetDirectoryName(installPath)!);
            if (Directory.Exists(installPath))
            {
                SafeDeleteDirectory(installPath, LauncherWorkspacePathHelper.InstalledServerRoot(preferences));
            }

            Directory.Move(extractedServerDirectory, installPath);
            return installPath;
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private LauncherPreferences LoadPreferences()
    {
        var preferences = preferencesService.Load();
        LauncherWorkspacePathHelper.EnsureWorkspace(preferences);
        return preferences;
    }

    private static string? FindPackagePath(LauncherPreferences preferences, string version)
    {
        if (!Directory.Exists(preferences.ServerDirectory))
        {
            return null;
        }

        return Directory
            .EnumerateFiles(preferences.ServerDirectory, "vs_server_win-x64_*.zip", SearchOption.TopDirectoryOnly)
            .Select(path => new
            {
                Path = path,
                Version = LauncherWorkspacePathHelper.TryExtractVersionFromPackageName(Path.GetFileName(path)),
                LastWrite = File.GetLastWriteTimeUtc(path)
            })
            .Where(item => item.Version?.Equals(version, StringComparison.OrdinalIgnoreCase) == true)
            .OrderByDescending(item => item.LastWrite)
            .Select(item => item.Path)
            .FirstOrDefault();
    }

    private static InstanceProfileIndex ReadIndex(LauncherPreferences preferences)
    {
        var path = LauncherWorkspacePathHelper.ProfilesIndexPath(preferences);
        if (!File.Exists(path))
        {
            return new InstanceProfileIndex();
        }

        try
        {
            return JsonSerializer.Deserialize<InstanceProfileIndex>(File.ReadAllText(path), JsonOptions)
                   ?? new InstanceProfileIndex();
        }
        catch
        {
            return new InstanceProfileIndex();
        }
    }

    private static void WriteIndex(LauncherPreferences preferences, InstanceProfileIndex index)
    {
        var path = LauncherWorkspacePathHelper.ProfilesIndexPath(preferences);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(index, JsonOptions));
    }

    private static bool NormalizeProfile(LauncherPreferences preferences, InstanceProfile profile)
    {
        var changed = false;
        if (string.IsNullOrWhiteSpace(profile.Id))
        {
            profile.Id = Guid.NewGuid().ToString("N");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(profile.DirectoryPath))
        {
            profile.DirectoryPath = LauncherWorkspacePathHelper.ProfileDataPath(preferences, profile.Id);
            changed = true;
        }

        var preferredSaveDirectory = ResolveProfileSaveDirectory(preferences, profile);
        var saveDirectory = TryGetFullPath(profile.SaveDirectory);
        var activeSaveFile = TryGetFullPath(profile.ActiveSaveFile);
        var profileDirectory = TryGetFullPath(profile.DirectoryPath);

        if (!string.IsNullOrWhiteSpace(activeSaveFile) &&
            !string.IsNullOrWhiteSpace(profileDirectory) &&
            LauncherWorkspacePathHelper.IsSameOrChildPath(activeSaveFile, profileDirectory))
        {
            var activeSaveDirectory = Path.GetDirectoryName(activeSaveFile);
            if (!string.IsNullOrWhiteSpace(activeSaveDirectory) &&
                !LauncherWorkspacePathHelper.NormalizePath(activeSaveDirectory)
                    .Equals(LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory), StringComparison.OrdinalIgnoreCase))
            {
                profile.SaveDirectory = activeSaveDirectory;
                changed = true;
            }

            if (!activeSaveFile.Equals(profile.ActiveSaveFile, StringComparison.OrdinalIgnoreCase))
            {
                profile.ActiveSaveFile = activeSaveFile;
                changed = true;
            }
        }
        else
        {
            if (string.IsNullOrWhiteSpace(saveDirectory) ||
                !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, profile.DirectoryPath))
            {
                profile.SaveDirectory = preferredSaveDirectory;
                saveDirectory = preferredSaveDirectory;
                changed = true;
            }

            if (string.IsNullOrWhiteSpace(activeSaveFile) ||
                !LauncherWorkspacePathHelper.IsSameOrChildPath(activeSaveFile, profile.DirectoryPath))
            {
                var migratedSaveFile = TryCopySaveIntoDirectory(activeSaveFile, profile.SaveDirectory);
                profile.ActiveSaveFile = migratedSaveFile
                                         ?? Path.Combine(
                                             profile.SaveDirectory,
                                             ResolveSaveFileName(activeSaveFile));
                changed = true;
            }
        }

        if (string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            profile.SaveDirectory = preferredSaveDirectory;
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(profile.ActiveSaveFile))
        {
            profile.ActiveSaveFile = Path.Combine(profile.SaveDirectory, "default.vcdbs");
            changed = true;
        }

        if (string.IsNullOrWhiteSpace(profile.Name))
        {
            profile.Name = $"Profile-{profile.Id[..Math.Min(6, profile.Id.Length)]}";
            changed = true;
        }

        return changed;
    }

    private static string ResolveProfileSaveDirectory(LauncherPreferences preferences, InstanceProfile profile)
    {
        return string.IsNullOrWhiteSpace(profile.DirectoryPath)
            ? LauncherWorkspacePathHelper.ProfileSaveDirectory(preferences, profile.Id)
            : Path.Combine(profile.DirectoryPath, "Saves");
    }

    private static string ResolveImportedActiveSaveFile(string profileDirectoryPath, string defaultSaveDirectory)
    {
        var defaultSaveFile = Path.Combine(defaultSaveDirectory, "default.vcdbs");
        var configuredSaveFile = TryReadConfiguredSaveFile(profileDirectoryPath);
        if (string.IsNullOrWhiteSpace(configuredSaveFile))
        {
            return defaultSaveFile;
        }

        var fullSaveFile = TryGetFullPath(
            Path.IsPathRooted(configuredSaveFile)
                ? configuredSaveFile
                : Path.Combine(profileDirectoryPath, configuredSaveFile));
        if (string.IsNullOrWhiteSpace(fullSaveFile))
        {
            return defaultSaveFile;
        }

        if (LauncherWorkspacePathHelper.IsSameOrChildPath(fullSaveFile, profileDirectoryPath))
        {
            return fullSaveFile;
        }

        return TryCopySaveIntoDirectory(fullSaveFile, defaultSaveDirectory)
               ?? Path.Combine(defaultSaveDirectory, ResolveSaveFileName(fullSaveFile));
    }

    private static string? TryReadConfiguredSaveFile(string profileDirectoryPath)
    {
        var configPath = Path.Combine(profileDirectoryPath, "serverconfig.json");
        if (!File.Exists(configPath))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(configPath));
            return document.RootElement.TryGetProperty("WorldConfig", out var worldConfig) &&
                   worldConfig.TryGetProperty("SaveFileLocation", out var saveFileLocation) &&
                   saveFileLocation.ValueKind == JsonValueKind.String
                ? saveFileLocation.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? TryCopySaveIntoDirectory(string? sourceSaveFile, string targetDirectory)
    {
        if (string.IsNullOrWhiteSpace(sourceSaveFile) ||
            !sourceSaveFile.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
            !File.Exists(sourceSaveFile))
        {
            return null;
        }

        var fileName = Path.GetFileName(sourceSaveFile);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "default.vcdbs";
        }

        var targetSaveFile = Path.Combine(targetDirectory, fileName);
        try
        {
            Directory.CreateDirectory(targetDirectory);
            if (!File.Exists(targetSaveFile))
            {
                File.Copy(sourceSaveFile, targetSaveFile, overwrite: false);
            }

            return targetSaveFile;
        }
        catch
        {
            return null;
        }
    }

    private static string ResolveSaveFileName(string? saveFilePath)
    {
        var fileName = string.IsNullOrWhiteSpace(saveFilePath) ? string.Empty : Path.GetFileName(saveFilePath);
        return string.IsNullOrWhiteSpace(fileName) ? "default.vcdbs" : fileName;
    }

    private static string TryGetFullPath(string? path)
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

    private static InstanceProfile Clone(InstanceProfile profile)
    {
        return new InstanceProfile
        {
            Id = profile.Id,
            Name = profile.Name,
            Version = profile.Version,
            DirectoryPath = profile.DirectoryPath,
            SaveDirectory = profile.SaveDirectory,
            ActiveSaveFile = profile.ActiveSaveFile,
            CreatedAtUtc = profile.CreatedAtUtc,
            LastUpdatedUtc = profile.LastUpdatedUtc
        };
    }

    private static void TryDeleteOwnedDirectory(string directoryPath, string rootPath)
    {
        if (!LauncherWorkspacePathHelper.IsSameOrChildPath(directoryPath, rootPath))
        {
            return;
        }

        if (Directory.Exists(directoryPath))
        {
            Directory.Delete(directoryPath, recursive: true);
        }
    }

    private static void SafeDeleteDirectory(string directoryPath, string rootPath)
    {
        if (!LauncherWorkspacePathHelper.IsSameOrChildPath(directoryPath, rootPath))
        {
            throw new InvalidOperationException($"拒绝删除工作区外目录：{directoryPath}");
        }

        Directory.Delete(directoryPath, recursive: true);
    }
}
