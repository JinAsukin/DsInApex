using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text;
using System.Text.RegularExpressions;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>
/// PnP 拓扑收集器：把"接在机器上的这个手柄到底由哪些设备实例组成"讲清楚。
///
/// <para>
/// <b>为什么要重写而不是照搬 PS 脚本的 <c>Get-PnpDevice</c>：</b>
/// 上游依赖 <c>Get-PnpDevice</c> / <c>Get-PnpDeviceProperty</c> 这两个
/// <b>CIM cmdlet</b>，它们在 C# 里没有直接等价物。本实现用两条可达路径复刻：
/// </para>
///
/// <list type="number">
/// <item><b>WMI <c>Win32_PnPEntity</c></b> —— 设备清单、状态、错误码、服务；
/// 实测可列出 APEX 4 的全部 6 个实例（MI_00–MI_03 + USB 复合设备 + USB 输入设备）。</item>
/// <item><b>注册表 <c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;\ContainerID</c></b> ——
/// 容器 ID。实测<b>普通用户可读</b>，且 6 个实例返回同一个容器 GUID，
/// 这正是把"一个物理手柄的多个接口"聚成一组的依据。</item>
/// </list>
///
/// <para>
/// <b>容器聚类只做一层，绝不递归父子关系</b>（沿袭上游的隐私设计）：
/// 一旦顺着父设备往上爬就会撞到 USB 主控制器，把整机硬件拖进报告里。
/// </para>
///
/// <para>
/// 主路径失败时按上游同款三级降级：<c>pnputil</c> 文本 → <c>reg query</c> → 记录失败。
/// </para>
/// </summary>
public sealed partial class PnpTopologyCollector
{
    private const string LogFileName = "dsinapex_diagnostics.log";

    /// <summary>判定"这是 Flydigi / APEX 4 相关设备"的标记。</summary>
    private const string SeedMarker = @"(?i)(flydigi|apex[ _-]*4|vid_04b4&pid_2412|vid_045e&pid_028e)";

    /// <summary>
    /// 容器 ID 占位值：Windows 给"无法确定物理容器"的设备发这些固定 GUID，
    /// 把它们当容器用会把互不相关的设备聚到一起。
    /// </summary>
    private const string PlaceholderContainer = "{9F4B56F0-1DF6-11E0-AC64-0800200C9A66}";

    [GeneratedRegex(@"^\{?0{8}-0{4}-0{4}-(0{4}|f{4})-(0{12}|f{12})\}?$",
        RegexOptions.IgnoreCase)]
    private static partial Regex PlaceholderContainerRegex();

    /// <summary>收集 PnP 拓扑，并落盘 <c>pnp-devices.json</c> / <c>pnp-devices.txt</c>。</summary>
    public PnpDiagnostics Collect(string workingDirectory)
    {
        try
        {
            List<RawDevice> all = QueryPresentDevices();
            if (all.Count == 0)
            {
                throw new InvalidOperationException("Win32_PnPEntity 没有返回任何设备。");
            }

            // ── 1. 找种子设备 ──
            var containerCache = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
            List<RawDevice> seeds = [.. all.Where(IsSeed)];

            // ── 2. 收集种子的容器 ──
            var seedContainers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var relatedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (RawDevice seed in seeds)
            {
                relatedIds.Add(seed.InstanceId);

                string? container = GetContainerId(seed.InstanceId, containerCache);
                if (!string.IsNullOrWhiteSpace(container) && !IsPlaceholderContainer(container))
                {
                    seedContainers.Add(container);
                }
            }

            // ── 3. 同容器即同属一个物理设备（只聚一层） ──
            foreach (RawDevice device in all)
            {
                string? container = GetContainerId(device.InstanceId, containerCache);
                if (!string.IsNullOrWhiteSpace(container) && seedContainers.Contains(container))
                {
                    relatedIds.Add(device.InstanceId);
                }
            }

            // ── 4. 组装输出（按实例 ID 排序，保证可复现） ──
            var normalized = new List<PnpDeviceEntry>();
            foreach (RawDevice device in all
                .Where(d => relatedIds.Contains(d.InstanceId))
                .OrderBy(d => d.InstanceId, StringComparer.OrdinalIgnoreCase))
            {
                var properties = new List<PnpProperty>();

                void AddProperty(string key, string? value)
                {
                    if (!string.IsNullOrWhiteSpace(value))
                    {
                        properties.Add(new PnpProperty { Key = key, Value = value });
                    }
                }

                AddProperty("DEVPKEY_Device_ContainerId", GetContainerId(device.InstanceId, containerCache));
                AddProperty("DEVPKEY_Device_BusReportedDeviceDesc", ReadRegistryString(device.InstanceId, "BusReportedDeviceDesc"));
                AddProperty("DEVPKEY_Device_Manufacturer", string.IsNullOrWhiteSpace(device.Manufacturer)
                    ? ReadRegistryString(device.InstanceId, "Mfg")
                    : device.Manufacturer);
                AddProperty("DEVPKEY_Device_Service", device.Service);
                AddProperty("DEVPKEY_Device_Class", device.PnpClass);
                AddProperty("DEVPKEY_Device_ProblemCode",
                    device.ConfigManagerErrorCode == 0
                        ? "0"
                        : device.ConfigManagerErrorCode.ToString(CultureInfo.InvariantCulture));
                AddProperty("DEVPKEY_Device_DeviceDesc", device.Name);

                normalized.Add(new PnpDeviceEntry
                {
                    InstanceId = device.InstanceId,
                    Status = device.Status,
                    DeviceClass = device.PnpClass,
                    FriendlyName = device.Name,
                    Problem = device.ConfigManagerErrorCode.ToString(CultureInfo.InvariantCulture),
                    ContainerId = GetContainerId(device.InstanceId, containerCache),
                    Parent = DeriveParent(device.InstanceId),
                    Properties = properties,
                });
            }

            var result = new PnpDiagnostics
            {
                Available = true,
                SeedCount = seeds.Count,
                RelatedCount = normalized.Count,
                Devices = normalized,
            };

            File.WriteAllText(
                Path.Combine(workingDirectory, "pnp-devices.json"),
                DiagnosticJson.Serialize(result, indented: true),
                new UTF8Encoding(true));

            File.WriteAllText(
                Path.Combine(workingDirectory, "pnp-devices.txt"),
                BuildPnpText(result),
                new UTF8Encoding(true));

            AppLog.Info(LogFileName, $"PnP 拓扑：种子 {seeds.Count} 个，聚类后相关设备 {normalized.Count} 个");
            return result;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"PnP 收集失败，降级到 pnputil / 注册表：{AppLog.Describe(ex)}");

            var failed = new PnpDiagnostics { Available = false, Error = ex.Message };
            try
            {
                File.WriteAllText(
                    Path.Combine(workingDirectory, "pnp-devices.json"),
                    DiagnosticJson.Serialize(failed, indented: true),
                    new UTF8Encoding(true));
                File.WriteAllText(
                    Path.Combine(workingDirectory, "pnp-devices.txt"),
                    "PnP 详细收集失败：" + ex.Message + Environment.NewLine,
                    new UTF8Encoding(true));
            }
            catch
            {
                // 落盘失败不再向上抛 —— 收集器要继续跑完其余步骤
            }

            return failed;
        }
    }

    // ────────────────────────── WMI 查询 ──────────────────────────

    private sealed record RawDevice(
        string InstanceId,
        string Name,
        string Status,
        string PnpClass,
        int ConfigManagerErrorCode,
        string Service,
        string Manufacturer);

    /// <summary>
    /// 枚举当前存在的 PnP 设备。
    /// <para>
    /// <c>Win32_PnPEntity</c> 本身就只返回"当前存在"的设备，
    /// 无需（也没有）<c>-PresentOnly</c> 这样的开关。
    /// </para>
    /// </summary>
    private static List<RawDevice> QueryPresentDevices()
    {
        var devices = new List<RawDevice>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT PNPDeviceID, Name, Status, PNPClass, ConfigManagerErrorCode, Service, Manufacturer " +
            "FROM Win32_PnPEntity");

        foreach (ManagementBaseObject item in searcher.Get())
        {
            using var obj = item as ManagementObject;
            if (obj is null) continue;

            string? instanceId = obj["PNPDeviceID"] as string;
            if (string.IsNullOrWhiteSpace(instanceId)) continue;

            devices.Add(new RawDevice(
                InstanceId: instanceId,
                Name: obj["Name"] as string ?? string.Empty,
                Status: obj["Status"] as string ?? string.Empty,
                PnpClass: obj["PNPClass"] as string ?? string.Empty,
                ConfigManagerErrorCode: ToInt(obj["ConfigManagerErrorCode"]),
                Service: obj["Service"] as string ?? string.Empty,
                Manufacturer: obj["Manufacturer"] as string ?? string.Empty));
        }

        return devices;
    }

    private static int ToInt(object? value)
    {
        try
        {
            return value is null ? 0 : Convert.ToInt32(value, CultureInfo.InvariantCulture);
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>判断设备是否为 Flydigi / APEX 4 相关种子。</summary>
    private static bool IsSeed(RawDevice device)
    {
        // 判据与上游一致：实例 ID / 名称 / 类 / 总线描述 / 厂商 里任一命中即算种子
        string text = string.Join('\n',
            device.InstanceId,
            device.Name,
            device.PnpClass,
            ReadRegistryString(device.InstanceId, "BusReportedDeviceDesc") ?? string.Empty,
            device.Manufacturer);

        return Regex.IsMatch(text, SeedMarker);
    }

    // ────────────────────────── 注册表读取 ──────────────────────────

    /// <summary>
    /// 读设备的容器 ID。
    ///
    /// <para>
    /// 实测（2026-10-03）：<c>HKLM\SYSTEM\CurrentControlSet\Enum\&lt;instance&gt;</c>
    /// 下的 <c>ContainerID</c> 值普通用户可读，APEX 4 的 6 个实例返回同一个 GUID。
    /// </para>
    ///
    /// <para>读不到不是错误 —— 返回 <c>null</c>，调用方跳过容器聚类。</para>
    /// </summary>
    private static string? GetContainerId(string instanceId, Dictionary<string, string?> cache)
    {
        if (cache.TryGetValue(instanceId, out string? cached)) return cached;

        string? value = ReadRegistryString(instanceId, "ContainerID");
        cache[instanceId] = value;
        return value;
    }

    /// <summary>读 <c>Enum\&lt;instance&gt;\&lt;valueName&gt;</c>。失败返回 <c>null</c>。</summary>
    private static string? ReadRegistryString(string instanceId, string valueName)
    {
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + instanceId);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            // 权限不足或键不存在都按"读不到"处理
            return null;
        }
    }

    private static bool IsPlaceholderContainer(string container)
        => container.Equals(PlaceholderContainer, StringComparison.OrdinalIgnoreCase)
           || PlaceholderContainerRegex().IsMatch(container);

    /// <summary>
    /// 从上一条实例 ID 推导父设备（去掉最后一段）。
    /// <para>
    /// 例如 <c>USB\VID_04B4&amp;PID_2412&amp;MI_03\6&amp;C88ABB4&amp;2&amp;0003</c>
    /// 的父是 <c>USB\VID_04B4&amp;PID_2412&amp;MI_03</c>。
    /// 这只是拓扑提示，不参与容器聚类。
    /// </para>
    /// </summary>
    private static string? DeriveParent(string instanceId)
    {
        int lastSlash = instanceId.LastIndexOf('\\');
        return lastSlash > 0 ? instanceId[..lastSlash] : null;
    }

    // ────────────────────────── 文本输出 ──────────────────────────

    private static string BuildPnpText(PnpDiagnostics result)
    {
        var text = new StringBuilder();
        text.AppendLine("APEX 4 / Flydigi PnP devices");
        text.AppendLine($"Seeds: {result.SeedCount}; related devices: {result.RelatedCount}");

        foreach (PnpDeviceEntry device in result.Devices)
        {
            text.AppendLine();
            text.AppendLine($"[{device.FriendlyName}]");
            text.AppendLine($"  Instance:  {device.InstanceId}");
            text.AppendLine($"  Class:     {device.DeviceClass}");
            text.AppendLine($"  Status:    {device.Status}");
            text.AppendLine($"  Container: {device.ContainerId ?? "(unknown)"}");
            text.AppendLine($"  Parent:    {device.Parent ?? "(unknown)"}");

            foreach (PnpProperty property in device.Properties)
            {
                string value = property.Value switch
                {
                    string[] array => string.Join("; ", array),
                    string single => single,
                    null => string.Empty,
                    _ => property.Value.ToString() ?? string.Empty,
                };
                text.AppendLine($"  {property.Key}: {value}");
            }
        }

        return text.ToString();
    }

    // ══════════════════════════ 降级路径 ══════════════════════════
    //
    //   WMI 路径在实测中可用，但上游刻意保留了两级文本回退 ——
    //   PnP provider 在某些系统上会整体失效（例如 WBEM 仓库损坏）。
    //   回退产出的价值在于"设备实例 ID 与 VID/PID"这类纯 ASCII 证据，
    //   即使中文设备名因代码页不匹配而乱码，也不影响排障。

    /// <summary>
    /// pnputil 回退：枚举在线设备，只保留命中 Flydigi / APEX 4 标记的块。
    /// </summary>
    /// <returns>是否找到至少一个相关设备。</returns>
    public bool CollectPnputilFallback(string workingDirectory)
    {
        string outputPath = Path.Combine(workingDirectory, "pnputil-relevant.txt");
        string exePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "pnputil.exe");

        if (!File.Exists(exePath))
        {
            WriteTextFile(outputPath, "pnputil.exe 不可用。" + Environment.NewLine);
            return false;
        }

        try
        {
            (int exitCode, string stdout, string stderr) =
                RunCapture(exePath, "/enum-devices /connected /deviceids /drivers", timeoutMs: 60_000);

            // pnputil 以空行分隔设备块 —— 与上游的 "(?:\r?\n){2,}" 切分一致
            string[] blocks = Regex.Split(stdout, @"(?:\r?\n){2,}");
            string[] relevant = [.. blocks.Where(block => Regex.IsMatch(block, SeedMarker))];

            var text = new StringBuilder();
            text.AppendLine("只保留命中 Flydigi / APEX 4 标记的设备块。");
            text.AppendLine($"pnputil 退出码：{exitCode}");
            text.AppendLine();
            text.AppendLine(string.Join(Environment.NewLine + Environment.NewLine, relevant));

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                text.AppendLine();
                text.AppendLine("── stderr ──");
                text.AppendLine(stderr);
            }

            WriteTextFile(outputPath, text.ToString());

            AppLog.Info(LogFileName, $"pnputil 回退：命中 {relevant.Length} 个设备块");
            return relevant.Length > 0;
        }
        catch (Exception ex)
        {
            WriteTextFile(outputPath, "pnputil 回退失败：" + AppLog.Describe(ex) + Environment.NewLine);
            AppLog.Warn(LogFileName, $"pnputil 回退失败：{AppLog.Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 注册表回退：直接在 <c>Enum\USB</c> 与 <c>Enum\HID</c> 下按关键字搜索。
    /// </summary>
    public void CollectRegistryFallback(string workingDirectory)
    {
        string outputPath = Path.Combine(workingDirectory, "registry-relevant.txt");
        string regPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System), "reg.exe");

        if (!File.Exists(regPath))
        {
            WriteTextFile(outputPath, "reg.exe 不可用。" + Environment.NewLine);
            return;
        }

        (string Root, string Filter)[] queries =
        [
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\USB", "APEX4"),
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\USB", "Flydigi"),
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\USB", "VID_04B4&PID_2412"),
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\USB", "VID_045E&PID_028E"),
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\HID", "APEX4"),
            (@"HKLM\SYSTEM\CurrentControlSet\Enum\HID", "Flydigi"),
        ];

        var lines = new List<string>();
        foreach ((string root, string filter) in queries)
        {
            lines.Add($"===== {root} /f {filter} =====");
            try
            {
                // ⚠️ filter 里含 & —— 必须整体加引号，否则会被 cmd 解析成命令分隔符
                (int _, string stdout, string stderr) =
                    RunCapture(regPath, $"query \"{root}\" /s /f \"{filter}\"", timeoutMs: 30_000);

                lines.AddRange(stdout
                    .Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => line.TrimEnd('\r')));

                if (!string.IsNullOrWhiteSpace(stderr))
                {
                    lines.Add(stderr.Trim());
                }
            }
            catch (Exception ex)
            {
                lines.Add(AppLog.Describe(ex));
            }

            lines.Add(string.Empty);
        }

        WriteTextFile(outputPath, string.Join(Environment.NewLine, lines));
    }

    /// <summary>
    /// 执行命令行工具并捕获输出。
    ///
    /// <para>
    /// 编码策略：优先跟随控制台代码页（中文系统是 GBK），
    /// 取不到时退回 UTF-8。即便解码不完美，实例 ID 这类关键证据是纯 ASCII。
    /// </para>
    /// </summary>
    private static (int ExitCode, string StdOut, string StdErr) RunCapture(
        string exePath, string arguments, int timeoutMs)
    {
        Encoding encoding = ResolveConsoleEncoding();

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding,
            StandardErrorEncoding = encoding,
        };

        using var process = Process.Start(psi);
        if (process is null) return (-1, string.Empty, "进程无法启动。");

        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();

        if (!process.WaitForExit(timeoutMs))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* 已退出 */ }
            return (-1, string.Empty, $"命令超时（{timeoutMs} ms）。");
        }

        // 无参 WaitForExit 等待异步输出排空 —— 少这一步会丢尾部内容
        process.WaitForExit();

        return (process.ExitCode, stdoutTask.GetAwaiter().GetResult(), stderrTask.GetAwaiter().GetResult());
    }

    private static Encoding ResolveConsoleEncoding()
    {
        try
        {
            Encoding console = Console.OutputEncoding;
            if (console.CodePage is not (65001 or 1200))
            {
                return console;
            }
        }
        catch
        {
            // GUI 进程可能没有控制台，忽略
        }

        try
        {
            return Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage);
        }
        catch
        {
            return Encoding.UTF8;
        }
    }

    private static void WriteTextFile(string path, string content)
    {
        try
        {
            File.WriteAllText(path, content, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"写入 {Path.GetFileName(path)} 失败：{AppLog.Describe(ex)}");
        }
    }
}
