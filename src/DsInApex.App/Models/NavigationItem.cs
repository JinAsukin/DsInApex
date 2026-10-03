namespace DsInApex.App.Models;

/// <summary>
/// 导航项。
/// </summary>
/// <param name="Tag">唯一标识（用于选中态同步，不参与显示）。</param>
/// <param name="LocKey">导航文案的本地化键（如 Loc_NavDashboard）。</param>
/// <param name="IconGlyph">Segoe Fluent Icons 字形。</param>
/// <param name="PageType">目标页面类型。</param>
public sealed record NavigationItem(
    string Tag,
    string LocKey,
    string IconGlyph,
    Type PageType);
