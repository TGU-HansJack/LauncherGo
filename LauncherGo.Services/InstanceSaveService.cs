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

            Directory.CreateDirectory(item.SaveDirectory);
            foreach (var path in Directory.EnumerateFiles(item.SaveDirectory, "*.vcdbs", SearchOption.TopDirectoryOnly))
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

        var configPath = Path.Combine(profile.DirectoryPath, "serverconfig.json");
        if (!File.Exists(profile.ActiveSaveFile))
        {
            profile.ActiveSaveFile = target;
            ServerConfigBootstrapper.ApplySaveLocation(configPath, target);
        }

        return target;
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
