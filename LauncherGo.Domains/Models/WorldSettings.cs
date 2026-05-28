namespace LauncherGo.Domains.Models;

/// <summary>
///     世界配置
/// </summary>
public class WorldSettings
{
    public string Seed { get; set; } = "123456789";

    public string WorldName { get; set; } = "A new world";

    public string SaveFileLocation { get; set; } = string.Empty;

    public string PlayStyle { get; set; } = "surviveandbuild";

    public string WorldType { get; set; } = "standard";

    public int? WorldHeight { get; set; } = 256;
}
