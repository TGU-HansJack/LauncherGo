namespace LauncherGo.Domains.Models;

public sealed class InstanceProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string DirectoryPath { get; set; } = string.Empty;

    public string SaveDirectory { get; set; } = string.Empty;

    public string ActiveSaveFile { get; set; } = string.Empty;

    public DateTimeOffset CreatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset LastUpdatedUtc { get; set; } = DateTimeOffset.UtcNow;

    public override string ToString()
    {
        return string.IsNullOrWhiteSpace(Name) ? Id : Name;
    }
}
