namespace LauncherGo.Domains.Models;

public sealed class SaveFileEntry
{
    public required string FullPath { get; init; }

    public required string FileName { get; init; }

    public required string ProfileId { get; init; }

    public required string ProfileName { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset LastWriteTimeUtc { get; init; }

    public override string ToString()
    {
        return FileName;
    }
}
