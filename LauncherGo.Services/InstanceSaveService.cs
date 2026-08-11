using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services.Paths;
using System.Text.Json;
using ZstdSharp;

namespace LauncherGo.Services;

public sealed class InstanceSaveService(
    IInstanceProfileService profileService,
    ILauncherPreferencesService preferencesService) : IInstanceSaveService
{
    private const int CompressionBufferSize = 128 * 1024;
    private const string ManagedBackupPrefix = "launchergo-backup-";
    private const string ManagedManifestFileName = ".launchergo-managed-backups.json";
    private const string ManagedCompressionMarkerFileName = ".launchergo-managed-backups";
    private static readonly JsonSerializerOptions ManagedBackupJsonOptions = new()
    {
        WriteIndented = true
    };
    private readonly SemaphoreSlim managedBackupGate = new(1, 1);

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

    public async Task<ManagedBackupResult> BackupManagedActiveSaveAsync(
        InstanceProfile profile,
        string backupFileName,
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var activeSave = ResolveActiveSavePath(profile);
        if (!File.Exists(activeSave))
        {
            throw new InvalidOperationException("当前存档文件不存在，无法备份。");
        }

        var backupRoot = Path.GetFullPath(Path.Combine(profile.DirectoryPath, "Backups"));
        Directory.CreateDirectory(backupRoot);
        var normalizedFileName = NormalizeManagedBackupFileName(backupFileName);
        var backupPath = Path.Combine(backupRoot, normalizedFileName);
        File.Copy(activeSave, backupPath, overwrite: false);
        return await FinalizeManagedBackupAsync(profile, backupPath, retentionCount, cancellationToken);
    }

    public async Task<ManagedBackupResult> FinalizeManagedBackupAsync(
        InstanceProfile profile,
        string backupFilePath,
        int retentionCount,
        CancellationToken cancellationToken = default)
    {
        await managedBackupGate.WaitAsync(cancellationToken);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourcePath = Path.GetFullPath(backupFilePath.Trim());
            var backupRoot = Path.GetFullPath(Path.Combine(profile.DirectoryPath, "Backups"));
            if (!LauncherWorkspacePathHelper.IsSameOrChildPath(sourcePath, backupRoot) ||
                !Path.GetFileName(sourcePath).StartsWith(ManagedBackupPrefix, StringComparison.OrdinalIgnoreCase) ||
                !sourcePath.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException("受管备份文件路径无效。");
            }

            if (!File.Exists(sourcePath))
            {
                throw new FileNotFoundException("备份文件不存在。", sourcePath);
            }

            var createdAtUtc = DateTimeOffset.UtcNow;
            var settings = preferencesService.Load().SaveCompression;
            SaveCompressionResult? compression = null;
            string? compressionError = null;
            if (settings?.Enabled == true)
            {
                try
                {
                    var managedCompressionDirectory = ResolveManagedCompressionDirectory(profile);
                    compression = await CompressBackupCoreAsync(
                        profile,
                        sourcePath,
                        managedCompressionDirectory,
                        cancellationToken);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    compressionError = ex.Message;
                }
            }

            var backupId = Path.GetFileNameWithoutExtension(sourcePath);
            var manifest = LoadManagedBackupManifest(backupRoot);
            ReconcileManagedBackupManifest(profile, manifest);
            var entry = new ManagedBackupEntry
            {
                BackupId = backupId,
                SourceFileName = Path.GetFileName(sourcePath),
                CreatedAtUtc = createdAtUtc,
                CompressedPath = compression?.CompressedPath
            };
            manifest.Backups.RemoveAll(item => item.BackupId.Equals(backupId, StringComparison.OrdinalIgnoreCase));
            manifest.Backups.Add(entry);

            var removedCount = PruneManagedBackups(profile, manifest, retentionCount);
            SaveManagedBackupManifest(backupRoot, manifest);

            return new ManagedBackupResult
            {
                BackupId = backupId,
                SourcePath = sourcePath,
                CompressedPath = compression?.CompressedPath,
                CompressionEnabled = settings?.Enabled == true,
                CompressionSkipped = compression?.Skipped == true,
                SourceDeleted = compression?.SourceDeleted == true,
                CompressionError = compressionError,
                RemovedCount = removedCount
            };
        }
        finally
        {
            managedBackupGate.Release();
        }
    }

    public async Task<SaveCompressionResult?> CompressBackupAsync(
        InstanceProfile profile,
        string backupFilePath,
        CancellationToken cancellationToken = default)
    {
        return await CompressBackupCoreAsync(profile, backupFilePath, null, cancellationToken);
    }

    private async Task<SaveCompressionResult?> CompressBackupCoreAsync(
        InstanceProfile profile,
        string backupFilePath,
        string? destinationDirectory,
        CancellationToken cancellationToken)
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
        var compressionDirectory = destinationDirectory ??
            (string.IsNullOrWhiteSpace(configuredCompressionPath)
                ? LauncherPathHelper.GetSaveCompressionDirectory(preferences.WorkspaceRoot)
                : Path.GetFullPath(configuredCompressionPath));
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

    private string ResolveManagedCompressionDirectory(InstanceProfile profile)
    {
        var preferences = preferencesService.Load();
        var settings = preferences.SaveCompression ?? new SaveCompressionSettings();
        var configuredCompressionPath = settings.CompressionPath?.Trim();
        var root = string.IsNullOrWhiteSpace(configuredCompressionPath)
            ? LauncherPathHelper.GetSaveCompressionDirectory(preferences.WorkspaceRoot)
            : Path.GetFullPath(configuredCompressionPath);
        var profileId = LauncherWorkspacePathHelper.SanitizeFileName(profile.Id);
        if (string.IsNullOrWhiteSpace(profileId))
            profileId = "default";

        var directory = Path.Combine(root, "LauncherGoManagedBackups", profileId);
        Directory.CreateDirectory(directory);
        var marker = Path.Combine(directory, ManagedCompressionMarkerFileName);
        if (!File.Exists(marker))
        {
            File.WriteAllText(marker, "LauncherGo managed backup artifacts\n");
        }

        return directory;
    }

    private static string NormalizeManagedBackupFileName(string value)
    {
        var fileName = Path.GetFileName(value?.Trim());
        if (string.IsNullOrWhiteSpace(fileName) ||
            !fileName.StartsWith(ManagedBackupPrefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("受管备份文件名无效。");
        }

        return fileName;
    }

    private static ManagedBackupManifest LoadManagedBackupManifest(string backupRoot)
    {
        var manifestPath = Path.Combine(backupRoot, ManagedManifestFileName);
        try
        {
            if (File.Exists(manifestPath))
            {
                var parsed = JsonSerializer.Deserialize<ManagedBackupManifest>(
                    File.ReadAllText(manifestPath),
                    ManagedBackupJsonOptions);
                if (parsed is not null)
                    return parsed;
            }
        }
        catch
        {
            // A damaged manifest is rebuilt from managed files below.
        }

        return new ManagedBackupManifest();
    }

    private static void SaveManagedBackupManifest(string backupRoot, ManagedBackupManifest manifest)
    {
        var manifestPath = Path.Combine(backupRoot, ManagedManifestFileName);
        var temporaryPath = $"{manifestPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            var json = JsonSerializer.Serialize(manifest, ManagedBackupJsonOptions);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, manifestPath, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temporaryPath);
        }
    }

    private static void ReconcileManagedBackupManifest(
        InstanceProfile profile,
        ManagedBackupManifest manifest)
    {
        var backupRoot = Path.GetFullPath(Path.Combine(profile.DirectoryPath, "Backups"));
        var existing = new HashSet<string>(
            manifest.Backups.Select(item => item.BackupId),
            StringComparer.OrdinalIgnoreCase);

        if (!Directory.Exists(backupRoot))
            return;

        foreach (var path in Directory.EnumerateFiles(backupRoot, $"{ManagedBackupPrefix}*.vcdbs", SearchOption.TopDirectoryOnly))
        {
            var id = Path.GetFileNameWithoutExtension(path);
            if (!existing.Add(id))
                continue;

            manifest.Backups.Add(new ManagedBackupEntry
            {
                BackupId = id,
                SourceFileName = Path.GetFileName(path),
                CreatedAtUtc = new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero)
            });
        }

        manifest.Backups.RemoveAll(item =>
        {
            var sourcePath = Path.Combine(backupRoot, item.SourceFileName ?? string.Empty);
            var sourceExists = IsManagedSourcePath(sourcePath, backupRoot) && File.Exists(sourcePath);
            var compressedExists = IsManagedCompressedPath(item.CompressedPath) && File.Exists(item.CompressedPath!);
            return !sourceExists && !compressedExists;
        });
    }

    private static int PruneManagedBackups(
        InstanceProfile profile,
        ManagedBackupManifest manifest,
        int retentionCount)
    {
        if (retentionCount <= 0)
            return 0;

        var removed = 0;
        foreach (var entry in manifest.Backups
                     .OrderByDescending(item => item.CreatedAtUtc)
                     .ThenByDescending(item => item.BackupId, StringComparer.OrdinalIgnoreCase)
                     .Skip(retentionCount)
                     .ToList())
        {
            if (!TryDeleteManagedBackup(profile, entry))
                continue;

            manifest.Backups.Remove(entry);
            removed++;
        }

        return removed;
    }

    private static bool TryDeleteManagedBackup(InstanceProfile profile, ManagedBackupEntry entry)
    {
        var backupRoot = Path.GetFullPath(Path.Combine(profile.DirectoryPath, "Backups"));
        try
        {
            var sourcePath = Path.Combine(backupRoot, entry.SourceFileName ?? string.Empty);
            if (!IsManagedSourcePath(sourcePath, backupRoot))
                return false;

            if (!string.IsNullOrWhiteSpace(entry.CompressedPath) &&
                !IsManagedCompressedPath(entry.CompressedPath))
            {
                return false;
            }

            if (File.Exists(sourcePath))
            {
                File.Delete(sourcePath);
            }

            if (IsManagedCompressedPath(entry.CompressedPath) && File.Exists(entry.CompressedPath!))
            {
                File.Delete(entry.CompressedPath!);
            }

            return !File.Exists(sourcePath) &&
                   (string.IsNullOrWhiteSpace(entry.CompressedPath) || !File.Exists(entry.CompressedPath));
        }
        catch
        {
            return false;
        }
    }

    private static bool IsManagedSourcePath(string path, string backupRoot)
    {
        try
        {
            var fullPath = Path.GetFullPath(path);
            return LauncherWorkspacePathHelper.IsSameOrChildPath(fullPath, backupRoot) &&
                   Path.GetDirectoryName(fullPath)!.Equals(backupRoot, StringComparison.OrdinalIgnoreCase) &&
                   Path.GetFileName(fullPath).StartsWith(ManagedBackupPrefix, StringComparison.OrdinalIgnoreCase) &&
                   fullPath.EndsWith(".vcdbs", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsManagedCompressedPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            var fullPath = Path.GetFullPath(path);
            var directory = Path.GetDirectoryName(fullPath);
            return directory is not null &&
                   File.Exists(Path.Combine(directory, ManagedCompressionMarkerFileName)) &&
                   Path.GetFileName(fullPath).StartsWith(ManagedBackupPrefix, StringComparison.OrdinalIgnoreCase) &&
                   fullPath.EndsWith(".vcdbs.zst", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private sealed class ManagedBackupManifest
    {
        public int Version { get; set; } = 1;

        public List<ManagedBackupEntry> Backups { get; set; } = [];
    }

    private sealed class ManagedBackupEntry
    {
        public string BackupId { get; set; } = string.Empty;

        public string SourceFileName { get; set; } = string.Empty;

        public DateTimeOffset CreatedAtUtc { get; set; }

        public string? CompressedPath { get; set; }
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
                if (Path.GetFileName(backupPath).StartsWith(ManagedBackupPrefix, StringComparison.OrdinalIgnoreCase))
                {
                    var managedResult = await FinalizeManagedBackupAsync(
                        profile,
                        backupPath,
                        retentionCount: 0,
                        cancellationToken);
                    if (managedResult.CompressedPath is not null &&
                        !managedResult.CompressionSkipped &&
                        string.IsNullOrWhiteSpace(managedResult.CompressionError))
                    {
                        processed++;
                    }

                    continue;
                }

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
