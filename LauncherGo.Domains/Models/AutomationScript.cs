namespace LauncherGo.Domains.Models;

/// <summary>
///     在实例生命周期关键点执行的 Windows 批处理脚本。
/// </summary>
public sealed class AutomationScript
{
    public bool Enabled { get; set; } = true;

    public AutomationScriptTrigger Trigger { get; set; } = AutomationScriptTrigger.BeforeStart;

    public string ScriptPath { get; set; } = string.Empty;
}
