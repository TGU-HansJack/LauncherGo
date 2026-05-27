using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

public interface ILauncherPreferencesService
{
    LauncherPreferences Load();

    void Save(LauncherPreferences preferences);
}
