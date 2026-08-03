using LauncherGo.Domains.Enums;

namespace LauncherGo.Domains.Models;

public sealed class SaveCompressionSettings
{
    public bool Enabled { get; set; }

    public int CompressionLevel { get; set; } = 3;

    public string CompressionPath { get; set; } = string.Empty;

    public SaveCompressionUpdateMode UpdateMode { get; set; } = SaveCompressionUpdateMode.UpdateAndAdd;

    public bool DeleteSourceFiles { get; set; }
}
