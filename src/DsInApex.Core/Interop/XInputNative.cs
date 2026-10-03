using System.Runtime.InteropServices;

namespace DsInApex.Core.Interop;

/// <summary>
/// XInput 的 P/Invoke 层。
///
/// <para>
/// <b>为什么必须在 C# 里原生实现，而不能继续用上游的 PowerShell 脚本：</b>
/// 上游 <c>Collect-Apex4-Diagnostics.ps1</c> 用 <c>Add-Type -TypeDefinition</c>
/// 在运行时编译这段 C# 源码来获得 XInput 探针
/// （见该脚本 <c>Initialize-XInputProbe</c>，约 210 行）。
/// 而<b>本机安全策略禁用了 <c>Add-Type</c></b>（README §5.8 已记录）——
/// 也就是说那份脚本在本机<b>必然在 XInput 环节失败</b>。
/// 在 C# 项目里直接 <c>DllImport</c> 就没有这个中间步骤，反而更短。
/// </para>
///
/// <para>
/// 结构体布局与脚本中的定义逐字段对齐（含 <c>XInputGetCapabilitiesEx</c> 的
/// <c>#108</c> 序号导入 —— 该函数没有公开导出名，只能按序号绑定）。
/// </para>
/// </summary>
internal static class XInputNative
{
    /// <summary>Win8 及以上统一提供 xinput1_4.dll。</summary>
    private const string Library = "xinput1_4.dll";

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputGamepad
    {
        public ushort Buttons;
        public byte LeftTrigger;
        public byte RightTrigger;
        public short ThumbLX;
        public short ThumbLY;
        public short ThumbRX;
        public short ThumbRY;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputState
    {
        public uint PacketNumber;
        public XInputGamepad Gamepad;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputVibration
    {
        public ushort LeftMotorSpeed;
        public ushort RightMotorSpeed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputCapabilities
    {
        public byte Type;
        public byte SubType;
        public ushort Flags;
        public XInputGamepad Gamepad;
        public XInputVibration Vibration;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct XInputCapabilitiesEx
    {
        public XInputCapabilities Capabilities;
        public ushort VendorId;
        public ushort ProductId;
        public ushort ProductVersion;
        public ushort Unknown1;
        public uint Unknown2;
    }

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetState(uint userIndex, out XInputState state);

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetCapabilities(
        uint userIndex, uint flags, out XInputCapabilities capabilities);

    /// <summary>
    /// 扩展能力查询（可拿到真实 VID/PID）。
    /// <para>
    /// ⚠️ 该函数<b>没有导出名</b>，只能按序号 <c>#108</c> 绑定；
    /// 不同 xinput 版本序号可能不同，因此调用方必须容忍
    /// <see cref="EntryPointNotFoundException"/> 并降级。
    /// </para>
    /// </summary>
    [DllImport(Library, EntryPoint = "#108", CallingConvention = CallingConvention.StdCall)]
    private static extern uint XInputGetCapabilitiesEx(
        uint reserved, uint userIndex, uint flags, out XInputCapabilitiesEx capabilities);

    /// <summary>XInput 按钮位掩码（<b>与 DualSense 位序不同</b>）。</summary>
    private static readonly (ushort Mask, string Name)[] ButtonTable =
    [
        (0x0001, "DPadUp"),
        (0x0002, "DPadDown"),
        (0x0004, "DPadLeft"),
        (0x0008, "DPadRight"),
        (0x0010, "Start"),
        (0x0020, "Back"),
        (0x0040, "LeftStick"),
        (0x0080, "RightStick"),
        (0x0100, "LeftShoulder"),
        (0x0200, "RightShoulder"),
        (0x0400, "Guide"),
        (0x1000, "A"),
        (0x2000, "B"),
        (0x4000, "X"),
        (0x8000, "Y"),
    ];

    /// <summary>把按钮掩码展开成名称列表。</summary>
    internal static IReadOnlyList<string> ButtonNames(ushort mask)
        => [.. ButtonTable.Where(entry => (mask & entry.Mask) != 0).Select(entry => entry.Name)];

    /// <summary>探测结果：某个槽位上是否存在设备。</summary>
    internal sealed record SlotInfo(
        int Slot,
        int SubType,
        int Flags,
        int VendorId,
        int ProductId,
        int ProductVersion,
        bool ExtendedCapabilitiesAvailable);

    /// <summary>采样统计。</summary>
    internal sealed class SlotSample
    {
        public int Slot { get; init; }
        public long Polls { get; set; }
        public long StateChanges { get; set; }
        public ushort ButtonsSeen { get; set; }
        public int LeftTriggerMin { get; set; }
        public int LeftTriggerMax { get; set; }
        public int RightTriggerMin { get; set; }
        public int RightTriggerMax { get; set; }
        public int ThumbLxMin { get; set; }
        public int ThumbLxMax { get; set; }
        public int ThumbLyMin { get; set; }
        public int ThumbLyMax { get; set; }
        public int ThumbRxMin { get; set; }
        public int ThumbRxMax { get; set; }
        public int ThumbRyMin { get; set; }
        public int ThumbRyMax { get; set; }
    }

    /// <summary>
    /// 枚举 0–3 号槽位上的 XInput 设备。
    /// <para>
    /// <b>永不抛异常</b>：xinput1_4.dll 缺失（老系统）或序号导入失败时返回空列表，
    /// 由调用方把"XInput 不可用"作为一条诊断结论记录下来。
    /// </para>
    /// </summary>
    internal static IReadOnlyList<SlotInfo> Enumerate()
    {
        var results = new List<SlotInfo>();

        try
        {
            for (uint slot = 0; slot < 4; ++slot)
            {
                if (XInputGetCapabilities(slot, 0, out XInputCapabilities basic) != 0)
                {
                    continue;   // 该槽位无设备
                }

                int vendorId = 0;
                int productId = 0;
                int productVersion = 0;
                bool extended = false;

                try
                {
                    // 序号参数有歧义：#108 的第一参数在不同文档里有 0/1 两种用法，
                    // 上游脚本也是先试 1 再试 0，此处照搬。
                    uint status = XInputGetCapabilitiesEx(1, slot, 0, out XInputCapabilitiesEx caps);
                    if (status != 0)
                    {
                        status = XInputGetCapabilitiesEx(0, slot, 0, out caps);
                    }

                    if (status == 0)
                    {
                        vendorId = caps.VendorId;
                        productId = caps.ProductId;
                        productVersion = caps.ProductVersion;
                        extended = true;
                    }
                }
                catch (EntryPointNotFoundException)
                {
                    // 该 xinput 版本没有 #108 导出 —— 保留基本信息即可
                }
                catch (DllNotFoundException)
                {
                    // 同上
                }

                results.Add(new SlotInfo(
                    (int)slot, basic.SubType, basic.Flags,
                    vendorId, productId, productVersion, extended));
            }
        }
        catch (DllNotFoundException)
        {
            // xinput1_4.dll 不可用（非常老的系统）—— 返回空，调用方会记录"不可用"
        }

        return results;
    }

    /// <summary>
    /// 只读采样：在指定时长内高频轮询，记录每个槽位的取值范围与状态变化次数。
    ///
    /// <para>
    /// 这是 XInput 诊断的核心价值 —— 它能证明"手柄确实在往系统送数据"
    /// （<c>state_changes &gt; 0</c>），比"设备存在"强得多。
    /// </para>
    ///
    /// <para>纯读取，不写任何震动 / 触发器指令。</para>
    /// </summary>
    internal static IReadOnlyList<SlotSample> Sample(int durationMs, CancellationToken cancellationToken)
    {
        var samples = new Dictionary<int, SlotSample>();
        var lastPackets = new Dictionary<int, uint>();

        try
        {
            long deadline = Environment.TickCount64 + durationMs;

            while (Environment.TickCount64 < deadline && !cancellationToken.IsCancellationRequested)
            {
                for (uint slot = 0; slot < 4; ++slot)
                {
                    if (XInputGetState(slot, out XInputState state) != 0)
                    {
                        continue;
                    }

                    int index = (int)slot;
                    if (!samples.TryGetValue(index, out SlotSample? sample))
                    {
                        sample = new SlotSample
                        {
                            Slot = index,
                            ButtonsSeen = state.Gamepad.Buttons,
                            LeftTriggerMin = state.Gamepad.LeftTrigger,
                            LeftTriggerMax = state.Gamepad.LeftTrigger,
                            RightTriggerMin = state.Gamepad.RightTrigger,
                            RightTriggerMax = state.Gamepad.RightTrigger,
                            ThumbLxMin = state.Gamepad.ThumbLX,
                            ThumbLxMax = state.Gamepad.ThumbLX,
                            ThumbLyMin = state.Gamepad.ThumbLY,
                            ThumbLyMax = state.Gamepad.ThumbLY,
                            ThumbRxMin = state.Gamepad.ThumbRX,
                            ThumbRxMax = state.Gamepad.ThumbRX,
                            ThumbRyMin = state.Gamepad.ThumbRY,
                            ThumbRyMax = state.Gamepad.ThumbRY,
                        };
                        samples[index] = sample;
                        lastPackets[index] = state.PacketNumber;
                    }
                    else
                    {
                        if (lastPackets[index] != state.PacketNumber)
                        {
                            sample.StateChanges++;
                            lastPackets[index] = state.PacketNumber;
                        }

                        sample.ButtonsSeen |= state.Gamepad.Buttons;
                        sample.LeftTriggerMin = Math.Min(sample.LeftTriggerMin, state.Gamepad.LeftTrigger);
                        sample.LeftTriggerMax = Math.Max(sample.LeftTriggerMax, state.Gamepad.LeftTrigger);
                        sample.RightTriggerMin = Math.Min(sample.RightTriggerMin, state.Gamepad.RightTrigger);
                        sample.RightTriggerMax = Math.Max(sample.RightTriggerMax, state.Gamepad.RightTrigger);
                        sample.ThumbLxMin = Math.Min(sample.ThumbLxMin, state.Gamepad.ThumbLX);
                        sample.ThumbLxMax = Math.Max(sample.ThumbLxMax, state.Gamepad.ThumbLX);
                        sample.ThumbLyMin = Math.Min(sample.ThumbLyMin, state.Gamepad.ThumbLY);
                        sample.ThumbLyMax = Math.Max(sample.ThumbLyMax, state.Gamepad.ThumbLY);
                        sample.ThumbRxMin = Math.Min(sample.ThumbRxMin, state.Gamepad.ThumbRX);
                        sample.ThumbRxMax = Math.Max(sample.ThumbRxMax, state.Gamepad.ThumbRX);
                        sample.ThumbRyMin = Math.Min(sample.ThumbRyMin, state.Gamepad.ThumbRY);
                        sample.ThumbRyMax = Math.Max(sample.ThumbRyMax, state.Gamepad.ThumbRY);
                    }

                    sample.Polls++;
                }

                Thread.Sleep(4);   // 与上游脚本一致：约 250 Hz
            }
        }
        catch (DllNotFoundException)
        {
            // 返回已采集到的部分
        }

        return [.. samples.Values.OrderBy(sample => sample.Slot)];
    }
}
