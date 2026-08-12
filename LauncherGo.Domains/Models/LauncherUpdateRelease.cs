namespace LauncherGo.Domains.Models;

public sealed class LauncherUpdateRelease
{
    public string TagName { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Body { get; init; } = string.Empty;

    public string HtmlUrl { get; init; } = string.Empty;

    public bool IsPrerelease { get; init; }

    public DateTimeOffset? PublishedAtUtc { get; init; }

    public IReadOnlyList<LauncherUpdateAsset> Assets { get; init; } = [];
}
