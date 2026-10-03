namespace DsInApex.Core.Models;

/// <summary>引擎 CLI 命令的分类。决定它在硬件测试页里落在哪个分区。</summary>
public enum EngineCommandCategory
{
    /// <summary>设备识别：读取身份与能力，不改任何东西。</summary>
    DeviceInfo,

    /// <summary>输入监视：读取按钮 / 摇杆 / 扳机状态。</summary>
    InputMonitor,

    /// <summary>硬件测试：会真实驱动扳机或马达，用户必须显式触发。</summary>
    HardwareTest,

    /// <summary>虚拟设备：创建 / 检视虚拟 DualSense。</summary>
    VirtualDevice,

    /// <summary>应急操作：中断会话、恢复手柄可见性、清空效果。</summary>
    Emergency,

    /// <summary>工具：不碰硬件的本地命令。</summary>
    Utility,
}

/// <summary>
/// 命令对系统的影响等级。
/// <para>
/// <b>这是 P5 的安全边界</b>：<see cref="ReadOnly"/> 可以随便跑（含自检）；
/// <see cref="WritesHardware"/> 与 <see cref="Emergency"/> 必须由用户显式点击触发，
/// 且绝不允许进入自动化自检流程。
/// </para>
/// </summary>
public enum EngineCommandRisk
{
    /// <summary>只读：不写手柄、不改系统状态。</summary>
    ReadOnly,

    /// <summary>写硬件：会真实驱动扳机 / 马达，用户能感觉到。</summary>
    WritesHardware,

    /// <summary>应急：会强制改变会话或设备可见性状态。</summary>
    Emergency,
}

/// <summary>
/// 一条引擎 CLI 命令的元数据（参数能力 / 影响等级 / 超时）。
///
/// <para>
/// 为什么要集中成表：P5 要收编 19 个子命令，散在各处的字符串拼接
/// 一旦与 C++ 解析器脱节就会静默失败（退出码 3 或直接参数被忽略）。
/// 集中后，参数顺序只有 <see cref="EngineCommandCatalog"/> 一处可改。
/// </para>
///
/// <para>
/// 全部参数名与拼写来自 <c>engine/src/cli/CliApplication.cpp</c> 与
/// <c>DeviceCommands.cpp</c> 的实际解析代码，**不是文档猜测**。
/// </para>
/// </summary>
/// <param name="Id">命令主词，如 <c>list</c>。</param>
/// <param name="Category">所属分区。</param>
/// <param name="Risk">影响等级。</param>
/// <param name="SupportsJson">是否支持 <c>--json</c>。</param>
/// <param name="SupportsIndex">是否接受可选的设备序号 <c>[index]</c>。</param>
/// <param name="NeedsDevice">是否需要手柄在位才有意义。</param>
/// <param name="NeedsSessionIdle">
/// 是否要求「没有活动桥接会话」——
/// 写硬件的测试与会话会抢同一个 HID 接口，必须互斥。
/// </param>
/// <param name="DefaultTimeoutMs">默认超时（毫秒）。长命令须留够时间。</param>
/// <param name="SecondsOption">
/// 是否接受 <c>--seconds N</c>，以及允许的取值范围（用于界面滑杆边界）。
/// </param>
public sealed record EngineCommandSpec(
    string Id,
    EngineCommandCategory Category,
    EngineCommandRisk Risk,
    bool SupportsJson = false,
    bool SupportsIndex = false,
    bool NeedsDevice = true,
    bool NeedsSessionIdle = false,
    int DefaultTimeoutMs = 30_000,
    (int Min, int Max)? SecondsOption = null)
{
    /// <summary>是否为只读命令（可以进自检）。</summary>
    public bool IsReadOnly => Risk == EngineCommandRisk.ReadOnly;
}

/// <summary>
/// <c>--seconds</c> 的取值范围。
///
/// <para>
/// C++ 侧对几乎所有 <c>--seconds</c> 的实现都是
/// <c>seconds == 0 || seconds > 60</c> 即报错，因此 1–60 是硬边界；
/// 各命令的默认值不同（<c>input-status</c> 默认 3，<c>apex4-port-test</c> 默认 10）。
/// </para>
/// </summary>
public static class EngineSecondsRange
{
    public const int Min = 1;
    public const int Max = 60;
}
