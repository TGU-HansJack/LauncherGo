namespace LauncherGo.Domains.Models;

/// <summary>
///     存档路径检查结果
/// </summary>
public class SavePathInspection
{
    /// <summary>
    ///     最终生效的存档文件路径
    /// </summary>
    public string EffectiveSaveFile { get; set; } = string.Empty;

    /// <summary>
    ///     最终生效的存档目录
    /// </summary>
    public string EffectiveSaveDirectory { get; set; } = string.Empty;

    /// <summary>
    ///     路径来源：instance / cross-profile / external / unknown
    /// </summary>
    public string Source { get; set; } = "unknown";

    /// <summary>
    ///     当前存档文件是否不存在（首启会由服务端创建）
    /// </summary>
    public bool IsMissing { get; set; }

    /// <summary>
    ///     是否跨档案目录
    /// </summary>
    public bool IsCrossProfile { get; set; }

    /// <summary>
    ///     是否在工作区外部
    /// </summary>
    public bool IsExternal { get; set; }

    /// <summary>
    ///     风险提示文案
    /// </summary>
    public string WarningMessage { get; set; } = string.Empty;
}

