namespace DsInApex.Core.Models;

/// <summary>开机自启的四种状态。</summary>
public enum AutoStartState
{
    /// <summary>未登记开机自启。</summary>
    Disabled,

    /// <summary>已登记，且指向当前正在运行的这个 exe。</summary>
    Enabled,

    /// <summary>
    /// 已登记，但指向<b>另一个路径</b>的 DsInApex.exe。
    ///
    /// <para>
    /// 这是<b>便携版特有的失效形态</b>：用户把文件夹挪个位置、或从 U 盘换到硬盘，
    /// 注册表里的旧路径就成了死链 —— 系统会在开机时尝试启动一个不存在的文件，
    /// 用户看到的现象是「设了自启但每次开机都没反应」，而日志里什么都查不到。
    /// 因此这一状态必须能被识别，并提供一键修复。
    /// </para>
    /// </summary>
    StalePath,

    /// <summary>注册表读取失败（权限被策略拦截等）。</summary>
    Unreadable,
}

/// <summary>
/// 开机自启状态快照（P6）。
///
/// <para>
/// 实现位置是 <c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> ——
/// 用户级、无需管理员、卸载/关闭即清理，符合便携版的「不污染系统」定位。
/// </para>
/// </summary>
public sealed record AutoStartStatus
{
    public required AutoStartState State { get; init; }

    /// <summary>注册表里当前登记的命令行（未登记时为 null）。</summary>
    public string? RegisteredCommand { get; init; }

    /// <summary>本机当前应有的命令行（形如 <c>"&lt;exe&gt;" --autostart</c>）。</summary>
    public required string ExpectedCommand { get; init; }

    /// <summary>注册表值名。</summary>
    public required string ValueName { get; init; }

    /// <summary>注册表键路径。</summary>
    public required string KeyPath { get; init; }

    /// <summary>读取失败原因（状态非 Unreadable 时为 null）。</summary>
    public string? Error { get; init; }

    /// <summary>是否已登记且指向正确的 exe。</summary>
    public bool IsEffective => State == AutoStartState.Enabled;

    /// <summary>是否登记了但路径已失效。</summary>
    public bool IsStale => State == AutoStartState.StalePath;
}
