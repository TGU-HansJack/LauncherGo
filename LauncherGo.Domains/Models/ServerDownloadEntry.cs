namespace LauncherGo.Domains.Models;

public sealed class ServerDownloadEntry
{
    public required string Version { get; init; }

    public required string Platform { get; init; }

    public required string FileSize { get; init; }

    public required string FileName { get; init; }

    public required string CdnUrl { get; init; }

    public override string ToString()
    {
        return $"{Version} ({FileSize})";
    }
}
