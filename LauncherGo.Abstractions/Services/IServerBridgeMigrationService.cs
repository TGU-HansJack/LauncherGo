namespace LauncherGo.Abstractions.Services;

public interface IServerBridgeMigrationService
{
    Task<bool> MigrateAsync(CancellationToken cancellationToken = default);
}
