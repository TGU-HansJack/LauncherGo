using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Enums;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class InstanceSaveCompressionTests
{
    [Fact]
    public async Task CompressBackupAndImportCompressedSave_RoundTripsAndDeletesSource()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-save-compression-");
        try
        {
            var profileDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "profile")).FullName;
            var saveDirectory = Directory.CreateDirectory(Path.Combine(profileDirectory, "Saves")).FullName;
            var backupDirectory = Directory.CreateDirectory(Path.Combine(profileDirectory, "Backups")).FullName;
            var compressionDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "compressed")).FullName;
            var sourcePath = Path.Combine(backupDirectory, "world.vcdbs");
            var sourceBytes = Enumerable.Range(0, 32_768)
                .Select(index => (byte)(index % 17))
                .ToArray();
            await File.WriteAllBytesAsync(sourcePath, sourceBytes);

            var profile = new InstanceProfile
            {
                Id = "profile-id",
                Name = "Test",
                DirectoryPath = profileDirectory,
                SaveDirectory = saveDirectory,
                ActiveSaveFile = Path.Combine(saveDirectory, "default.vcdbs")
            };
            var preferences = new LauncherPreferences
            {
                WorkspaceRoot = root.FullName,
                SaveCompression = new SaveCompressionSettings
                {
                    Enabled = true,
                    CompressionLevel = 5,
                    CompressionPath = compressionDirectory,
                    UpdateMode = SaveCompressionUpdateMode.AddAndReplace,
                    DeleteSourceFiles = true
                }
            };

            var service = new InstanceSaveService(
                new FakeProfileService(profile),
                new FakePreferencesService(preferences));

            var compression = await service.CompressBackupAsync(profile, sourcePath);

            Assert.NotNull(compression);
            Assert.False(compression!.Skipped);
            Assert.True(compression.SourceDeleted);
            Assert.False(File.Exists(sourcePath));
            Assert.True(File.Exists(compression.CompressedPath));

            var importedPath = await service.ImportSaveAsync(profile, compression.CompressedPath);

            Assert.Equal(Path.Combine(saveDirectory, "world.vcdbs"), importedPath);
            Assert.Equal(sourceBytes, await File.ReadAllBytesAsync(importedPath));
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ManagedBackupsPruneOldestLogicalBackupAndPreserveManualFiles()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-managed-backup-");
        try
        {
            var (service, profile, _) = CreateManagedBackupFixture(root, compressionEnabled: false);
            var backupDirectory = Path.Combine(profile.DirectoryPath, "Backups");
            Directory.CreateDirectory(backupDirectory);
            var manualPath = Path.Combine(backupDirectory, "manual-backup.vcdbs");
            await File.WriteAllBytesAsync(manualPath, [1, 2, 3]);

            var first = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_01-00-00-00000001.vcdbs",
                retentionCount: 2);
            await Task.Delay(5);
            var second = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_02-00-00-00000002.vcdbs",
                retentionCount: 2);
            await Task.Delay(5);
            var third = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_03-00-00-00000003.vcdbs",
                retentionCount: 2);

            Assert.False(File.Exists(first.SourcePath));
            Assert.True(File.Exists(second.SourcePath));
            Assert.True(File.Exists(third.SourcePath));
            Assert.True(File.Exists(manualPath));
            Assert.Equal(1, third.RemovedCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ManagedBackupRetentionCountsSourceAndZstdAsOneBackup()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-managed-zstd-");
        try
        {
            var (service, profile, compressionDirectory) = CreateManagedBackupFixture(
                root,
                compressionEnabled: true,
                deleteSource: false);

            var first = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_01-00-00-00000001.vcdbs",
                retentionCount: 1);
            await Task.Delay(5);
            var second = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_02-00-00-00000002.vcdbs",
                retentionCount: 1);

            Assert.NotNull(first.CompressedPath);
            Assert.False(File.Exists(first.SourcePath));
            Assert.False(File.Exists(first.CompressedPath!));
            Assert.True(File.Exists(second.SourcePath));
            Assert.True(File.Exists(second.CompressedPath!));
            Assert.StartsWith(compressionDirectory, second.CompressedPath!, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(1, second.RemovedCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ManagedBackupCanRetainOnlyCompressedArtifact()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-managed-zstd-only-");
        try
        {
            var (service, profile, _) = CreateManagedBackupFixture(
                root,
                compressionEnabled: true,
                deleteSource: true);

            var first = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_01-00-00-00000001.vcdbs",
                retentionCount: 24);

            Assert.True(first.SourceDeleted);
            Assert.False(File.Exists(first.SourcePath));
            Assert.NotNull(first.CompressedPath);
            Assert.True(File.Exists(first.CompressedPath!));

            var second = await service.BackupManagedActiveSaveAsync(
                profile,
                "launchergo-backup-world-2026-08-11_02-00-00-00000002.vcdbs",
                retentionCount: 1);

            Assert.False(File.Exists(first.CompressedPath!));
            Assert.True(second.SourceDeleted);
            Assert.NotNull(second.CompressedPath);
            Assert.True(File.Exists(second.CompressedPath!));
            Assert.Equal(1, second.RemovedCount);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task ZeroRetentionKeepsAllManagedBackups()
    {
        var root = Directory.CreateTempSubdirectory("launchergo-managed-unlimited-");
        try
        {
            var (service, profile, _) = CreateManagedBackupFixture(root, compressionEnabled: false);
            for (var index = 0; index < 3; index++)
            {
                await service.BackupManagedActiveSaveAsync(
                    profile,
                    $"launchergo-backup-world-2026-08-11_0{index + 1}-00-00-0000000{index + 1}.vcdbs",
                    retentionCount: 0);
            }

            var managedFiles = Directory.GetFiles(
                Path.Combine(profile.DirectoryPath, "Backups"),
                "launchergo-backup-*.vcdbs",
                SearchOption.TopDirectoryOnly);
            Assert.Equal(3, managedFiles.Length);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    private static (InstanceSaveService Service, InstanceProfile Profile, string CompressionDirectory)
        CreateManagedBackupFixture(
            DirectoryInfo root,
            bool compressionEnabled,
            bool deleteSource = false)
    {
        var profileDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "profile")).FullName;
        var saveDirectory = Directory.CreateDirectory(Path.Combine(profileDirectory, "Saves")).FullName;
        var compressionDirectory = Directory.CreateDirectory(Path.Combine(root.FullName, "compressed")).FullName;
        var activeSave = Path.Combine(saveDirectory, "world.vcdbs");
        File.WriteAllBytes(activeSave, Enumerable.Range(0, 4096).Select(index => (byte)(index % 31)).ToArray());
        var profile = new InstanceProfile
        {
            Id = "profile-id",
            Name = "Test",
            DirectoryPath = profileDirectory,
            SaveDirectory = saveDirectory,
            ActiveSaveFile = activeSave
        };
        var preferences = new LauncherPreferences
        {
            WorkspaceRoot = root.FullName,
            SaveCompression = new SaveCompressionSettings
            {
                Enabled = compressionEnabled,
                CompressionLevel = 3,
                CompressionPath = compressionDirectory,
                UpdateMode = SaveCompressionUpdateMode.AddAndReplace,
                DeleteSourceFiles = deleteSource
            }
        };
        var service = new InstanceSaveService(
            new FakeProfileService(profile),
            new FakePreferencesService(preferences));
        return (service, profile, compressionDirectory);
    }

    private sealed class FakePreferencesService(LauncherPreferences preferences) : ILauncherPreferencesService
    {
        public LauncherPreferences Load() => preferences;

        public void Save(LauncherPreferences value)
        {
        }
    }

    private sealed class FakeProfileService(InstanceProfile profile) : IInstanceProfileService
    {
        public IReadOnlyList<string> GetInstalledVersions() => [];

        public IReadOnlyList<InstanceProfile> GetProfiles() => [profile];

        public InstanceProfile? GetProfileById(string profileId) =>
            profile.Id.Equals(profileId, StringComparison.OrdinalIgnoreCase) ? profile : null;

        public InstanceProfile CreateProfile(string profileName, string version) =>
            throw new NotSupportedException();

        public InstanceProfile ImportProfile(string directoryPath) =>
            throw new NotSupportedException();

        public void UpdateProfile(InstanceProfile value)
        {
        }

        public int DeleteProfiles(IReadOnlyCollection<string> profileIds, bool deleteData) =>
            throw new NotSupportedException();

        public string GetDefaultSaveFilePath(string profileId) =>
            Path.Combine(profile.SaveDirectory, "default.vcdbs");

        public string EnsureVersionInstalled(string version) =>
            throw new NotSupportedException();
    }
}
