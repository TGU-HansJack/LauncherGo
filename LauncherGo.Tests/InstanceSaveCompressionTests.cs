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
