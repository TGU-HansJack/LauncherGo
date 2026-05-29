using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;

namespace LauncherGo.Services;

public sealed class InstanceSaveService(
    ILauncherPreferencesService preferencesService,
    IInstanceProfileService profileService) : IInstanceSaveService
{
    public Task<IReadOnlyList<SaveFileEntry>> GetSavesAsync(
        InstanceProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var profiles = profile is null
            ? profileService.GetProfiles()
            : [profile];

        var entries = new List<SaveFileEntry>();
        foreach (var item in profiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(item.SaveDirectory))
            {
                continue;
            }

            var saveDirectory = Path.GetFullPath(item.SaveDirectory);
            Directory.CreateDirectory(saveDirectory);
            foreach (var path in Directory.EnumerateFiles(saveDirectory, "*.vcdbs", SearchOption.TopDirectoryOnly))
            {
                var info = new FileInfo(path);
                entries.Add(new SaveFileEntry
                {
                    FullPath = info.FullName,
                    FileName = info.Name,
                    ProfileId = item.Id,
                    ProfileName = item.Name,
                    SizeBytes = info.Length,
                    LastWriteTimeUtc = info.LastWriteTimeUtc
                });
            }

            if (string.IsNullOrWhiteSpace(item.ActiveSaveFile) ||
                !item.ActiveSaveFile.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string activePath;
            try
            {
                activePath = Path.GetFullPath(item.ActiveSaveFile.Trim());
            }
            catch
            {
                continue;
            }

            if (entries.Any(entry =>
                    entry.ProfileId.Equals(item.Id, StringComparison.OrdinalIgnoreCase) &&
                    entry.FullPath.Equals(activePath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var activeDirectory = Path.GetDirectoryName(activePath);
            if (string.IsNullOrWhiteSpace(activeDirectory) ||
                !activeDirectory.Equals(saveDirectory, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var activeExists = File.Exists(activePath);
            var activeInfo = activeExists ? new FileInfo(activePath) : null;
            entries.Add(new SaveFileEntry
            {
                FullPath = activePath,
                FileName = Path.GetFileName(activePath),
                ProfileId = item.Id,
                ProfileName = item.Name,
                SizeBytes = activeInfo?.Length ?? 0,
                LastWriteTimeUtc = activeInfo?.LastWriteTimeUtc ?? item.LastUpdatedUtc
            });
        }

        return Task.FromResult<IReadOnlyList<SaveFileEntry>>(
            entries
                .OrderByDescending(entry => entry.LastWriteTimeUtc)
                .ToList());
    }

    public async Task<string> ImportSaveAsync(
        InstanceProfile profile,
        string sourceFilePath,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourceFilePath))
        {
            throw new InvalidOperationException("存档路径不能为空。");
        }

        var source = Path.GetFullPath(sourceFilePath.Trim());
        if (!File.Exists(source))
        {
            throw new InvalidOperationException($"存档文件不存在：{source}");
        }

        if (!source.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅支持导入 .vcdbs 存档文件。");
        }

        Directory.CreateDirectory(profile.SaveDirectory);
        var target = Path.Combine(profile.SaveDirectory, Path.GetFileName(source));
        await using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read);
        await using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None);
        await input.CopyToAsync(output, cancellationToken);

        if (!File.Exists(profile.ActiveSaveFile))
        {
            await SetActiveSaveAsync(profile, target, cancellationToken);
        }

        return target;
    }

    public async Task<string> CreateSaveAsync(
        InstanceProfile profile,
        string saveName,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(saveName))
        {
            throw new InvalidOperationException("存档名称不能为空。");
        }

        var saveDirectory = ResolveSaveDirectory(profile);
        Directory.CreateDirectory(saveDirectory);

        var fileName = LauncherWorkspacePathHelper.SanitizeFileName(saveName.Trim()) + ".vcdbs";
        var savePath = Path.Combine(saveDirectory, fileName);
        await SetActiveSaveAsync(profile, savePath, cancellationToken);
        return savePath;
    }

    public Task<string> BackupActiveSaveAsync(
        InstanceProfile profile,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeSave = ResolveActiveSavePath(profile);
        if (!File.Exists(activeSave))
        {
            throw new InvalidOperationException("当前存档文件不存在，无法备份。");
        }

        var backupRoot = Path.Combine(profile.DirectoryPath, "Backups");
        Directory.CreateDirectory(backupRoot);

        var sourceName = Path.GetFileNameWithoutExtension(activeSave);
        var backupName = $"{LauncherWorkspacePathHelper.SanitizeFileName(sourceName)}-{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.vcdbs";
        var backupPath = Path.Combine(backupRoot, backupName);
        File.Copy(activeSave, backupPath, overwrite: false);
        return Task.FromResult(backupPath);
    }

    public Task SetActiveSaveAsync(
        InstanceProfile profile,
        string saveFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(saveFilePath))
        {
            throw new InvalidOperationException("存档路径不能为空。");
        }

        var fullPath = Path.GetFullPath(saveFilePath.Trim());
        var saveRoot = LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory);
        var requestedDirectory = LauncherWorkspacePathHelper.NormalizePath(Path.GetDirectoryName(fullPath));
        if (!LauncherWorkspacePathHelper.IsSameOrChildPath(requestedDirectory, saveRoot))
        {
            var fileName = Path.GetFileName(fullPath);
            if (string.IsNullOrWhiteSpace(fileName))
            {
                fileName = "default.vcdbs";
            }

            fullPath = Path.Combine(saveRoot, fileName);
        }

        var saveDirectory = Path.GetDirectoryName(fullPath);
        if (string.IsNullOrWhiteSpace(saveDirectory))
        {
            throw new InvalidOperationException("无效存档路径。");
        }

        Directory.CreateDirectory(saveDirectory);
        if (File.Exists(fullPath) && new FileInfo(fullPath).Length == 0)
        {
            File.Delete(fullPath);
        }

        profile.ActiveSaveFile = fullPath;
        profile.SaveDirectory = saveDirectory;
        profile.LastUpdatedUtc = DateTimeOffset.UtcNow;
        ServerConfigBootstrapper.ApplySaveLocation(Path.Combine(profile.DirectoryPath, "serverconfig.json"), fullPath);
        profileService.UpdateProfile(profile);
        return Task.CompletedTask;
    }

    public Task<SavePathInspection> InspectSavePathAsync(
        InstanceProfile profile,
        string? candidateSaveFilePath = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var candidate = string.IsNullOrWhiteSpace(candidateSaveFilePath)
            ? profile.ActiveSaveFile
            : candidateSaveFilePath;

        string effectiveSaveFile;
        try
        {
            effectiveSaveFile = Path.GetFullPath(
                string.IsNullOrWhiteSpace(candidate)
                    ? Path.Combine(ResolveSaveDirectory(profile), "default.vcdbs")
                    : candidate.Trim());
        }
        catch
        {
            effectiveSaveFile = Path.Combine(ResolveSaveDirectory(profile), "default.vcdbs");
        }

        var effectiveSaveDirectory = Path.GetDirectoryName(effectiveSaveFile) ?? string.Empty;
        var profileSaveRoot = LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory);
        var globalSaveRoot = LauncherWorkspacePathHelper.NormalizePath(preferencesService.Load().SaveDirectory);
        var saveDirectory = LauncherWorkspacePathHelper.NormalizePath(effectiveSaveDirectory);

        var isCrossProfile = !string.IsNullOrWhiteSpace(saveDirectory)
                             && !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, profileSaveRoot)
                             && LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, globalSaveRoot);
        var isExternal = !string.IsNullOrWhiteSpace(saveDirectory)
                         && !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, profileSaveRoot)
                         && !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, globalSaveRoot);

        var warningMessage = isCrossProfile
            ? "当前存档路径位于其他档案目录，可能导致启动错档或误删。"
            : isExternal
                ? "当前存档路径位于工作区外部目录，请确认这是你预期的数据源。"
                : string.Empty;

        return Task.FromResult(new SavePathInspection
        {
            EffectiveSaveFile = effectiveSaveFile,
            EffectiveSaveDirectory = effectiveSaveDirectory,
            Source = isCrossProfile ? "cross-profile" : isExternal ? "external" : "instance",
            IsMissing = !File.Exists(effectiveSaveFile),
            IsCrossProfile = isCrossProfile,
            IsExternal = isExternal,
            WarningMessage = warningMessage
        });
    }

    public Task<int> DeleteSavesAsync(
        IReadOnlyCollection<string> saveFilePaths,
        CancellationToken cancellationToken = default)
    {
        if (saveFilePaths.Count == 0)
        {
            return Task.FromResult(0);
        }

        var preferences = preferencesService.Load();
        var deleted = 0;
        foreach (var path in saveFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) ||
                !path.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
                !LauncherWorkspacePathHelper.IsSameOrChildPath(path, preferences.SaveDirectory) ||
                !File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            deleted++;
        }

        return Task.FromResult(deleted);
    }

    public Task<int> DeleteSavesAsync(
        InstanceProfile profile,
        IReadOnlyCollection<string> saveFilePaths,
        CancellationToken cancellationToken = default)
    {
        if (saveFilePaths.Count == 0)
        {
            return Task.FromResult(0);
        }

        var saveRoot = LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory);
        var deleted = 0;
        foreach (var path in saveFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) ||
                !path.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
                !LauncherWorkspacePathHelper.IsSameOrChildPath(path, saveRoot) ||
                !File.Exists(path))
            {
                continue;
            }

            File.Delete(path);
            deleted++;
        }

        return Task.FromResult(deleted);
    }

    private static string ResolveSaveDirectory(InstanceProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.SaveDirectory))
        {
            return Path.GetFullPath(profile.SaveDirectory);
        }

        return Path.Combine(profile.DirectoryPath, "Saves");
    }

    private static string ResolveActiveSavePath(InstanceProfile profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.ActiveSaveFile))
        {
            return Path.GetFullPath(profile.ActiveSaveFile);
        }

        return Path.Combine(ResolveSaveDirectory(profile), "default.vcdbs");
    }
}
