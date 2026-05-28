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
}
