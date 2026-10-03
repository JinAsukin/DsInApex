namespace DsInApex.Core.Models;

/// <summary>
/// 单条本地化审计发现（P8）。
///
/// <para>
/// 一条 = 一个键（或一处写死文案）在两种语言下的问题。
/// <see cref="IssueKind"/> 决定它是否阻断验收：
/// <b>缺键与占位符不一致是真缺陷</b>（界面会露出键名或 <c>{1}</c> 毛刺）；
/// <b>双语同文与写死文案只是可疑</b>（品牌名、语言选择器自身的标签都合法）。
/// </para>
/// </summary>
public sealed record LocalizationAuditFinding
{
    /// <summary>词条键（写死文案项则是 <c>Text="xxx"</c> 这样的属性描述）。</summary>
    public required string Key { get; init; }

    /// <summary>问题类型。</summary>
    public required LocalizationAuditIssueKind IssueKind { get; init; }

    /// <summary>涉及的文件（仅源码腿有；控件树腿为空）。</summary>
    public string? File { get; init; }

    /// <summary>涉及的行号（仅源码腿有；控件树腿为 0）。</summary>
    public int Line { get; init; }

    /// <summary>问题描述（直接可读，可原样写进自检日志）。</summary>
    public required string Detail { get; init; }

    /// <summary>文件与行号的紧凑前缀（源码腿用；无位置时为空串）。</summary>
    public string Location => string.IsNullOrEmpty(File) ? string.Empty : $"{File}:{Line} ";
}

/// <summary>本地化审计的问题类型。</summary>
public enum LocalizationAuditIssueKind
{
    /// <summary>键只存在于 <c>Strings.En</c>（中文界面会回落显示英文原文）。</summary>
    MissingInZhCN,

    /// <summary>键只存在于 <c>Strings.ZhCN</c>（英文界面会显示键名本身）。</summary>
    MissingInEn,

    /// <summary>键在两种语言里都缺失（最严重：界面直接显示 <c>Loc_XXX</c>）。</summary>
    MissingInBoth,

    /// <summary>
    /// 两种语言的文案完全相同。
    ///
    /// <para>
    /// ⚠️ 这是<b>可疑</b>而非缺陷：品牌名（<c>Loc_AppName = "Ds in Apex"</c>）、
    /// 技术名词（DualSense / APEX 4）本就该在两种语言下相同。
    /// 之所以仍要报，是因为 P6 踩过的两次坑（<c>Expander</c> 命中 <c>ContentControl</c> 分支、
    /// <c>MenuFlyoutSubItem</c> 漏分支）恰好都会表现为「某处文案在两种语言下一样」，
    /// 而这种异常在中文界面下<b>肉眼完全看不出来</b>。
    /// </para>
    /// </summary>
    SameTextInBothLanguages,

    /// <summary>
    /// 占位符集合在两种语言下不一致。
    ///
    /// <para>
    /// 例：中文 <c>"{0} 已就绪"</c> 1 个占位符，英文 <c>"{0} is ready, {1} left"</c> 2 个 ——
    /// <c>Format()</c> 遇到参数数量不符会静默降级返回模板原文，界面上就会露出 <c>{1}</c> 毛刺。
    /// </para>
    /// </summary>
    PlaceholderMismatch,
}

/// <summary>审计发现的阻断性判定。</summary>
public static class LocalizationAuditFindingExtensions
{
    /// <summary>
    /// 这条发现是否阻断验收。
    ///
    /// <para>
    /// <b>只有「缺键」与「占位符不一致」阻断。</b>
    /// 双语同文、写死文案都只是可疑项 —— 它们有大量合法存在
    /// （品牌名、语言选择器自身的「简体中文 / English」标签、技术型号），
    /// 判失败会逼着人维护例外名单，最后整个检查就没人看了，那比漏报更糟。
    /// </para>
    /// </summary>
    public static bool IsBlocking(this LocalizationAuditFinding finding)
        => finding.IssueKind
           is not (LocalizationAuditIssueKind.SameTextInBothLanguages);
}
