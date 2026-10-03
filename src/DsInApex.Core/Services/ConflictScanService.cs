using System.Diagnostics;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 冲突软件扫描：找出"可能抢走或隐藏手柄"的程序与驱动服务。
///
/// <para>
/// <b>为什么这条不是可有可无的：</b>2026-10-03 实测中，<c>input-status</c> 首跑失败并
/// 明确报出 "use USB/dongle DInput mode and <b>close Flydigi Space Station</b> before retrying"，
/// 而当时 <c>SpaceStationService</c> 确实在运行。也就是说，
/// "手柄连了但命令读不到"最常见的根因就是这类程序在轮询同一个 HID 接口。
/// </para>
///
/// <para>
/// <b>隐私边界（沿袭上游）：</b>进程只记录<b>名称</b>，
/// 不记录路径、命令行、窗口标题；服务只记录名称 / 状态 / 启动方式。
/// </para>
/// </summary>
public sealed class ConflictScanService
{
    private const string LogFileName = "dsinapex_diagnostics.log";

    /// <summary>可能抢占手柄的进程名标记。</summary>
    private const string ProcessPattern = @"(?i)(flydigi|space.?station|steam|dsx|dualsensex|rewasd|hidhide|apexsense|viiper)";

    /// <summary>与本项目相关的驱动 / 服务名标记。</summary>
    private const string ServicePattern = @"(?i)(hidhide|usbip|vigem|vhf|flydigi|apexsense)";

    /// <summary>
    /// 真正需要警告用户的进程（比收集范围窄 —— Steam 本身不算冲突，
    /// 但 Steam 的输入层可能在后台接管手柄，因此仍在收集范围内）。
    /// </summary>
    private const string ConflictPattern = @"(?i)(flydigi|space.?station|steam|dsx|dualsensex|rewasd)";

    /// <summary>收集进程名清单并落盘。</summary>
    public IReadOnlyList<ConflictProcessInfo> CollectProcesses(string workingDirectory)
    {
        var results = new List<ConflictProcessInfo>();

        try
        {
            foreach (Process process in Process.GetProcesses())
            {
                using (process)
                {
                    string name;
                    try
                    {
                        name = process.ProcessName;
                    }
                    catch
                    {
                        continue;   // 受保护进程读不到名字，跳过
                    }

                    if (Regex.IsMatch(name, ProcessPattern))
                    {
                        results.Add(new ConflictProcessInfo { ProcessName = name, Id = process.Id });
                    }
                }
            }

            results.Sort(static (left, right) =>
                string.Compare(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"进程扫描失败：{AppLog.Describe(ex)}");
        }

        WriteJson(workingDirectory, "possibly-conflicting-processes.json", results);
        return results;
    }

    /// <summary>
    /// 收集相关驱动 / 服务状态并落盘。
    ///
    /// <para>
    /// ⚠️ <b>两个 WMI 类都要查</b>：<c>HidHide</c> / <c>usbip2_ude</c> 等是
    /// <b>内核驱动</b>，只出现在 <c>Win32_SystemDriver</c>；
    /// 只查 <c>Win32_Service</c> 会得到空集合，从而误报"没在运行"（P4 已踩过）。
    /// </para>
    /// </summary>
    public IReadOnlyList<RelevantServiceInfo> CollectServices(string workingDirectory)
    {
        var results = new List<RelevantServiceInfo>();

        // 内核驱动优先
        QueryServiceClass("Win32_SystemDriver", results);
        // 再补用户态服务（同名项不重复记录）
        var seen = new HashSet<string>(results.Select(r => r.Name), StringComparer.OrdinalIgnoreCase);
        var userServices = new List<RelevantServiceInfo>();
        QueryServiceClass("Win32_Service", userServices);
        results.AddRange(userServices.Where(service => seen.Add(service.Name)));

        results.Sort(static (left, right) =>
            string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase));

        WriteJson(workingDirectory, "relevant-services.json", results);
        return results;
    }

    private static void QueryServiceClass(string wmiClass, List<RelevantServiceInfo> sink)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT Name, DisplayName, State, StartMode FROM {wmiClass}");

            foreach (ManagementBaseObject item in searcher.Get())
            {
                using var obj = item as ManagementObject;
                if (obj is null) continue;

                string name = obj["Name"] as string ?? string.Empty;
                string displayName = obj["DisplayName"] as string ?? string.Empty;

                if (!Regex.IsMatch(name + " " + displayName, ServicePattern)) continue;

                sink.Add(new RelevantServiceInfo
                {
                    Name = name,
                    DisplayName = displayName,
                    Status = obj["State"] as string ?? string.Empty,
                    StartType = obj["StartMode"] as string ?? string.Empty,
                    Source = wmiClass,
                });
            }
        }
        catch (Exception ex)
        {
            // WMI 类不可用不致命 —— 另一个类仍然提供大部分信息
            AppLog.Warn(LogFileName, $"{wmiClass} 查询失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>从进程清单里挑出需要警告用户的那些。</summary>
    public static IReadOnlyList<string> FindConflicts(IReadOnlyList<ConflictProcessInfo> processes)
        => [.. processes
            .Select(p => p.ProcessName)
            .Where(name => Regex.IsMatch(name, ConflictPattern))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)];

    private static void WriteJson<T>(string workingDirectory, string fileName, T payload)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(workingDirectory, fileName),
                DiagnosticJson.Serialize(payload, indented: true),
                new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"写入 {fileName} 失败：{AppLog.Describe(ex)}");
        }
    }
}
