using LauncherGo.Abstractions.Services;
using LauncherGo.Domains.Models;
using LauncherGo.Services;
using Xunit;

namespace LauncherGo.Tests;

public sealed class ServerProcessServiceStatusTests
{
    [Fact]
    public void GetCurrentStatus_OnlyReadsCache()
    {
        var service = new ServerProcessService(new ThrowingProfileService());

        var status = service.GetCurrentStatus("profile-1");

        Assert.Equal("profile-1", status.ProfileId);
        Assert.False(status.IsRunning);
    }

    private sealed class ThrowingProfileService : IInstanceProfileService
    {
        public IReadOnlyList<string> GetInstalledVersions() => throw new InvalidOperationException();

        public IReadOnlyList<InstanceProfile> GetProfiles() => throw new InvalidOperationException();

        public InstanceProfile? GetProfileById(string profileId) => throw new InvalidOperationException();

        public InstanceProfile CreateProfile(string profileName, string version) =>
            throw new InvalidOperationException();

        public InstanceProfile ImportProfile(string directoryPath) => throw new InvalidOperationException();

        public void UpdateProfile(InstanceProfile profile) => throw new InvalidOperationException();

        public int DeleteProfiles(IReadOnlyCollection<string> profileIds, bool deleteData) =>
            throw new InvalidOperationException();

        public string GetDefaultSaveFilePath(string profileId) => throw new InvalidOperationException();

        public string EnsureVersionInstalled(string version) => throw new InvalidOperationException();
    }
}
