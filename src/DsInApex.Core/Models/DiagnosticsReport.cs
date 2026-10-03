using System.Text.Json.Serialization;

namespace DsInApex.Core.Models;

/// <summary>
/// 诊断报告的全部数据模型。
///
/// <para>
/// <b>为什么要跟上游 PowerShell 脚本的 JSON 字段名保持一致：</b>
/// 这些文件会被用户打包发给社区 / 开发者排障。上游已有 1127 行脚本产出的
/// 同构报告，字段名一致意味着两边的报告可以互相对照着看，
/// 已有的排障经验不会因为换了个客户端而失效。
/// </para>
///
/// <para>
/// 少数字段是 DIA 特有的（例如把 <c>powershell_version</c> 换成
/// <c>runtime_version</c> —— DIA 不是 PowerShell 程序），已在注解中标明。
/// </para>
/// </summary>
public sealed class DiagnosticSystemInfo
{
    public string CollectorVersion { get; init; } = string.Empty;
    public string CollectedAtUtc { get; init; } = string.Empty;
    public string WindowsProductName { get; init; } = string.Empty;
    public string WindowsDisplayVersion { get; init; } = string.Empty;
    public string WindowsBuild { get; init; } = string.Empty;
    public string WindowsUbr { get; init; } = string.Empty;
    public bool OperatingSystem64Bit { get; init; }
    public bool Process64Bit { get; init; }

    /// <summary>DIA 特有：把上游的 <c>powershell_version</c> 换成 .NET 运行时版本。</summary>
    public string RuntimeVersion { get; init; } = string.Empty;

    public string Culture { get; init; } = string.Empty;
    public bool Administrator { get; init; }
}

/// <summary>
/// bridge.json —— 引擎可执行文件自身的版本信息与诊断调用结果。
/// <para>
/// ⚠️ 必须是 <c>record</c>：收集流程里会在拿到文件版本后
/// 用 <c>with</c> 表达式补全字段（<c>class</c> 不支持 <c>with</c>）。
/// </para>
/// </summary>
public sealed record BridgeExecutableInfo
{
    public bool Available { get; init; }
    public string FileName { get; init; } = string.Empty;
    public string FileVersion { get; init; } = string.Empty;
    public string ProductVersion { get; init; } = string.Empty;

    /// <summary><c>diagnose --json</c> 的退出码；未执行时为 <c>null</c>。</summary>
    public int? DiagnosticExitCode { get; init; }
}

/// <summary>
/// model-assessment.json —— 根据 HID + PnP 证据判断"接的是不是 APEX 4，处于什么模式"。
///
/// <para>
/// 这是上游脚本最有价值的一段逻辑：APEX 4 在 XInput 模式下会伪装成
/// 通用的 <c>045E:028E</c>（Xbox 360 手柄）+ 产品字符串 "Flydigi VADER3"，
/// 单看设备名会得出"接错了手柄"的错误结论。这段规则就是为了识别这个陷阱。
/// </para>
/// </summary>
public sealed class ModelAssessment
{
    /// <summary>
    /// 判定结果之一：
    /// <c>expected_model_reported</c> / <c>expected_model_xinput_mode</c> /
    /// <c>expected_and_other_models_reported</c> / <c>different_model_reported</c> /
    /// <c>inconclusive</c>。
    /// </summary>
    public string Status { get; init; } = "inconclusive";

    public string ExpectedModel { get; init; } = "Flydigi APEX 4";
    public IReadOnlyList<string> ReportedProducts { get; init; } = [];
    public IReadOnlyList<string> Evidence { get; init; } = [];
    public bool Apex4BluetoothVisible { get; init; }
    public bool Apex4UsbOrHidVisible { get; init; }

    [JsonPropertyName("xinput_045e_028e_visible")]
    public bool XInput045E028EVisible { get; init; }

    public bool Apex4XInputModeLikely { get; init; }
    public bool OtherFlydigiModelVisible { get; init; }
}

/// <summary>pnp-devices.json 的单个属性项。</summary>
public sealed class PnpProperty
{
    public string Key { get; init; } = string.Empty;

    /// <summary>值可能是字符串，也可能是字符串数组（多值属性）。</summary>
    public object? Value { get; init; }
}

/// <summary>pnp-devices.json 的单个设备。</summary>
public sealed class PnpDeviceEntry
{
    public string InstanceId { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;

    /// <summary>⚠️ 上游字段名是 <c>class</c>（C# 关键字），显式指定。</summary>
    [JsonPropertyName("class")]
    public string DeviceClass { get; init; } = string.Empty;

    public string FriendlyName { get; init; } = string.Empty;
    public string Problem { get; init; } = string.Empty;
    public string? ContainerId { get; init; }
    public string? Parent { get; init; }
    public IReadOnlyList<PnpProperty> Properties { get; init; } = [];
}

/// <summary>pnp-devices.json 顶层。</summary>
public sealed class PnpDiagnostics
{
    public bool Available { get; init; }
    public int SeedCount { get; init; }
    public int RelatedCount { get; init; }
    public IReadOnlyList<PnpDeviceEntry> Devices { get; init; } = [];
    public string Error { get; init; } = string.Empty;
}

/// <summary>xinput.json 的单个槽位。</summary>
public sealed class XInputDeviceInfo
{
    public int Slot { get; init; }
    public int SubType { get; init; }
    public int Flags { get; init; }
    public int VendorId { get; init; }
    public string VendorIdHex { get; init; } = string.Empty;
    public int ProductId { get; init; }
    public string ProductIdHex { get; init; } = string.Empty;
    public int ProductVersion { get; init; }
    public bool ExtendedCapabilitiesAvailable { get; init; }
}

/// <summary>某个轴 / 扳机的取值范围。</summary>
public sealed record XInputRange(int Min, int Max);

/// <summary>xinput.json 的采样结果。</summary>
public sealed class XInputSampleInfo
{
    public int Slot { get; init; }
    public long Polls { get; init; }
    public long StateChanges { get; init; }
    public int ButtonsSeenMask { get; init; }
    public string ButtonsSeenHex { get; init; } = string.Empty;
    public IReadOnlyList<string> ButtonsSeen { get; init; } = [];
    public XInputRange LeftTrigger { get; init; } = new(0, 0);
    public XInputRange RightTrigger { get; init; } = new(0, 0);
    public XInputRange LeftStickX { get; init; } = new(0, 0);
    public XInputRange LeftStickY { get; init; } = new(0, 0);
    public XInputRange RightStickX { get; init; } = new(0, 0);
    public XInputRange RightStickY { get; init; } = new(0, 0);
}

/// <summary>xinput.json 顶层。</summary>
public sealed class XInputDiagnostics
{
    public bool Available { get; init; }
    public IReadOnlyList<XInputDeviceInfo> Devices { get; init; } = [];
    public IReadOnlyList<XInputSampleInfo> Samples { get; init; } = [];
    public string Error { get; init; } = string.Empty;
}

/// <summary>
/// possibly-conflicting-processes.json 的条目。
/// <para>
/// ⚠️ 上游用 <c>Select-Object ProcessName, Id</c> 直接序列化，
/// 因此字段名是 <b>PascalCase</b> 的 <c>ProcessName</c> / <c>Id</c>，显式保留。
/// </para>
/// </summary>
public sealed class ConflictProcessInfo
{
    [JsonPropertyName("ProcessName")] public string ProcessName { get; init; } = string.Empty;
    [JsonPropertyName("Id")] public int Id { get; init; }
}

/// <summary>
/// relevant-services.json 的条目。
/// <para>
/// ⚠️ 内核驱动会出现在 <c>Win32_SystemDriver</c> 而非 <c>Win32_Service</c>
/// （P4 已踩过的坑），因此收集时两个 WMI 类都要查。
/// </para>
/// </summary>
public sealed class RelevantServiceInfo
{
    public string Name { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string StartType { get; init; } = string.Empty;

    /// <summary>来源 WMI 类（<c>Win32_SystemDriver</c> / <c>Win32_Service</c>），DIA 附加字段。</summary>
    public string Source { get; init; } = string.Empty;
}

/// <summary>summary.json 里的单个收集步骤。</summary>
public sealed class DiagnosticStep
{
    public string Name { get; init; } = string.Empty;
    public bool Success { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>summary.json —— 整体收集结果的索引。</summary>
public sealed class DiagnosticSummary
{
    public string CollectorVersion { get; init; } = string.Empty;
    public string CollectedAtUtc { get; init; } = string.Empty;
    public BridgeExecutableInfo? Bridge { get; init; }
    public ModelAssessment? ModelAssessment { get; init; }
    public bool PnpAvailable { get; init; }
    public int PnpSeedCount { get; init; }
    public int PnpRelatedCount { get; init; }
    public bool PnputilMatchFound { get; init; }

    // ⚠️ 必须显式指定：默认的 snake_case 策略会把 XInputAvailable 拆成
    //    `x_input_available`，而上游脚本产出的是 `xinput_available`。
    //    字段名不一致会让两边的报告没法直接对照。
    [JsonPropertyName("xinput_available")]
    public bool XInputAvailable { get; init; }

    [JsonPropertyName("xinput_device_count")]
    public int XInputDeviceCount { get; init; }
    public IReadOnlyList<string> Warnings { get; init; } = [];
    public IReadOnlyList<DiagnosticStep> Steps { get; init; } = [];
}

/// <summary>
/// 一次完整诊断收集的结果（编排层返回给界面）。
/// </summary>
public sealed class DiagnosticCollectionResult
{
    public bool Success { get; init; }

    /// <summary>未压缩的中间目录（收集失败时保留，供排查）。</summary>
    public string WorkingDirectory { get; init; } = string.Empty;

    /// <summary>生成的 ZIP 路径；失败时为 <c>null</c>。</summary>
    public string? ArchivePath { get; init; }

    public DiagnosticSummary? Summary { get; init; }

    /// <summary>收集过程中的逐行日志（界面实时展示）。</summary>
    public IReadOnlyList<string> LogLines { get; init; } = [];

    /// <summary>失败原因；成功时为空。</summary>
    public string Error { get; init; } = string.Empty;
}
