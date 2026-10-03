namespace DsInApex.Core.Models;

/// <summary>
/// 驱动清单中的一项（来自 <c>Driver-manifest.json</c>）。
///
/// <para>
/// 迁移自上游 <c>installer/driver-manifest.json</c> + <c>ApexSenseBridge.iss</c> 里的
/// <c>#define</c> 常量。上游把这份信息分散在 Inno 脚本、PowerShell 脚本和 JSON 三处，
/// DIA 统一收到此处，作为**唯一真源**。
/// </para>
///
/// <para>
/// <b>⚠️ 版本号与 SHA-256 是硬约束</b>：SHA-256 用于确认捆绑的安装器未被篡改（上游
/// <c>install-usbip.ps1</c> 同样做法）。改了安装器就必须同步改这里，否则安装会被拒绝。
/// </para>
/// </summary>
public sealed class DriverInfo
{
    /// <summary>清单键名，同时用作界面标识（如 <c>usbip-win2</c> / <c>HidHide</c>）。</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>界面显示名。</summary>
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>期望版本（如 <c>0.9.8.0</c>）。空字符串表示不做版本比对。</summary>
    public string Version { get; set; } = string.Empty;

    /// <summary>捆绑安装器文件名。</summary>
    public string Installer { get; set; } = string.Empty;

    /// <summary>期望的安装器 SHA-256（大写十六进制）。</summary>
    public string Sha256 { get; set; } = string.Empty;

    /// <summary>卸载注册项名（Inno 的 <c>_is1</c> 形式或 MSI 的 GUID）。</summary>
    public string ProductCode { get; set; } = string.Empty;

    /// <summary>随驱动一起安装的内核服务名（用于判定「是否装全」）。</summary>
    public string[] Services { get; set; } = [];

    /// <summary>驱动原始 INF 文件名（卸载审计用）。</summary>
    public string[] OriginalInf { get; set; } = [];

    /// <summary>静默安装参数（不含日志参数）。原上游硬编码在 <c>install-usbip.ps1</c> 与 <c>.iss</c> 里。</summary>
    public string[] InstallArgs { get; set; } = [];

    /// <summary>日志参数模板，<c>{log}</c> 占位符会被替换为实际日志路径；空表示安装器不支持日志参数。</summary>
    public string LogArgTemplate { get; set; } = string.Empty;

    /// <summary>是否为可选驱动（缺失不算「未就绪」，只是提示）。</summary>
    public bool Optional { get; set; }

    /// <summary>补充说明（界面显示）。</summary>
    public string Note { get; set; } = string.Empty;

    public bool IsRequired => !Optional;
}
