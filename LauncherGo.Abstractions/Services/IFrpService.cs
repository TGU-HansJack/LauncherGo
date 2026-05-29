using LauncherGo.Domains.Models;

namespace LauncherGo.Abstractions.Services;

/// <summary>
///     FRP 服务
/// </summary>
public interface IFrpService
{
    event EventHandler<FrpRuntimeStatus>? StatusChanged;

    string ConfigPath { get; }

    /// <summary>
    ///     获取当前运行状态
    /// </summary>
    FrpRuntimeStatus GetCurrentStatus();

    /// <summary>
    ///     读取配置文件内容
    /// </summary>
    Task<string> LoadConfigAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     保存配置文件内容
    /// </summary>
    Task SaveConfigAsync(string configText, CancellationToken cancellationToken = default);

    /// <summary>
    ///     导入 FRP 可执行文件
    /// </summary>
    Task ImportExecutableAsync(string sourcePath, CancellationToken cancellationToken = default);

    /// <summary>
    ///     启动 FRP
    /// </summary>
    Task StartAsync(CancellationToken cancellationToken = default);

    /// <summary>
    ///     停止 FRP
    /// </summary>
    Task StopAsync(TimeSpan gracefulTimeout, CancellationToken cancellationToken = default);
}

