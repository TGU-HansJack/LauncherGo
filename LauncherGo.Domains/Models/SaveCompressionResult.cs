namespace LauncherGo.Domains.Models;

public sealed class SaveCompressionResult
{
    public required string SourcePath { get; init; }

    public required string CompressedPath { get; init; }

    public bool Skipped { get; init; }

    public bool SourceDeleted { get; init; }
}
