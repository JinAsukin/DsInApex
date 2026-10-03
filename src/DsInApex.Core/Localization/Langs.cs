namespace DsInApex.Core.Localization;

/// <summary>
/// 语言标识常量。
///
/// 与上游 <c>tray_settings.json</c> 的 <c>Language</c> 字段保持兼容：
/// 上游只支持 <c>auto</c> / <c>en</c> / <c>fr</c>，DIA 新增 <c>zh-CN</c>。
/// </summary>
public static class Langs
{
    /// <summary>跟随系统语言（上游既有语义）。</summary>
    public const string Auto = "auto";

    /// <summary>简体中文（DIA 新增，上游没有）。</summary>
    public const string ZhCN = "zh-CN";

    /// <summary>English（上游既有）。</summary>
    public const string En = "en";

    /// <summary>Français（上游既有，DIA 保留识别但不提供 UI 入口）。</summary>
    public const string Fr = "fr";

    /// <summary>DIA 界面正式提供的语言集（用于语言选择器渲染）。</summary>
    public static readonly IReadOnlyList<string> Supported = [ZhCN, En];
}
