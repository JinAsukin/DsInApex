using System.Text.Json.Serialization;

namespace DsInApex.Core.Models;

/// <summary>手柄与主机的连接方式。</summary>
public enum LinkMode
{
    Unknown = 0,
    Wired,
    Dongle,
    Bluetooth,
}

/// <summary>
/// <c>identify</c> 命令解析出的手柄身份。
///
/// <para>对应引擎文本（2026-10-03 实测）：</para>
/// <code>
/// Verified: Apex 4 (k2, DeviceType 84, firmware 0x6837)
/// Connection: dongle (raw 0)
/// Battery level: 85 (charging)      ← 有电量信息时才出现
/// Adaptive triggers: yes
/// </code>
/// </summary>
public sealed class ApexIdentity
{
    /// <summary>机型显示名，如 <c>Apex 4</c>。</summary>
    public string ModelName { get; init; } = string.Empty;

    /// <summary>机型代号，如 <c>k2</c>。</summary>
    public string Codename { get; init; } = string.Empty;

    /// <summary>设备类型号（<c>DeviceType 84</c>）。</summary>
    public int? DeviceType { get; init; }

    /// <summary>固件版本，如 <c>0x6837</c>（保留原始十六进制写法）。</summary>
    public string Firmware { get; init; } = string.Empty;

    /// <summary>连接方式。</summary>
    public LinkMode Link { get; init; } = LinkMode.Unknown;

    /// <summary>连接方式的原始数值（引擎 <c>raw N</c>）。</summary>
    public int? LinkRaw { get; init; }

    /// <summary>电量百分比；引擎未上报时为 <c>null</c>。</summary>
    public int? BatteryPercent { get; init; }

    /// <summary>是否正在充电。</summary>
    public bool IsCharging { get; init; }

    /// <summary>是否支持自适应扳机（引擎固定报 yes，保留字段以便未来变化）。</summary>
    public bool AdaptiveTriggers { get; init; }

    /// <summary>是否为 APEX 4 机型（决定界面上哪些测试可用）。</summary>
    public bool IsApex4 => ModelName.Contains("Apex 4", StringComparison.OrdinalIgnoreCase);

    /// <summary>是否为 APEX 5 机型。</summary>
    public bool IsApex5 => ModelName.Contains("Apex 5", StringComparison.OrdinalIgnoreCase);

    /// <summary>解析失败时为 true —— 此时界面应降级展示原始文本。</summary>
    public bool IsUnparsed { get; init; }
}

/// <summary>
/// <c>input-status --json</c> 的解析结果。
///
/// <para>
/// 字段与 <c>engine/src/cli/DeviceCommands.cpp::runInputStatus</c> 的 JSON 输出一一对应。
/// 摇杆 / 扳机取值范围 0–255，摇杆中位 128。
/// </para>
/// </summary>
public sealed class InputStatusReport
{
    [JsonPropertyName("backend")] public string Backend { get; init; } = string.Empty;
    [JsonPropertyName("event_driven")] public bool EventDriven { get; init; }
    [JsonPropertyName("received_state")] public bool ReceivedState { get; init; }
    [JsonPropertyName("reports")] public long Reports { get; init; }
    [JsonPropertyName("state_changes")] public long StateChanges { get; init; }
    [JsonPropertyName("timeouts")] public long Timeouts { get; init; }
    [JsonPropertyName("parse_failures")] public long ParseFailures { get; init; }
    [JsonPropertyName("lx")] public int LeftStickX { get; init; }
    [JsonPropertyName("ly")] public int LeftStickY { get; init; }
    [JsonPropertyName("rx")] public int RightStickX { get; init; }
    [JsonPropertyName("ry")] public int RightStickY { get; init; }
    [JsonPropertyName("l2")] public int LeftTrigger { get; init; }
    [JsonPropertyName("r2")] public int RightTrigger { get; init; }
    [JsonPropertyName("dpad")] public int DPad { get; init; }
    [JsonPropertyName("dpad_name")] public string DPadName { get; init; } = string.Empty;
    [JsonPropertyName("seen_dpad")] public int SeenDPad { get; init; }
    [JsonPropertyName("seen_dpad_directions")] public string SeenDPadDirections { get; init; } = string.Empty;
    [JsonPropertyName("buttons")] public int Buttons { get; init; }
    [JsonPropertyName("warning")] public string Warning { get; init; } = string.Empty;

    /// <summary>把按钮位掩码展开成可读名称（DualSense 标准位序）。</summary>
    public IReadOnlyList<string> ButtonNames => DualSenseButton.NamesOf(Buttons);
}

/// <summary>
/// DualSense 按钮位掩码。
///
/// <para>
/// <b>⚠️ 不是 XInput 位序。</b>源码依据：<c>engine/src/dualsense/DualSenseInput.h</c>
/// 的 <c>namespace asb::dualsense::button</c>。用 XInput 的掩码去解读会得到
/// 完全错误的按钮名，而且不会报错 —— 典型的静默故障。
/// </para>
/// </summary>
public static class DualSenseButton
{
    private static readonly (int Mask, string Name)[] Table =
    [
        (0x0001, "PS"),
        (0x0002, "Touchpad"),
        (0x0004, "Mute"),
        (0x0010, "Square"),
        (0x0020, "Cross"),
        (0x0040, "Circle"),
        (0x0080, "Triangle"),
        (0x0100, "L1"),
        (0x0200, "R1"),
        (0x0400, "L2"),
        (0x0800, "R2"),
        (0x1000, "Create"),
        (0x2000, "Options"),
        (0x4000, "L3"),
        (0x8000, "R3"),
    ];

    /// <summary>展开掩码为按钮名列表（按位序，未识别的位置忽略）。</summary>
    public static IReadOnlyList<string> NamesOf(int mask)
        => [.. Table.Where(entry => (mask & entry.Mask) != 0).Select(entry => entry.Name)];
}

/// <summary>
/// <c>virtual-ds --json</c> 的解析结果。
///
/// <para>
/// 字段对应 <c>VirtualDualSenseCommand.cpp</c> 的输出。
/// 该命令<b>不打开 APEX 接口</b>，只创建一个中立虚拟 DualSense 并统计收到的反馈报告，
/// 因此它是安全的"虚拟设备链路"验证手段。
/// </para>
/// </summary>
public sealed class VirtualDsReport
{
    [JsonPropertyName("virtual_ds_connected")] public bool Connected { get; init; }
    [JsonPropertyName("backend")] public string Backend { get; init; } = string.Empty;
    [JsonPropertyName("backend_version")] public string BackendVersion { get; init; } = string.Empty;
    [JsonPropertyName("dualsense_firmware_update")] public string DualSenseFirmware { get; init; } = string.Empty;
    [JsonPropertyName("dualsense_firmware_current")] public bool FirmwareCurrent { get; init; }
    [JsonPropertyName("input_mode")] public string InputMode { get; init; } = string.Empty;
    [JsonPropertyName("apex_routing")] public string ApexRouting { get; init; } = string.Empty;
    [JsonPropertyName("output_reports")] public long OutputReports { get; init; }
    [JsonPropertyName("trigger_reports")] public long TriggerReports { get; init; }
    [JsonPropertyName("rumble_reports")] public long RumbleReports { get; init; }
    [JsonPropertyName("audio_haptics_frames")] public long AudioHapticsFrames { get; init; }
    [JsonPropertyName("audio_default_protection")] public string AudioDefaultProtection { get; init; } = string.Empty;
    [JsonPropertyName("audio_default_roles_restored")] public long AudioDefaultRolesRestored { get; init; }
    [JsonPropertyName("malformed_frames")] public long MalformedFrames { get; init; }
    [JsonPropertyName("unknown_frames")] public long UnknownFrames { get; init; }
}

/// <summary>
/// <c>diagnose --json</c> 里的单个 HID 接口。
///
/// <para>
/// 比 <see cref="FlydigiDeviceInfo"/>（解析自 <c>list</c> 的文本）信息量大得多：
/// 包含设备路径、容器 ID、父实例、硬件 ID 列表 —— 诊断报告的原始素材。
/// </para>
/// </summary>
public sealed class HidDeviceDetail
{
    [JsonPropertyName("index")] public int Index { get; init; }
    [JsonPropertyName("device_path")] public string DevicePath { get; init; } = string.Empty;
    [JsonPropertyName("vendor_id")] public int VendorId { get; init; }
    [JsonPropertyName("vendor_id_hex")] public string VendorIdHex { get; init; } = string.Empty;
    [JsonPropertyName("product_id")] public int ProductId { get; init; }
    [JsonPropertyName("product_id_hex")] public string ProductIdHex { get; init; } = string.Empty;
    [JsonPropertyName("manufacturer")] public string Manufacturer { get; init; } = string.Empty;
    [JsonPropertyName("product")] public string Product { get; init; } = string.Empty;
    [JsonPropertyName("serial")] public string Serial { get; init; } = string.Empty;
    [JsonPropertyName("friendly_name")] public string FriendlyName { get; init; } = string.Empty;
    [JsonPropertyName("class")] public string DeviceClass { get; init; } = string.Empty;
    [JsonPropertyName("usage_page")] public int UsagePage { get; init; }
    [JsonPropertyName("usage_page_hex")] public string UsagePageHex { get; init; } = string.Empty;
    [JsonPropertyName("usage")] public int Usage { get; init; }
    [JsonPropertyName("usage_hex")] public string UsageHex { get; init; } = string.Empty;
    [JsonPropertyName("input_report_length")] public int InputReportLength { get; init; }
    [JsonPropertyName("output_report_length")] public int OutputReportLength { get; init; }
    [JsonPropertyName("feature_report_length")] public int FeatureReportLength { get; init; }
    [JsonPropertyName("instance_id")] public string InstanceId { get; init; } = string.Empty;
    [JsonPropertyName("parent_instance_id")] public string ParentInstanceId { get; init; } = string.Empty;
    [JsonPropertyName("container_id")] public string ContainerId { get; init; } = string.Empty;
    [JsonPropertyName("interface_number")] public string InterfaceNumber { get; init; } = string.Empty;
    [JsonPropertyName("hardware_ids")] public IReadOnlyList<string> HardwareIds { get; init; } = [];
    [JsonPropertyName("compatible_ids")] public IReadOnlyList<string> CompatibleIds { get; init; } = [];

    /// <summary>是否属于飞智 APEX 4 的厂商接口（VID 04B4 / PID 2412）。</summary>
    public bool IsApex4VendorInterface
        => string.Equals(VendorIdHex, "0x04B4", StringComparison.OrdinalIgnoreCase)
           && string.Equals(ProductIdHex, "0x2412", StringComparison.OrdinalIgnoreCase);
}

/// <summary><c>diagnose --json</c> 的顶层结构。</summary>
public sealed class HidDiagnosticReport
{
    [JsonPropertyName("count")] public int Count { get; init; }
    [JsonPropertyName("devices")] public IReadOnlyList<HidDeviceDetail> Devices { get; init; } = [];

    /// <summary>属于 APEX 4 厂商接口的条目。</summary>
    public IReadOnlyList<HidDeviceDetail> Apex4Interfaces
        => [.. Devices.Where(device => device.IsApex4VendorInterface)];
}

/// <summary>
/// 一次引擎命令的统一结果。
///
/// <para>
/// <b>降级契约：</b><see cref="IsParsed"/> 为 <c>false</c> 时，
/// 界面必须展示 <see cref="StandardOutput"/> / <see cref="StandardError"/> 的原始文本，
/// 而不是显示"无数据"。引擎输出格式是已知脆弱项，解析失败必须可见。
/// </para>
/// </summary>
public sealed class EngineCommandOutcome
{
    public required string CommandId { get; init; }
    public required string Arguments { get; init; }
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public bool TimedOut { get; init; }

    /// <summary>是否被识别为成功（退出码 0 且未超时）。</summary>
    public bool Succeeded => !TimedOut && ExitCode == 0;

    /// <summary>是否解析出了结构化结果。为 false 时界面走原始文本降级。</summary>
    public bool IsParsed { get; init; }

    /// <summary>解析出的产物（<see cref="ApexIdentity"/> / <see cref="InputStatusReport"/> 等）。</summary>
    public object? Parsed { get; init; }

    /// <summary>给用户看的一句话结论（已本地化）。</summary>
    public string Summary { get; init; } = string.Empty;

    /// <summary>解析失败时的原因（供诊断展示）。</summary>
    public string ParseError { get; init; } = string.Empty;

    /// <summary>合并后的原始文本，用于降级展示。</summary>
    public string RawText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(StandardError)) return StandardOutput;
            if (string.IsNullOrWhiteSpace(StandardOutput)) return StandardError;
            return StandardOutput + Environment.NewLine + StandardError;
        }
    }
}
