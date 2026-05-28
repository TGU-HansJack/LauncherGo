using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface IInstanceProfileService
{
    IReadOnlyList<string> GetInstalledVersions();

    IReadOnlyList<InstanceProfile> GetProfiles();

    InstanceProfile? GetProfileById(string profileId);

    InstanceProfile CreateProfile(string profileName, string version);

    InstanceProfile ImportProfile(string directoryPath);

    void UpdateProfile(InstanceProfile profile);

    int DeleteProfiles(IReadOnlyCollection<string> profileIds, bool deleteData);

    string GetDefaultSaveFilePath(string profileId);

    string EnsureVersionInstalled(string version);
}
