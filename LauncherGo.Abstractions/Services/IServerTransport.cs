namespace LauncherGo.Abstractions.Services;

public interface IServerTransport
{
    Task SendGroupMessageToServerAsync(long groupId, string message, CancellationToken cancellationToken = default);
}
