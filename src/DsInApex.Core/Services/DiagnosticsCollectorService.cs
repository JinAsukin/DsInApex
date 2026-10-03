using System.Diagnostics;
using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using DsInApex.Core.Interop;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>诊断收集的选项。</summary>
/// <param name="OutputDirectory">ZIP 输出目录；为空则用桌面。</param>
/// <param name="InputSampleSeconds">XInput 采样时长（1–30 秒）。</param>
/// <param name="SkipInputSample">跳过交互式采样（仍会枚举槽位）。</param>
/// <param name="KeepWorkingDirectory">保留未压缩的中间目录，便于排查。</param>
/// <param name="BridgeExecutable">显式指定引擎路径；为空则用 <see cref="EngineLocator"/>。</param>
public sealed record DiagnosticCollectionOptions(
    string? OutputDirectory = null,
    int InputSampleSeconds = 8,
    bool SkipInputSample = false,
    bool KeepWorkingDirectory = false,
    string? BridgeExecutable = null);

/// <summary>
/// 诊断收集服务 —— 上游 <c>Collect-Apex4-Diagnostics.ps1</c>（1127 行）的 C# 重写。
///
/// <para><b>为什么必须重写而不是直接调那份脚本：</b></para>
/// <list type="number">
/// <item>脚本用 <c>Add-Type</c> 在运行时编译 XInput 探针，
/// 而<b>本机安全策略禁用 <c>Add-Type</c></b> → 该脚本在本机必然在 XInput 环节失败；</item>
/// <item>脚本依赖 PowerShell 与 <c>Get-PnpDevice</c> 等 CIM cmdlet，DIA 是自包含 GUI 程序，
/// 不该要求用户环境里有这些；</item>
/// <item>输出的 README 面向上游英文社区，DIA 需要中文报告。</item>
/// </list>
///
/// <para><b>保留了什么：</b>7 步流程、三级降级链、JSON 字段名、
/// 以及最关键的"机型 / 连接模式判定"规则（识别 APEX 4 在 XInput 模式下
/// 伪装成 <c>045E:028E</c> + "Flydigi VADER3" 的陷阱）。</para>
///
/// <para><b>安全性（与上游同等承诺）：</b>全程只读 —— 不装驱动、不改手柄设置、
/// 不使能/禁用设备、不写厂商或输出报告、不驱动马达或自适应扳机。
/// 唯一的"写"是往临时目录写文件和自己打包 ZIP。</para>
/// </summary>
public sealed partial class DiagnosticsCollectorService
{
    private const string LogFileName = "dsinapex_diagnostics.log";

    /// <summary>DIA 收集器版本（对应上游脚本里的 <c>$script:CollectorVersion</c>）。</summary>
    private const string CollectorVersion = "1.5.0-dia";

    private readonly PnpTopologyCollector _pnp = new();
    private readonly ConflictScanService _conflicts = new();

    /// <summary>引擎标签文本（用于 README 与文件名，与上游 "Apex4" 区分开）。</summary>
    private const string ArchivePrefix = "DsInApex-Diagnostics";

    // ─────────────────────── 机型判定用的正则 ───────────────────────

    [GeneratedRegex(@"(?i)(flydigi|apex|vader)")]
    private static partial Regex FlydigiHidRegex();

    [GeneratedRegex(@"(?i)vid_(045e&pid_028e|04b4&pid_2412)")]
    private static partial Regex KnownVidPidRegex();

    [GeneratedRegex(@"(?i)vid_045e&pid_028e")]
    private static partial Regex XInput045ERegex();

    [GeneratedRegex(@"(?i)(flydigi|apex|vader|direwolf)")]
    private static partial Regex FlydigiLabelRegex();

    [GeneratedRegex(@"(?i)(vid_045e&pid_028e|vid_04b4&pid_2412|vid&0304b4_pid&2412)")]
    private static partial Regex KnownInstanceRegex();

    [GeneratedRegex(@"(?i)APEX[ _-]*4")]
    private static partial Regex Apex4Regex();

    [GeneratedRegex(@"(?i)^BTH")]
    private static partial Regex BluetoothPrefixRegex();

    [GeneratedRegex(@"(?i)^(USB|HID)\\")]
    private static partial Regex UsbOrHidPrefixRegex();

    [GeneratedRegex(@"(?i)^(USB|HID)\\VID_04B4&PID_2412")]
    private static partial Regex Apex4UsbInstanceRegex();

    [GeneratedRegex(@"(?i)^(USB|HID)\\VID_045E&PID_028E")]
    private static partial Regex XInputUsbInstanceRegex();

    [GeneratedRegex(@"(?i)(VADER|DIREWOLF|APEX[ _-]*(2|3|5|6))")]
    private static partial Regex OtherModelRegex();

    [GeneratedRegex(@"(?i)(DIREWOLF|APEX[ _-]*(2|3|5|6)|VADER[ _-]*(2|4|5))")]
    private static partial Regex OtherModelStrictRegex();

    /// <summary>
    /// 执行一次完整收集。
    /// </summary>
    /// <param name="options">收集选项。</param>
    /// <param name="progress">逐行进度回调（界面实时展示）。</param>
    /// <param name="cancellationToken">取消。</param>
    public Task<DiagnosticCollectionResult> CollectAsync(
        DiagnosticCollectionOptions options,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        // WMI / XInput 采样都是阻塞调用 —— 整体丢到后台线程，
        // 由 IProgress<T> 自行把日志送回 UI 线程。
        return Task.Run(() => CollectCore(options, progress, cancellationToken), cancellationToken);
    }

    private DiagnosticCollectionResult CollectCore(
        DiagnosticCollectionOptions options,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        var log = new List<string>();
        var warnings = new List<string>();
        var steps = new List<DiagnosticStep>();

        void Report(string line)
        {
            log.Add(line);
            progress?.Report(line);
        }

        void Warn(string message)
        {
            warnings.Add(message);
            Report("[警告] " + message);
            AppLog.Warn(LogFileName, message);
        }

        void Step(string name, bool success, string detail)
            => steps.Add(new DiagnosticStep { Name = name, Success = success, Detail = detail });

        // ── 准备工作目录 ──
        string outputDirectory = ResolveOutputDirectory(options.OutputDirectory);
        string timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string shortId = Guid.NewGuid().ToString("N")[..8];
        string workingDirectory = Path.Combine(
            Path.GetTempPath(), $"{ArchivePrefix}-{timestamp}-{shortId}");
        string archivePath = Path.Combine(outputDirectory, $"{ArchivePrefix}-{timestamp}-{shortId}.zip");

        try
        {
            Directory.CreateDirectory(workingDirectory);
        }
        catch (Exception ex)
        {
            string message = "无法创建临时工作目录：" + AppLog.Describe(ex);
            AppLog.Warn(LogFileName, message);
            return new DiagnosticCollectionResult
            {
                Success = false, Error = message, WorkingDirectory = workingDirectory,
                LogLines = log,
            };
        }

        Report($"Ds in Apex 诊断收集器 v{CollectorVersion}");
        Report("只读收集：不安装驱动、不修改手柄设置、不发送任何效果或输出报文。");
        Report($"工作目录：{workingDirectory}");
        Report(string.Empty);

        try
        {
            // ══════════ [1/7] 系统信息 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[1/7] 收集系统信息…");
            DiagnosticSystemInfo system = CollectSystemInfo(timestamp);

            // 采集时间用统一值，避免各文件时间戳不一致
            string collectedAtUtc = system.CollectedAtUtc;
            WriteJson(workingDirectory, "system.json", system);
            Step("system", true, "已记录 Windows 版本与运行时信息。");

            // ══════════ [2/7] HID 接口枚举 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[2/7] 枚举 HID 接口…");
            (BridgeExecutableInfo bridge, HidDiagnosticReport? hid) =
                CollectHidInterfaces(workingDirectory, options, warnings, Step);

            // ══════════ [3/7] PnP 拓扑与驱动属性 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[3/7] 收集即插即用拓扑与驱动属性…");
            PnpDiagnostics pnp = _pnp.Collect(workingDirectory);
            bool pnputilFound = _pnp.CollectPnputilFallback(workingDirectory);
            _pnp.CollectRegistryFallback(workingDirectory);

            if (pnp.Available)
            {
                Step("pnp", true, $"命中 {pnp.SeedCount} 个种子设备，聚类后 {pnp.RelatedCount} 个相关设备。");
            }
            else if (pnputilFound)
            {
                Step("pnp", true, "WMI 不可用，但 pnputil 回退找到了相关设备。");
            }
            else
            {
                Step("pnp", false, "没有找到任何匹配的 PnP 设备。");
            }

            // ── 机型判定 ──
            ModelAssessment assessment = AssessModel(hid, pnp);
            WriteJson(workingDirectory, "model-assessment.json", assessment);
            EmitAssessmentWarnings(assessment, Warn);

            // ══════════ [4/7] XInput 枚举与采样 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[4/7] 以只读方式枚举并采样 XInput…");
            XInputDiagnostics xinput = CollectXInput(
                workingDirectory, options, Report, Warn, Step, cancellationToken);

            // ══════════ [5/7] 冲突软件检查 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[5/7] 检查可能抢占或隐藏手柄的软件…");
            IReadOnlyList<ConflictProcessInfo> processes = _conflicts.CollectProcesses(workingDirectory);
            _ = _conflicts.CollectServices(workingDirectory);

            IReadOnlyList<string> conflicts = ConflictScanService.FindConflicts(processes);
            if (conflicts.Count > 0)
            {
                Warn($"检测到可能抢占手柄的程序正在运行：{string.Join(", ", conflicts)}。"
                     + "若检测结果不完整，请关闭它们后重试。");
            }
            else if (processes.Count == 0)
            {
                Report("  未发现可能抢占手柄的进程。");
            }
            else
            {
                Report($"  发现相关进程 {processes.Count} 个，均不属于需要警告的类别。");
            }

            Step("software", true, "已记录相关进程名与桥接驱动服务状态。");

            // ══════════ [6/7] 生成报告 ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[6/7] 生成报告…");

            var summary = new DiagnosticSummary
            {
                CollectorVersion = CollectorVersion,
                CollectedAtUtc = collectedAtUtc,
                Bridge = bridge,
                ModelAssessment = assessment,
                PnpAvailable = pnp.Available,
                PnpSeedCount = pnp.SeedCount,
                PnpRelatedCount = pnp.RelatedCount,
                PnputilMatchFound = pnputilFound,
                XInputAvailable = xinput.Available,
                XInputDeviceCount = xinput.Devices.Count,
                Warnings = warnings,
                Steps = steps,
            };

            // ⚠️ 顺序有讲究：先把「打包」这一步记进 steps、写好 summary.json，
            //    再真正创建 ZIP。
            //    否则 ZIP 里的报告会缺掉"打包"这一步 —— 出现
            //    「压缩包明明存在，报告里却只列了 5 步」的自相矛盾。
            Step("archive", true, "ZIP 已生成。");
            WriteJson(workingDirectory, "summary.json", summary);
            WriteTextFile(workingDirectory, "README.txt", BuildReadme(system, warnings));

            // ══════════ [7/7] 打包 ZIP ══════════
            cancellationToken.ThrowIfCancellationRequested();
            Report("[7/7] 打包 ZIP…");
            CreateArchive(workingDirectory, archivePath);

            Report(string.Empty);
            Report($"✓ 诊断包已生成：{archivePath}");

            if (options.KeepWorkingDirectory)
            {
                Report($"  未压缩文件保留在：{workingDirectory}");
            }
            else
            {
                TryDeleteDirectory(workingDirectory);
            }

            AppLog.Info(LogFileName,
                $"诊断收集完成：警告 {warnings.Count} 条，包体 {archivePath}");

            return new DiagnosticCollectionResult
            {
                Success = true,
                WorkingDirectory = workingDirectory,
                ArchivePath = archivePath,
                Summary = summary,
                LogLines = log,
            };
        }
        catch (OperationCanceledException)
        {
            Report("收集已被用户中止。");
            AppLog.Warn(LogFileName, "诊断收集被用户中止，保留中间目录：" + workingDirectory);
            return new DiagnosticCollectionResult
            {
                Success = false,
                Error = "收集已被中止。",
                WorkingDirectory = workingDirectory,
                LogLines = log,
            };
        }
        catch (Exception ex)
        {
            string message = AppLog.Describe(ex);
            Report("收集失败：" + message);
            Report("中间文件保留在：" + workingDirectory);
            AppLog.Warn(LogFileName, $"诊断收集失败：{message}");
            return new DiagnosticCollectionResult
            {
                Success = false,
                Error = message,
                WorkingDirectory = workingDirectory,
                LogLines = log,
            };
        }
    }

    // ────────────────────────── 各步骤实现 ──────────────────────────

    /// <summary>[1/7] 系统信息。刻意不含计算机名与用户名（隐私）。</summary>
    private static DiagnosticSystemInfo CollectSystemInfo(string timestamp)
    {
        string productName = string.Empty;
        string displayVersion = string.Empty;
        string build = string.Empty;
        string ubr = string.Empty;

        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            productName = key?.GetValue("ProductName") as string ?? string.Empty;
            displayVersion = key?.GetValue("DisplayVersion") as string ?? string.Empty;
            build = key?.GetValue("CurrentBuildNumber") as string ?? string.Empty;
            ubr = key?.GetValue("UBR")?.ToString() ?? string.Empty;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"读取 Windows 版本失败：{AppLog.Describe(ex)}");
        }

        return new DiagnosticSystemInfo
        {
            CollectorVersion = CollectorVersion,
            // 用本地时间串做文件名锚点，UTC 做内容锚点 —— 与上游一致
            CollectedAtUtc = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture),
            WindowsProductName = productName,
            WindowsDisplayVersion = displayVersion,
            WindowsBuild = build,
            WindowsUbr = ubr,
            OperatingSystem64Bit = Environment.Is64BitOperatingSystem,
            Process64Bit = Environment.Is64BitProcess,
            RuntimeVersion = RuntimeInformation.FrameworkDescription,
            Culture = CultureInfo.CurrentCulture.Name,
            Administrator = DriverDetectionService.IsElevated,
        };
    }

    /// <summary>[2/7] 调引擎 <c>diagnose --json</c> 拿 HID 能力。</summary>
    private static (BridgeExecutableInfo Bridge, HidDiagnosticReport? Hid) CollectHidInterfaces(
        string workingDirectory,
        DiagnosticCollectionOptions options,
        List<string> warnings,
        Action<string, bool, string> step)
    {
        string enginePath = string.IsNullOrWhiteSpace(options.BridgeExecutable)
            ? EngineLocator.ResolveEngine()
            : options.BridgeExecutable;

        var bridgeInfo = new BridgeExecutableInfo
        {
            Available = !string.IsNullOrWhiteSpace(enginePath) && File.Exists(enginePath),
        };

        string hidJsonPath = Path.Combine(workingDirectory, "hid-relevant.json");
        string hidErrPath = Path.Combine(workingDirectory, "hid-relevant.stderr.txt");

        if (!bridgeInfo.Available)
        {
            WriteTextFile(workingDirectory, "hid-relevant.json",
                "{\n  \"count\": 0,\n  \"devices\": []\n}\n");
            WriteTextFile(workingDirectory, "hid-relevant.stderr.txt",
                "未找到引擎可执行文件；HID 报文长度与用途信息可能缺失。\n");

            warnings.Add("未找到引擎可执行文件，HID 报文长度与用途信息缺失。");
            step("hid", false, "引擎不可用。");
            WriteJson(workingDirectory, "bridge.json", bridgeInfo);
            return (bridgeInfo, null);
        }

        // ⚠️ 版本信息要用 FileVersionInfo.GetVersionInfo，而不是 FileInfo.VersionInfo
        //    —— 后者是 PowerShell 的 Get-Item 对象才有的属性，C# 的 FileInfo 没有。
        FileVersionInfo versionInfo = FileVersionInfo.GetVersionInfo(enginePath);
        bridgeInfo = bridgeInfo with
        {
            FileName = Path.GetFileName(enginePath),
            FileVersion = versionInfo.FileVersion ?? string.Empty,
            ProductVersion = versionInfo.ProductVersion ?? string.Empty,
        };

        var runner = new EngineRunner(enginePath);
        EngineResult result = runner
            .RunAsync("diagnose --json", timeoutMs: 30_000)
            .GetAwaiter().GetResult();

        bridgeInfo = bridgeInfo with { DiagnosticExitCode = result.ExitCode };

        File.WriteAllText(hidJsonPath, result.StandardOutput, new UTF8Encoding(true));
        File.WriteAllText(hidErrPath, result.StandardError, new UTF8Encoding(true));

        HidDiagnosticReport? hid = null;
        if (result.ExitCode == 0)
        {
            // `out` 参数用的是可空类型，编译器不会自动推断非空 ——
            // 必须先做 `is not null` 再解引用，否则留下 CS8602 空引用警告。
            if (EngineOutputParser.TryParseDiagnose(
                    result.StandardOutput, out HidDiagnosticReport? parsed, out string parseError)
                && parsed is not null)
            {
                hid = parsed;
                step("hid", true,
                    $"已通过引擎收集 {parsed.Count} 个 HID 接口（APEX 4 厂商接口 {parsed.Apex4Interfaces.Count} 个）。");
            }
            else
            {
                warnings.Add("引擎 diagnose 输出未能解析：" + parseError);
                step("hid", false, "diagnose 输出格式与预期不符。");
            }
        }
        else
        {
            warnings.Add($"引擎 HID 诊断退出码为 {result.ExitCode}。");
            step("hid", false, "引擎返回非零退出码。");
        }

        WriteJson(workingDirectory, "bridge.json", bridgeInfo);
        return (bridgeInfo, hid);
    }

    /// <summary>[4/7] XInput 枚举与只读采样。</summary>
    private static XInputDiagnostics CollectXInput(
        string workingDirectory,
        DiagnosticCollectionOptions options,
        Action<string> report,
        Action<string> warn,
        Action<string, bool, string> step,
        CancellationToken cancellationToken)
    {
        var result = new XInputDiagnostics { Available = false };

        try
        {
            IReadOnlyList<XInputNative.SlotInfo> slots = XInputNative.Enumerate();

            var devices = slots.Select(slot => new XInputDeviceInfo
            {
                Slot = slot.Slot,
                SubType = slot.SubType,
                Flags = slot.Flags,
                VendorId = slot.VendorId,
                VendorIdHex = $"0x{slot.VendorId:X4}",
                ProductId = slot.ProductId,
                ProductIdHex = $"0x{slot.ProductId:X4}",
                ProductVersion = slot.ProductVersion,
                ExtendedCapabilitiesAvailable = slot.ExtendedCapabilitiesAvailable,
            }).ToList();

            var samples = new List<XInputSampleInfo>();
            if (!options.SkipInputSample && devices.Count > 0
                && !cancellationToken.IsCancellationRequested)
            {
                int seconds = Math.Clamp(options.InputSampleSeconds, 1, 30);
                report($"  接下来 {seconds} 秒：请把两个摇杆推到底、两个扳机按到底、再按几个按键。");

                IReadOnlyList<XInputNative.SlotSample> raw =
                    XInputNative.Sample(seconds * 1000, cancellationToken);

                samples.AddRange(raw.Select(sample => new XInputSampleInfo
                {
                    Slot = sample.Slot,
                    Polls = sample.Polls,
                    StateChanges = sample.StateChanges,
                    ButtonsSeenMask = sample.ButtonsSeen,
                    ButtonsSeenHex = $"0x{sample.ButtonsSeen:X4}",
                    ButtonsSeen = XInputNative.ButtonNames(sample.ButtonsSeen),
                    LeftTrigger = new XInputRange(sample.LeftTriggerMin, sample.LeftTriggerMax),
                    RightTrigger = new XInputRange(sample.RightTriggerMin, sample.RightTriggerMax),
                    LeftStickX = new XInputRange(sample.ThumbLxMin, sample.ThumbLxMax),
                    LeftStickY = new XInputRange(sample.ThumbLyMin, sample.ThumbLyMax),
                    RightStickX = new XInputRange(sample.ThumbRxMin, sample.ThumbRxMax),
                    RightStickY = new XInputRange(sample.ThumbRyMin, sample.ThumbRyMax),
                }));

                report("  XInput 采样完成。");
            }

            result = new XInputDiagnostics { Available = true, Devices = devices, Samples = samples };

            if (devices.Count > 0)
            {
                step("xinput", true, $"发现 {devices.Count} 个已连接的 XInput 槽位。");
            }
            else
            {
                warn("收集期间没有看到任何已连接的 XInput 手柄。");
                step("xinput", false, "没有发现已连接的 XInput 槽位。");
            }
        }
        catch (Exception ex)
        {
            result = new XInputDiagnostics { Available = false, Error = ex.Message };
            warn("XInput 探测失败：" + ex.Message);
            step("xinput", false, "XInput 探测不可用。");
        }

        WriteJson(workingDirectory, "xinput.json", result);
        return result;
    }

    // ────────────────────────── 机型判定 ──────────────────────────

    /// <summary>
    /// 机型 / 连接模式判定 —— 上游脚本 <c>Get-ConnectedModelAssessment</c> 的规则移植。
    ///
    /// <para>
    /// <b>它要解决的真实陷阱：</b>APEX 4 在 PC/XInput 模式下会暴露通用的
    /// Xbox 360 身份 <c>045E:028E</c>，且四个接口的产品字符串都是
    /// 旧版遗留的 "Flydigi <b>VADER3</b>"（VID_04B4 是 Cypress 通用 VID）。
    /// 单看名字会误判成"接了别的手柄"。因此这里结合 HID + PnP 证据来判断：
    /// </para>
    /// <list type="bullet">
    /// <item>能看到模型专属的 <c>04B4:2412</c> → DInput 模式，正常；</item>
    /// <item>只看到 <c>045E:028E</c> 且同时有 BLE 可见 → 很可能是 XInput 模式，应切 DInput；</item>
    /// <item>看到的是 VADER3 但 VID:PID 是 04B4:2412 → 那是遗留别名，<b>不是</b>第二只手柄。</item>
    /// </list>
    /// </summary>
    private static ModelAssessment AssessModel(HidDiagnosticReport? hid, PnpDiagnostics pnp)
    {
        var products = new List<string>();
        var evidence = new List<string>();
        bool bluetoothVisible = false;
        bool usbOrHidVisible = false;
        bool xinputVisible = false;

        // ── HID 侧证据 ──
        if (hid is not null)
        {
            var flydigiDevices = hid.Devices.Where(device =>
                FlydigiHidRegex().IsMatch(device.Manufacturer)
                || FlydigiHidRegex().IsMatch(device.Product)
                || KnownVidPidRegex().IsMatch(device.DevicePath)).ToList();

            if (flydigiDevices.Any(device => XInput045ERegex().IsMatch(device.DevicePath)))
            {
                xinputVisible = true;
            }

            foreach (string product in flydigiDevices
                .Select(device => device.Product)
                .Where(product => !string.IsNullOrWhiteSpace(product)))
            {
                products.Add(product);
            }

            foreach (HidDeviceDetail device in flydigiDevices)
            {
                evidence.Add($"HID {device.VendorIdHex}:{device.ProductIdHex} {device.Product}");
            }
        }

        // ── PnP 侧证据 ──
        if (pnp.Available)
        {
            foreach (PnpDeviceEntry device in pnp.Devices)
            {
                string busDescription = device.Properties
                    .FirstOrDefault(property => property.Key == "DEVPKEY_Device_BusReportedDeviceDesc")
                    ?.Value as string ?? string.Empty;

                string label = string.Join(" / ",
                    new[] { device.FriendlyName, busDescription }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));

                if (FlydigiLabelRegex().IsMatch(label) || KnownInstanceRegex().IsMatch(device.InstanceId))
                {
                    if (!string.IsNullOrWhiteSpace(label)) products.Add(label);
                    evidence.Add($"PnP {device.InstanceId} [{label}]");
                }

                if (Apex4Regex().IsMatch(label))
                {
                    if (BluetoothPrefixRegex().IsMatch(device.InstanceId)) bluetoothVisible = true;
                    else if (UsbOrHidPrefixRegex().IsMatch(device.InstanceId)) usbOrHidVisible = true;
                }

                if (Apex4UsbInstanceRegex().IsMatch(device.InstanceId)) usbOrHidVisible = true;
                if (XInputUsbInstanceRegex().IsMatch(device.InstanceId)) xinputVisible = true;
            }
        }

        List<string> uniqueProducts = [.. products.Distinct().OrderBy(p => p, StringComparer.OrdinalIgnoreCase)];
        List<string> uniqueEvidence = [.. evidence.Distinct().OrderBy(e => e, StringComparer.OrdinalIgnoreCase)];

        string joined = string.Join(" ", uniqueProducts);

        // ⚠️ 关键修正（相对上游脚本的一次实质改进）：
        //    APEX 4 的四个接口<b>全都沿用旧版固件的产品字符串 "Flydigi VADER3"</b>，
        //    产品名里根本没有 "APEX 4" 字样。若只按产品名判定，
        //    最常见的「DInput + dongle」组合会被判成 inconclusive
        //    （实测确凿：2026-10-03 冒烟即得到 inconclusive + "没有接口报出机型名称"）。
        //
        //    而 USB/HID 实例 ID 里的 VID_04B4&PID_2412 是 APEX 4 <b>设备侧</b>的
        //    固有标识（VID_04B4 是 Cypress 通用 VID，真正有区分度的是 PID 2412）。
        //    命中它即可确认机型，无需依赖那个会骗人的产品名。
        bool hasApex4 = Apex4Regex().IsMatch(joined) || usbOrHidVisible;
        bool hasOther = OtherModelRegex().IsMatch(joined);

        // APEX 4 的 XInput 模式候选：看得到 045E:028E、看不到模型专属接口、
        // 且没有明确指向其他机型的更强证据
        bool apex4XInputCandidate =
            hasApex4
            && xinputVisible
            && !usbOrHidVisible
            && !OtherModelStrictRegex().IsMatch(joined);

        // 唯一已知的遗留别名（VADER3 挂在 04B4:2412 上）不算"第二只手柄"
        bool onlyKnownLegacyAlias =
            usbOrHidVisible && !OtherModelStrictRegex().IsMatch(joined);

        if (onlyKnownLegacyAlias)
        {
            hasOther = false;
        }

        string status = "inconclusive";
        if (apex4XInputCandidate) status = "expected_model_xinput_mode";
        else if (hasApex4 && hasOther) status = "expected_and_other_models_reported";
        else if (hasApex4) status = "expected_model_reported";
        else if (hasOther) status = "different_model_reported";

        // 机型确认依据也记进证据里，方便别人看报告时理解判定从何而来
        if (hasApex4 && usbOrHidVisible && !Apex4Regex().IsMatch(joined))
        {
            uniqueEvidence.Add(
                "机型依据：实例 ID 命中 VID_04B4&PID_2412（APEX 4 固有标识；" +
                "产品名沿用旧固件字符串 \"Flydigi VADER3\"，不可作为判据）");
        }

        return new ModelAssessment
        {
            Status = status,
            ExpectedModel = "Flydigi APEX 4",
            ReportedProducts = uniqueProducts,
            Evidence = uniqueEvidence,
            Apex4BluetoothVisible = bluetoothVisible,
            Apex4UsbOrHidVisible = usbOrHidVisible,
            XInput045E028EVisible = xinputVisible,
            Apex4XInputModeLikely = apex4XInputCandidate,
            OtherFlydigiModelVisible = hasOther && !apex4XInputCandidate,
        };
    }

    /// <summary>按判定结果给出可操作的中文警告（对应上游各分支的 <c>Add-Warning</c>）。</summary>
    private static void EmitAssessmentWarnings(ModelAssessment assessment, Action<string> warn)
    {
        switch (assessment.Status)
        {
            case "expected_model_xinput_mode":
                warn("手柄很可能连在 XInput 模式下，此时它会报成通用的 045E:028E / Flydigi VADER3 身份。"
                     + "请把控制器切到 DInput 模式（长按 FN + A 约 3 秒，或用它的 LCD 连接菜单）后重新收集。");
                break;

            case "expected_and_other_models_reported":
                warn("同时检测到 APEX 4 与其他飞智机型。请断开其他飞智手柄 / 接收器后重新收集。");
                break;

            case "different_model_reported":
                string reported = assessment.ReportedProducts.Count > 0
                    ? string.Join(", ", assessment.ReportedProducts)
                    : "其他飞智机型";
                warn($"连接的手柄自报为「{reported}」，不是 APEX 4。请接上目标手柄后重新收集。");
                break;

            case "inconclusive":
                warn("没有任何已连接接口明确报出 APEX 4 机型名称；分享报告前请先确认手柄。");
                break;
        }

        if (assessment.Apex4BluetoothVisible
            && !assessment.Apex4UsbOrHidVisible
            && !assessment.Apex4XInputModeLikely)
        {
            warn("APEX 4 仅通过蓝牙可见。要做协议相关工作，请改用 USB 线或它的 2.4G 接收器后重新收集。");
        }
    }

    // ────────────────────────── 输出辅助 ──────────────────────────

    private static string ResolveOutputDirectory(string? configured)
    {
        string directory = configured ?? string.Empty;

        if (string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                directory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            }
            catch
            {
                directory = string.Empty;
            }
        }

        if (string.IsNullOrWhiteSpace(directory))
        {
            directory = Path.GetTempPath();
        }

        directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static void WriteJson<T>(string workingDirectory, string fileName, T payload)
        => WriteTextFile(workingDirectory, fileName,
            DiagnosticJson.Serialize(payload, indented: true));

    private static void WriteTextFile(string workingDirectory, string fileName, string content)
    {
        try
        {
            File.WriteAllText(
                Path.Combine(workingDirectory, fileName), content, new UTF8Encoding(true));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"写入 {fileName} 失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 中文版 README —— 上游那份面向英文社区，这里改写为本项目口径。
    /// 安全声明与隐私说明两段是刻意保留的：报告可能被转发。
    /// </summary>
    private static string BuildReadme(DiagnosticSystemInfo system, IReadOnlyList<string> warnings)
    {
        string warningText = warnings.Count == 0
            ? "无。"
            : string.Join(Environment.NewLine, warnings.Select(w => "- " + w));

        var sb = new StringBuilder();
        sb.AppendLine("Ds in Apex 诊断报告");
        sb.AppendLine("===================");
        sb.AppendLine();
        sb.AppendLine($"收集器版本：{CollectorVersion}");
        sb.AppendLine($"收集时间（UTC）：{system.CollectedAtUtc}");
        sb.AppendLine($"操作系统：{system.WindowsProductName} {system.WindowsDisplayVersion} " +
                      $"(Build {system.WindowsBuild}.{system.WindowsUbr})");
        sb.AppendLine($"运行时：{system.RuntimeVersion}");
        sb.AppendLine($"管理员权限：{(system.Administrator ? "是" : "否")}");
        sb.AppendLine();
        sb.AppendLine("安全性");
        sb.AppendLine("------");
        sb.AppendLine("本次收集全程只读。它没有安装驱动、没有修改手柄设置、没有启用或禁用任何设备、");
        sb.AppendLine("没有写入厂商 HID 报文，也没有启动马达或自适应扳机。");
        sb.AppendLine();
        sb.AppendLine("文件清单");
        sb.AppendLine("--------");
        sb.AppendLine("- summary.json：收集状态与警告汇总");
        sb.AppendLine("- system.json：Windows / 运行时版本（不含计算机名与用户名）");
        sb.AppendLine("- hid-relevant.json：相关 HID 路径、用途页、报文长度与拓扑");
        sb.AppendLine("- model-assessment.json：基于 HID/PnP 证据的机型与连接模式判定");
        sb.AppendLine("- pnp-devices.json / .txt：匹配的 PnP 设备、容器关系与驱动属性");
        sb.AppendLine("- pnputil-relevant.txt：过滤后的 Windows PnP 回退结果");
        sb.AppendLine("- registry-relevant.txt：过滤后的 APEX 4 / Flydigi 注册表回退结果");
        sb.AppendLine("- xinput.json：XInput 槽位与输入取值范围 / 按键掩码");
        sb.AppendLine("- possibly-conflicting-processes.json：仅进程名，不含路径与命令行");
        sb.AppendLine("- relevant-services.json：HidHide / USBip / 虚拟手柄类服务的状态");
        sb.AppendLine();
        sb.AppendLine("隐私说明");
        sb.AppendLine("--------");
        sb.AppendLine("报告刻意剔除了 Windows 用户名、计算机名、IP 地址、文件列表、游戏库、");
        sb.AppendLine("完整进程列表与命令行。但硬件实例 ID、容器 ID、HID 路径与手柄序列号字段");
        sb.AppendLine("可能具有唯一性 —— 它们被保留是因为关联 APEX 4 的各接口必须用到。");
        sb.AppendLine("分享前请自行检查这些文本 / JSON 文件。");
        sb.AppendLine();
        sb.AppendLine("警告");
        sb.AppendLine("------");
        sb.AppendLine(warningText);

        return sb.ToString();
    }

    private static void CreateArchive(string sourceDirectory, string destinationPath)
    {
        if (File.Exists(destinationPath))
        {
            // 与上游同款保护：绝不覆盖已存在的压缩包
            throw new IOException("目标压缩包已存在，拒绝覆盖：" + destinationPath);
        }

        ZipFile.CreateFromDirectory(
            sourceDirectory, destinationPath, CompressionLevel.Optimal, includeBaseDirectory: false);
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"清理临时目录失败（不影响结果）：{AppLog.Describe(ex)}");
        }
    }
}
