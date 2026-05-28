namespace LauncherGo.Domains.Models;

public class ServerImageFileInfo
{
    public required ServerImageKind Kind { get; init; }

    public required string FullPath { get; init; }

    public required string RelativePath { get; init; }

    public required string FileName { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset LastWriteUtc { get; init; }
}
