namespace LauncherGo.Domains.Models;

/// <summary>
///     一次受管自动备份及其轮换结果。
/// </summary>
public sealed class ManagedBackupResult
{
    public required string BackupId { get; init; }

    public required string SourcePath { get; init; }

    public string? CompressedPath { get; init; }

    public bool CompressionEnabled { get; init; }

    public bool CompressionSkipped { get; init; }

    public bool SourceDeleted { get; init; }

    public string? CompressionError { get; init; }

    public int RemovedCount { get; init; }
}
