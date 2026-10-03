using System.Management;
using System.Security.Cryptography;
using System.Security.Principal;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>
/// 驱动状态检测：读注册表 + 查服务，判定 USBip / HidHide / ViGEmBus 是否就绪。
///
/// <para>
/// <b>为什么是 C# 而不是继续用 PowerShell：</b>上游把检测/安装逻辑写在
/// <c>install-usbip.ps1</c> 与 Inno <c>ApexSenseBridge.iss</c> 里，用户只能靠安装器一次性完成，
/// 装完之后想「看看驱动现在什么状态」是做不到的。DIA 把它做成可随时调用的检测服务。
/// </para>
///
/// <para>
/// <b>只读保证：</b>本服务**只读**注册表与 WMI，不写任何键、不改服务状态。
/// 安装动作在 <c>DriverInstallerService</c>，且必须提权 + 用户显式确认。
/// </para>
///
/// <para>
/// <b>64 位视图：</b>卸载项必须在 <see cref="RegistryView.Registry64"/> 下读 ——
/// USBip 是 64 位安装器，注册在 64 位视图；从 32 位视图读会「看起来没装」，从而误触发重装。
/// </para>
/// </summary>
public sealed class DriverDetectionService
{
    private const string LogFileName = "dsinapex_drivers.log";

    private const string UninstallSubKey = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
    private const string ServicesSubKey = @"SYSTEM\CurrentControlSet\Services\";

    private readonly DriverManifestService _manifest;

    public DriverDetectionService(DriverManifestService manifest) => _manifest = manifest;

    /// <summary>
    /// 捆绑安装器所在目录。默认按 App 输出布局推断（<c>&lt;out&gt;\Prerequisites</c>）；可由宿主覆盖。
    /// </summary>
    public string PrerequisitesDirectory { get; set; } =
        Path.Combine(AppContext.BaseDirectory, "Prerequisites");

    /// <summary>当前进程是否以管理员身份运行（决定能否直接执行安装）。</summary>
    public static bool IsElevated
    {
        get
        {
            try
            {
                using WindowsIdentity identity = WindowsIdentity.GetCurrent();
                return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>检测清单中的全部驱动。</summary>
    public IReadOnlyList<DriverStatus> DetectAll()
    {
        var results = new List<DriverStatus>();
        foreach (DriverInfo info in _manifest.Load())
        {
            results.Add(Detect(info));
        }
        return results;
    }

    /// <summary>检测单个驱动。</summary>
    public DriverStatus Detect(DriverInfo info)
    {
        var status = new DriverStatus { Info = info };

        // ── 1. 卸载注册项（64 位视图）──
        if (!string.IsNullOrWhiteSpace(info.ProductCode))
        {
            try
            {
                using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
                using RegistryKey? uninstallKey = baseKey.OpenSubKey(UninstallSubKey + info.ProductCode);
                if (uninstallKey is not null)
                {
                    status.UninstallEntryPresent = true;
                    status.InstalledVersion = (uninstallKey.GetValue("DisplayVersion") as string)?.Trim();
                }
            }
            catch (Exception ex)
            {
                status.Detail = "读取卸载注册项失败：" + AppLog.Describe(ex);
            }
        }

        // ── 2. 各服务 ──
        foreach (string service in info.Services)
        {
            status.Services[service] = QueryServiceState(service);
        }

        // ── 3. 捆绑安装器存在性与完整性 ──
        if (!string.IsNullOrWhiteSpace(info.Installer))
        {
            string candidate = Path.Combine(PrerequisitesDirectory, info.Installer);
            status.InstallerPath = candidate;
            status.InstallerPresent = File.Exists(candidate);

            if (status.InstallerPresent && !string.IsNullOrWhiteSpace(info.Sha256))
            {
                status.InstallerVerified = VerifySha256(candidate, info.Sha256);
            }
        }

        // ── 4. 综合判定 ──
        status.State = Judge(info, status);

        AppLog.Info(LogFileName,
            $"检测 {info.Id}：状态={status.State} 已装版本={(status.InstalledVersion ?? "(无)")} " +
            $"卸载项={status.UninstallEntryPresent} 服务=[{DescribeServices(status)}] " +
            $"安装器={(status.InstallerPresent ? (status.InstallerVerified ? "已校验" : "未校验") : "缺失")}");

        return status;
    }

    /// <summary>校验文件 SHA-256 是否与期望一致。</summary>
    public static bool VerifySha256(string filePath, string expectedHex)
    {
        if (string.IsNullOrWhiteSpace(expectedHex)) return false;

        try
        {
            using FileStream stream = File.OpenRead(filePath);
            byte[] hash = SHA256.HashData(stream);
            string actual = Convert.ToHexString(hash);
            return string.Equals(actual, expectedHex.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 查单个服务的状态。
    ///
    /// <para>
    /// <b>⚠️ 关键坑（2026-10-03 实测踩到）：内核驱动服务 ≠ Win32 服务。</b>
    /// <c>usbip2_ude</c> / <c>usbip2_filter</c> / <c>HidHide</c> / <c>ViGEmBus</c> 全都是**内核驱动**，
    /// 它们在 <c>Win32_SystemDriver</c> 里，**不在** <c>Win32_Service</c> 里。
    /// 只查 <c>Win32_Service</c> 会拿到空结果，从而把「驱动正在运行」误报成 <c>Stopped</c>，
    /// 进而把整个驱动的状态从 <c>Running</c> 错降级为 <c>Installed</c>。
    /// </para>
    ///
    /// <para>
    /// 因此这里两个类都查：先 <c>Win32_SystemDriver</c>（内核驱动），再 <c>Win32_Service</c>（用户态服务）。
    /// </para>
    /// </summary>
    private static DriverServiceState QueryServiceState(string serviceName)
    {
        bool keyExists;
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? key = baseKey.OpenSubKey(ServicesSubKey + serviceName);
            keyExists = key is not null;
        }
        catch
        {
            return DriverServiceState.Unknown;
        }

        if (!keyExists) return DriverServiceState.Missing;

        // 内核驱动优先；再退到用户态服务
        DriverServiceState? state =
            QueryWmiState("Win32_SystemDriver", serviceName) ??
            QueryWmiState("Win32_Service", serviceName);

        // 服务键在，但 WMI 两边都查不到实例 —— 多半是刚注册、尚未重启生效
        return state ?? DriverServiceState.Stopped;
    }

    /// <summary>查指定 WMI 类里该服务的状态；查不到返回 <c>null</c>。</summary>
    private static DriverServiceState? QueryWmiState(string wmiClass, string serviceName)
    {
        try
        {
            var query = new ObjectQuery($"SELECT State FROM {wmiClass} WHERE Name = '{serviceName}'");
            using var searcher = new ManagementObjectSearcher(query);

            foreach (ManagementBaseObject item in searcher.Get())
            {
                string? state = item["State"] as string;
                if (string.Equals(state, "Running", StringComparison.OrdinalIgnoreCase))
                {
                    return DriverServiceState.Running;
                }
                if (!string.IsNullOrWhiteSpace(state))
                {
                    return DriverServiceState.Stopped;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"WMI 查询 {wmiClass}.{serviceName} 失败：{AppLog.Describe(ex)}");
        }

        return null;
    }

    private static DriverState Judge(DriverInfo info, DriverStatus status)
    {
        bool anyServicePresent = status.Services.Values.Any(s => s != DriverServiceState.Missing);
        bool allServicesPresent = status.Services.Count > 0 &&
                                  status.Services.Values.All(s => s != DriverServiceState.Missing);
        bool allServicesRunning = status.Services.Count > 0 &&
                                  status.Services.Values.All(s => s == DriverServiceState.Running);

        // 没有卸载项定义的驱动（如可选组件）：只能靠服务判定
        if (string.IsNullOrWhiteSpace(info.ProductCode))
        {
            if (!anyServicePresent)
            {
                return info.Optional ? DriverState.OptionalMissing : DriverState.NotInstalled;
            }
            return allServicesRunning ? DriverState.Running : DriverState.Installed;
        }

        if (!status.UninstallEntryPresent && !anyServicePresent)
        {
            return info.Optional ? DriverState.OptionalMissing : DriverState.NotInstalled;
        }

        // 有服务残留但没有卸载项 → 装了一半（上游 .iss 对此直接拒绝安装并要求人工处理）
        if (!status.UninstallEntryPresent)
        {
            return DriverState.Partial;
        }

        // 有卸载项但服务不全 → 同样是半成品
        if (!allServicesPresent)
        {
            return DriverState.Partial;
        }

        if (!string.IsNullOrWhiteSpace(info.Version))
        {
            string installed = status.InstalledVersion ?? string.Empty;
            if (!string.Equals(installed, info.Version, StringComparison.OrdinalIgnoreCase))
            {
                return DriverState.Outdated;
            }
        }

        return allServicesRunning ? DriverState.Running : DriverState.Installed;
    }

    private static string DescribeServices(DriverStatus status)
        => status.Services.Count == 0
            ? "无"
            : string.Join(", ", status.Services.Select(kv => $"{kv.Key}={kv.Value}"));
}
