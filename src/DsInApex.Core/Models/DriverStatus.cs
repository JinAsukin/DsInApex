namespace DsInApex.Core.Models;

/// <summary>驱动整体状态。</summary>
public enum DriverState
{
    /// <summary>尚未检测。</summary>
    Unknown,

    /// <summary>既无卸载注册项、也无服务键 —— 完全没装。</summary>
    NotInstalled,

    /// <summary>装了，但版本与期望不符（需修复/重装）。</summary>
    Outdated,

    /// <summary>注册项与服务键对不上（装了一半 / 被第三方破坏）。</summary>
    Partial,

    /// <summary>已装、版本正确、服务键齐全，但服务未在运行。</summary>
    Installed,

    /// <summary>完全就绪：版本正确 + 服务齐全 + 服务运行中。</summary>
    Running,

    /// <summary>可选驱动未安装（不影响使用，仅提示）。</summary>
    OptionalMissing,
}

/// <summary>单个内核服务的状态。</summary>
public enum DriverServiceState
{
    /// <summary>服务键都不存在。</summary>
    Missing,

    /// <summary>服务已注册但未运行（可能是「需重启」态）。</summary>
    Stopped,

    /// <summary>服务正在运行。</summary>
    Running,

    /// <summary>查询失败（权限等原因）。</summary>
    Unknown,
}

/// <summary>
/// 一次驱动检测的结果。
///
/// <para>
/// 判定基准**对齐上游 Inno 脚本**（<c>ApexSenseBridge.iss</c> 的
/// <c>NeedUsbip</c> / <c>VerifyUsbipInstall</c> / <c>NeedHidHide</c> / <c>VerifyHidHideInstall</c>）：
/// 上游只看「卸载注册项是否存在 + 版本是否相等 + 服务键是否存在」三件事。
/// DIA 在此之上补了「服务是否在运行」，因为对用户而言「装了但没跑」和「没装」是两种不同的病。
/// </para>
/// </summary>
public sealed class DriverStatus
{
    public required DriverInfo Info { get; init; }

    public DriverState State { get; set; } = DriverState.Unknown;

    /// <summary>注册表读到的已安装版本；未装时为 null。</summary>
    public string? InstalledVersion { get; set; }

    /// <summary>卸载注册项是否存在。</summary>
    public bool UninstallEntryPresent { get; set; }

    /// <summary>各服务名的状态。</summary>
    public Dictionary<string, DriverServiceState> Services { get; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>捆绑安装器的绝对路径；未打包时为 null。</summary>
    public string? InstallerPath { get; set; }

    /// <summary>安装器文件是否存在于磁盘。</summary>
    public bool InstallerPresent { get; set; }

    /// <summary>安装器 SHA-256 是否与清单一致。</summary>
    public bool InstallerVerified { get; set; }

    /// <summary>人类可读的补充说明（错误原因 / 提示）。</summary>
    public string? Detail { get; set; }

    /// <summary>是否算「就绪」。</summary>
    public bool IsHealthy => State is DriverState.Running or DriverState.Installed;

    /// <summary>是否需要立刻处理（缺装 / 版本错 / 装了一半）。</summary>
    public bool NeedsAction => State is DriverState.NotInstalled or DriverState.Outdated or DriverState.Partial;

    /// <summary>是否需要重启才能生效（服务已注册但没起来）。</summary>
    public bool NeedsRestart =>
        State == DriverState.Installed &&
        Services.Values.Any(s => s == DriverServiceState.Stopped);
}
