using LauncherGo.Abstractions.Services;

namespace LauncherGo.Services;

/// <summary>
///     本地服务端消息传输，仅路由到当前 LauncherGo 控制的本地服务端。
/// </summary>
public sealed class LocalServerTransport : IServerTransport
{
    private readonly IServerProcessService _serverProcessService;

    public LocalServerTransport(IServerProcessService serverProcessService)
    {
        _serverProcessService = serverProcessService;
    }

    public async Task SendGroupMessageToServerAsync(long groupId, string message, CancellationToken cancellationToken = default)
    {
        var outbound = (message ?? string.Empty).Replace('\r', ' ').Replace('\n', ' ').Trim();
        if (string.IsNullOrWhiteSpace(outbound))
        {
            return;
        }

        await _serverProcessService.SendCommandAsync($"/announce {outbound}", cancellationToken);
    }
}
