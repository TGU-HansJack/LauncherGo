namespace LauncherGo.Domains.Models;

public sealed class LauncherUpdateAsset
{
    public string Name { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    public long Size { get; init; }

    public string Digest { get; init; } = string.Empty;
}
