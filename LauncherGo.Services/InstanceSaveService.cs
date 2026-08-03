using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;
using ZstdSharp;

namespace LauncherGo.Services;

public sealed class InstanceSaveService(
    IInstanceProfileService profileService,
    ILauncherPreferencesService preferencesService) : IInstanceSaveService
{
    private const int CompressionBufferSize = 128 * 1024;

    public InstanceSaveService(IInstanceProfileService profileService)
        : this(profileService, new LauncherPreferencesService())
    {
    }

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

        var isCompressed = IsCompressedSavePath(source);
        if (!isCompressed && !source.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅支持导入 .vcdbs 或 .vcdbs.zst 存档文件。");
        }

        Directory.CreateDirectory(profile.SaveDirectory);
        var targetFileName = isCompressed
            ? GetUncompressedSaveFileName(source)
            : Path.GetFileName(source);
        var target = Path.Combine(profile.SaveDirectory, targetFileName);
        var tempTarget = $"{target}.{Guid.NewGuid():N}.import.tmp";

        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             CompressionBufferSize,
                             FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             tempTarget,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CompressionBufferSize,
                             FileOptions.SequentialScan))
            {
                if (isCompressed)
                {
                    await using var decompressor = new DecompressionStream(
                        input,
                        CompressionBufferSize,
                        checkEndOfStream: true,
                        leaveOpen: true);
                    await decompressor.CopyToAsync(output, CompressionBufferSize, cancellationToken);
                }
                else
                {
                    await input.CopyToAsync(output, CompressionBufferSize, cancellationToken);
                }
            }

            if (new FileInfo(tempTarget).Length == 0)
            {
                throw new InvalidDataException("解压后的存档文件为空。");
            }

            File.Move(tempTarget, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(tempTarget);
        }

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

    public async Task<string> BackupActiveSaveAsync(
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
        var compression = await CompressBackupAsync(profile, backupPath, cancellationToken);
        return compression?.CompressedPath ?? backupPath;
    }

    public async Task<SaveCompressionResult?> CompressBackupAsync(
        InstanceProfile profile,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var preferences = preferencesService.Load();
        var settings = preferences.SaveCompression;
        if (settings is null || !settings.Enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(backupFilePath))
        {
            throw new InvalidOperationException("备份文件路径不能为空。");
        }

        var sourcePath = Path.GetFullPath(backupFilePath.Trim());
        if (!sourcePath.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("仅支持压缩 .vcdbs 存档文件。");
        }

        var backupRoot = LauncherWorkspacePathHelper.NormalizePath(
            Path.Combine(profile.DirectoryPath, "Backups"));
        if (!LauncherWorkspacePathHelper.IsSameOrChildPath(sourcePath, backupRoot))
        {
            throw new InvalidOperationException("存档压缩仅允许处理当前档案 Backups 目录中的文件。");
        }

        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("备份文件不存在。", sourcePath);
        }

        var configuredCompressionPath = settings.CompressionPath?.Trim();
        var compressionDirectory = string.IsNullOrWhiteSpace(configuredCompressionPath)
            ? LauncherPathHelper.GetSaveCompressionDirectory(preferences.WorkspaceRoot)
            : Path.GetFullPath(configuredCompressionPath);
        Directory.CreateDirectory(compressionDirectory);

        var compressedPath = Path.Combine(
            compressionDirectory,
            Path.GetFileName(sourcePath) + ".zst");

        if (settings.UpdateMode == SaveCompressionUpdateMode.UpdateAndAdd &&
            File.Exists(compressedPath) &&
            File.GetLastWriteTimeUtc(compressedPath) >= File.GetLastWriteTimeUtc(sourcePath))
        {
            return new SaveCompressionResult
            {
                SourcePath = sourcePath,
                CompressedPath = compressedPath,
                Skipped = true,
                SourceDeleted = false
            };
        }

        var temporaryPath = $"{compressedPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await using (var input = new FileStream(
                             sourcePath,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             CompressionBufferSize,
                             FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             CompressionBufferSize,
                             FileOptions.SequentialScan))
            await using (var compressor = new CompressionStream(
                             output,
                             Math.Clamp(settings.CompressionLevel, 1, 22),
                             CompressionBufferSize,
                             leaveOpen: true))
            {
                await input.CopyToAsync(compressor, CompressionBufferSize, cancellationToken);
            }

            if (new FileInfo(temporaryPath).Length == 0)
            {
                throw new InvalidDataException("ZSTD 压缩结果为空。");
            }

            ReplaceFile(temporaryPath, compressedPath);

            var sourceDeleted = false;
            if (settings.DeleteSourceFiles &&
                !sourcePath.Equals(compressedPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(sourcePath);
                sourceDeleted = true;
            }

            return new SaveCompressionResult
            {
                SourcePath = sourcePath,
                CompressedPath = compressedPath,
                SourceDeleted = sourceDeleted
            };
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    public async Task<int> CompressExistingBackupsAsync(
        CancellationToken cancellationToken = default)
    {
        var settings = preferencesService.Load().SaveCompression;
        if (settings is null || !settings.Enabled)
        {
            throw new InvalidOperationException("请先启用存档压缩。");
        }

        var processed = 0;
        foreach (var profile in profileService.GetProfiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            var backupRoot = Path.Combine(profile.DirectoryPath, "Backups");
            if (!Directory.Exists(backupRoot))
            {
                continue;
            }

            foreach (var backupPath in Directory.EnumerateFiles(
                         backupRoot,
                         "*.vcdbs",
                         SearchOption.TopDirectoryOnly))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var result = await CompressBackupAsync(profile, backupPath, cancellationToken);
                if (result is not null && !result.Skipped)
                {
                    processed++;
                }
            }
        }

        return processed;
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
        var otherProfileSaveRoots = profileService.GetProfiles()
            .Where(item => !item.Id.Equals(profile.Id, StringComparison.OrdinalIgnoreCase))
            .Select(item => LauncherWorkspacePathHelper.NormalizePath(item.SaveDirectory))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .ToList();
        var saveDirectory = LauncherWorkspacePathHelper.NormalizePath(effectiveSaveDirectory);

        var isCrossProfile = !string.IsNullOrWhiteSpace(saveDirectory)
                             && !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, profileSaveRoot)
                             && otherProfileSaveRoots.Any(root =>
                                 LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, root));
        var isExternal = !string.IsNullOrWhiteSpace(saveDirectory)
                         && !LauncherWorkspacePathHelper.IsSameOrChildPath(saveDirectory, profileSaveRoot)
                         && !isCrossProfile;

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

        var saveRoots = profileService.GetProfiles()
            .Select(profile => LauncherWorkspacePathHelper.NormalizePath(profile.SaveDirectory))
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var deleted = 0;
        foreach (var path in saveFilePaths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(path) ||
                !path.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase) ||
                !saveRoots.Any(root => LauncherWorkspacePathHelper.IsSameOrChildPath(path, root)) ||
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

    private static bool IsCompressedSavePath(string path)
    {
        return path.EndsWith(".vcdbs.zst", StringComparison.OrdinalIgnoreCase) ||
               path.EndsWith(".zst", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetUncompressedSaveFileName(string compressedPath)
    {
        var fileName = Path.GetFileName(compressedPath);
        if (fileName.EndsWith(".zst", StringComparison.OrdinalIgnoreCase))
        {
            fileName = fileName[..^4];
        }

        return fileName.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase)
            ? fileName
            : fileName + ".vcdbs";
    }

    private static void ReplaceFile(string temporaryPath, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            try
            {
                File.Replace(temporaryPath, destinationPath, null, ignoreMetadataErrors: true);
                return;
            }
            catch (PlatformNotSupportedException)
            {
                // Fall back to an overwrite move on platforms without File.Replace.
            }
            catch (IOException)
            {
                // File.Replace can fail when the destination is on a different filesystem.
            }
        }

        File.Move(temporaryPath, destinationPath, overwrite: true);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Temporary files are best-effort cleanup only.
        }
    }
}
