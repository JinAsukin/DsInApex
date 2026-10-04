using System.Globalization;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 一次引擎调用的可选参数。
///
/// <para>
/// 用记录而不是散落的字符串拼接：参数字母表与顺序只允许在
/// <see cref="EngineCommandCatalog.BuildArguments"/> 一处成型。
/// </para>
/// </summary>
/// <param name="DeviceIndex">设备序号（位置参数，紧跟命令主词）。</param>
/// <param name="Seconds"><c>--seconds</c> 的取值。</param>
/// <param name="Json">是否追加 <c>--json</c>（该命令不支持时会被忽略）。</param>
/// <param name="ProfileTarget"><c>--target</c> 的取值（1–4，仅配置档切换用）。</param>
/// <param name="Rumble">是否追加 <c>--rumble</c>（仅 <c>apex4-port-test</c> 用）。</param>
/// <param name="ForceAdapt">是否追加 <c>--forceadapt</c>（仅 <c>apex4-port-test</c> 用）。</param>
/// <param name="TriggerSide">
/// <c>--side</c> 的取值（仅 <c>test-trigger</c> 用）：<c>lt</c> / <c>rt</c> / <c>both</c>。
/// </param>
/// <param name="TriggerMode">
/// <c>--mode</c> 的取值（仅 <c>test-trigger</c> 用）：
/// <c>resistance</c> / <c>weapon</c> / <c>vibration</c> / <c>bow</c> / <c>normal</c>。
/// </param>
/// <param name="TriggerLevel">
/// <c>--level</c> 的强度档位 1–4（仅 <c>test-trigger</c> 用）。越界不会下发。
/// </param>
public sealed record EngineCommandOptions(
    int? DeviceIndex = null,
    int? Seconds = null,
    bool Json = false,
    int? ProfileTarget = null,
    bool Rumble = false,
    bool ForceAdapt = false,
    string? TriggerSide = null,
    string? TriggerMode = null,
    int? TriggerLevel = null);

/// <summary>
/// 引擎 CLI 命令目录（<b>唯一真源</b>）。
///
/// <para>
/// <b>为什么需要它：</b>上游有 19 个可用子命令，参数散布在
/// <c>CliApplication.cpp</c> 的分发与各 <c>commandXxx()</c> 的解析循环里。
/// P5 要把它们全部收进界面，如果每个页面各自拼字符串，
/// 参数拼错不会报错 —— C++ 侧对未知选项的处理不一致（有的退出 1，
/// 有的直接忽略），结果是"点了没反应"这类最难查的故障。
/// </para>
///
/// <para>
/// <b>本表的每一行都有实测或源码依据</b>（2026-10-03 实测 + 源码核对），
/// 不是照文档抄的 —— 上游 <c>printUsage()</c> 并未列出
/// <c>xinput-status</c> / <c>restore-controller-visibility</c> / <c>hidhide-watchdog</c>。
/// </para>
/// </summary>
public static class EngineCommandCatalog
{
    /// <summary>不写硬件的通用超时：够覆盖 HID 枚举与身份交换。</summary>
    private const int ShortTimeoutMs = 20_000;

    /// <summary>带采样的命令：采样时长 + 启动开销。</summary>
    private const int SampleTimeoutMs = 100_000;

    /// <summary>全部命令定义。顺序即界面上大致的展示顺序。</summary>
    public static readonly IReadOnlyList<EngineCommandSpec> All =
    [
        // ─────────── 设备识别 ───────────
        new(Id: "list",
            Category: EngineCommandCategory.DeviceInfo,
            Risk: EngineCommandRisk.ReadOnly,
            SupportsJson: false,          // ⚠️ 实测：list 不支持 --json
            SupportsIndex: false,
            NeedsDevice: false,           // 无设备时返回退出码 2 + 明确提示，本身就是有效结果
            DefaultTimeoutMs: ShortTimeoutMs),

        new(Id: "identify",
            Category: EngineCommandCategory.DeviceInfo,
            Risk: EngineCommandRisk.ReadOnly,
            SupportsIndex: true,
            NeedsDevice: true,
            DefaultTimeoutMs: ShortTimeoutMs),

        new(Id: "diagnose",
            Category: EngineCommandCategory.DeviceInfo,
            Risk: EngineCommandRisk.ReadOnly,
            SupportsJson: true,
            NeedsDevice: false,           // 列全部 HID 接口，无手柄也有内容
            DefaultTimeoutMs: ShortTimeoutMs),

        // ─────────── 输入监视 ───────────
        new(Id: "input-status",
            Category: EngineCommandCategory.InputMonitor,
            Risk: EngineCommandRisk.ReadOnly,
            SupportsJson: true,
            SupportsIndex: true,
            NeedsDevice: true,
            DefaultTimeoutMs: SampleTimeoutMs,
            SecondsOption: (EngineSecondsRange.Min, EngineSecondsRange.Max)),

        new(Id: "xinput-status",
            Category: EngineCommandCategory.InputMonitor,
            Risk: EngineCommandRisk.ReadOnly,
            NeedsDevice: false,           // 查的是系统 XInput 槽位，与 APEX 是否在位无关
            DefaultTimeoutMs: 10_000),

        // ─────────── 虚拟设备 ───────────
        new(Id: "virtual-ds",
            Category: EngineCommandCategory.VirtualDevice,
            Risk: EngineCommandRisk.ReadOnly,
            SupportsJson: true,
            NeedsDevice: false,           // 明确不打开 APEX 接口
            NeedsSessionIdle: true,       // 但与活动会话会争用虚拟手柄后端
            DefaultTimeoutMs: SampleTimeoutMs,
            SecondsOption: (EngineSecondsRange.Min, EngineSecondsRange.Max)),

        // ─────────── 硬件测试（会真实驱动手柄） ───────────
        new(Id: "test-rt",
            Category: EngineCommandCategory.HardwareTest,
            Risk: EngineCommandRisk.WritesHardware,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: 30_000),

        // P10：test-trigger 是上游 1.0.0 才有的能力 —— 能**分侧**驱动扳机。
        // 旧版只有 test-rt（只测右扳机），导致「到底哪一侧坏」根本无法自证，
        // 实测报告里"RT 没反应"这种结论只能靠体感，没法复现。
        new(Id: "test-trigger",
            Category: EngineCommandCategory.HardwareTest,
            Risk: EngineCommandRisk.WritesHardware,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: SampleTimeoutMs,   // 引擎 --seconds 上限 60，留足裕量
            SecondsOption: (EngineSecondsRange.Min, EngineSecondsRange.Max)),

        new(Id: "test-rumble",
            Category: EngineCommandCategory.HardwareTest,
            Risk: EngineCommandRisk.WritesHardware,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: 30_000),

        new(Id: "test-profile-switch",
            Category: EngineCommandCategory.HardwareTest,
            Risk: EngineCommandRisk.WritesHardware,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: 30_000),

        new(Id: "apex4-port-test",
            Category: EngineCommandCategory.HardwareTest,
            Risk: EngineCommandRisk.WritesHardware,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: SampleTimeoutMs,
            SecondsOption: (EngineSecondsRange.Min, EngineSecondsRange.Max)),

        // ─────────── 应急操作 ───────────
        new(Id: "clear",
            Category: EngineCommandCategory.Emergency,
            Risk: EngineCommandRisk.Emergency,
            SupportsIndex: true,
            NeedsDevice: true,
            NeedsSessionIdle: true,
            DefaultTimeoutMs: ShortTimeoutMs),

        new(Id: "stop-active-sessions",
            Category: EngineCommandCategory.Emergency,
            Risk: EngineCommandRisk.Emergency,
            NeedsDevice: false,
            DefaultTimeoutMs: 25_000),    // 引擎内部等待上限 10 秒，留足裕量

        new(Id: "restore-controller-visibility",
            Category: EngineCommandCategory.Emergency,
            Risk: EngineCommandRisk.Emergency,
            NeedsDevice: false,
            DefaultTimeoutMs: ShortTimeoutMs),

        // ─────────── 工具 ───────────
        new(Id: "dry-run",
            Category: EngineCommandCategory.Utility,
            Risk: EngineCommandRisk.ReadOnly,
            NeedsDevice: false,           // 纯本地拼包，无任何 HID I/O
            DefaultTimeoutMs: 10_000),
    ];

    private static readonly Dictionary<string, EngineCommandSpec> ById =
        All.ToDictionary(spec => spec.Id, StringComparer.Ordinal);

    /// <summary>按命令主词取定义；未知命令返回 <c>null</c>。</summary>
    public static EngineCommandSpec? Get(string id)
        => ById.TryGetValue(id, out EngineCommandSpec? spec) ? spec : null;

    /// <summary>按分类筛选（界面上按分区渲染时用）。</summary>
    public static IReadOnlyList<EngineCommandSpec> InCategory(EngineCommandCategory category)
        => [.. All.Where(spec => spec.Category == category)];

    /// <summary>
    /// 拼装完整参数串。
    ///
    /// <para>
    /// <b>顺序契约：</b>命令主词 → 位置参数 index → 选项。
    /// C++ 侧的位置参数解析在选项循环里用 <c>stoul</c> 兜底
    /// （见 <c>DeviceCommands.cpp</c> 的 <c>else</c> 分支），因此
    /// index 放在选项之前或之后都能被接受，但**只能出现一次**。
    /// </para>
    /// </summary>
    public static string BuildArguments(EngineCommandSpec spec, EngineCommandOptions? options = null)
    {
        options ??= new EngineCommandOptions();
        var parts = new List<string> { spec.Id };

        // 位置参数：设备序号
        if (spec.SupportsIndex && options.DeviceIndex is >= 0)
        {
            parts.Add(options.DeviceIndex.Value.ToString(CultureInfo.InvariantCulture));
        }

        // --seconds：超出范围时钳制，不发非法参数让引擎报退出码 1
        if (spec.SecondsOption is { } range && options.Seconds is { } seconds)
        {
            int clamped = Math.Clamp(seconds, range.Min, range.Max);
            parts.Add("--seconds");
            parts.Add(clamped.ToString(CultureInfo.InvariantCulture));
        }

        // --json：命令不支持时静默忽略（而不是发过去被当未知选项）
        if (spec.SupportsJson && options.Json)
        {
            parts.Add("--json");
        }

        // --target：仅 test-profile-switch，且必须是 1–4
        if (spec.Id == "test-profile-switch" && options.ProfileTarget is >= 1 and <= 4)
        {
            parts.Add("--target");
            parts.Add(options.ProfileTarget.Value.ToString(CultureInfo.InvariantCulture));
        }

        // --rumble / --forceadapt：仅 apex4-port-test
        if (spec.Id == "apex4-port-test")
        {
            if (options.Rumble) parts.Add("--rumble");
            if (options.ForceAdapt) parts.Add("--forceadapt");
        }

        // --side / --mode / --level：仅 test-trigger
        // ⚠️ 取值直接透传上游的字符串字面量（C++ 侧对未知取值会**静默回落到默认**，
        //    不会报错），所以合法性约束必须在 UI 层做，别指望引擎兜底。
        //    可接受的别名见 DeviceCommands.cpp：side 还认 left/l2/right/r2；
        //    mode 还认 race/off/clear/break/sniper/rattle/recoil。
        if (spec.Id == "test-trigger")
        {
            if (!string.IsNullOrWhiteSpace(options.TriggerSide))
            {
                parts.Add("--side");
                parts.Add(options.TriggerSide);
            }

            if (!string.IsNullOrWhiteSpace(options.TriggerMode))
            {
                parts.Add("--mode");
                parts.Add(options.TriggerMode);
            }

            if (options.TriggerLevel is >= 1 and <= 4)
            {
                parts.Add("--level");
                parts.Add(options.TriggerLevel.Value.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>带兜底文案的退出码解释。</summary>
    /// <param name="Key">本地化资源键；界面优先取它。</param>
    /// <param name="Fallback">键缺失时的中文兜底，保证永不显示裸键名。</param>
    public readonly record struct ExitMeaning(string Key, string Fallback);

    /// <summary>
    /// 把退出码翻译成人能看懂的原因。
    ///
    /// <para>
    /// <b>为什么不能只看 0 / 非 0：</b>引擎的退出码是逐命令自定义的 ——
    /// <c>list</c> 的 2 是"没插手柄"，<c>test-rt</c> 的 5 是"重置失败"，
    /// <c>test-profile-switch</c> 的 14 是"没能恢复原配置档"。
    /// 一律当成"命令崩了"会把可操作的信息浪费掉。
    /// </para>
    ///
    /// <para>
    /// 取值来源：<c>engine/src/cli/DeviceCommands.cpp</c> 与 <c>CliApplication.cpp</c>
    /// 的 <c>return</c> 语句逐一核对（2026-10-03）。
    /// </para>
    /// </summary>
    public static ExitMeaning DescribeExitCode(string commandId, int exitCode)
    {
        if (exitCode == 0)
        {
            return new ExitMeaning("Loc_HwExit_Ok", "命令执行成功。");
        }

        return commandId switch
        {
            "list" when exitCode == 2
                => new("Loc_HwExit_NoDevice", "没有找到 APEX 4/5 的厂商 HID 接口——请确认手柄已连接、且处于 DInput 模式。"),

            "identify" when exitCode == 3
                => new("Loc_HwExit_IdentityFailed", "身份校验失败——设备可能被 Flydigi Space Station 等程序占用。"),

            "input-status" when exitCode == 2
                => new("Loc_HwExit_IdentityFailed", "身份校验失败——设备可能被 Flydigi Space Station 等程序占用。"),
            "input-status" when exitCode == 4
                => new("Loc_HwExit_InputStreamFailed", "输入流中断——手柄可能在采样期间断开。"),
            "input-status" when exitCode == 5
                => new("Loc_HwExit_NoState", "采样期间没有收到任何输入状态。"),

            "test-rt" when exitCode == 3
                => new("Loc_HwExit_OpenFailed", "无法打开手柄。"),
            "test-rt" when exitCode == 4
                => new("Loc_HwExit_WriteFailed", "写入扳机效果失败。"),
            "test-rt" when exitCode == 5
                => new("Loc_HwExit_ResetFailed", "测试后自动复位失败——请在飞智空间站把手柄两个扳机设回「标准」。"),

            "test-trigger" when exitCode == 3
                => new("Loc_HwExit_OpenFailed", "无法打开手柄。"),
            "test-trigger" when exitCode == 4
                => new("Loc_HwExit_WriteFailed", "写入扳机效果失败——引擎输出里会指明是 LT 还是 RT。"),
            "test-trigger" when exitCode == 5
                => new("Loc_HwExit_ResetFailed", "测试后自动复位失败——请在飞智空间站把手柄两个扳机设回「标准」。"),

            "test-rumble" when exitCode == 3
                => new("Loc_HwExit_OpenFailed", "无法打开手柄。"),
            "test-rumble" when exitCode == 12
                => new("Loc_HwExit_RumbleFailed", "震动写入或停止失败。"),

            "test-profile-switch" when exitCode == 2
                => new("Loc_HwExit_IdentityFailed", "身份校验失败——设备可能被 Flydigi Space Station 等程序占用。"),
            "test-profile-switch" when exitCode == 3
                => new("Loc_HwExit_Apex5Only", "该命令仅支持 APEX 5；当前机型不适用，未发送任何命令。"),
            "test-profile-switch" when exitCode == 4
                => new("Loc_HwExit_ProfilePreflight", "切换前的配置档状态预检失败。"),
            "test-profile-switch" when exitCode == 13
                => new("Loc_HwExit_ProfileSwitchUnverified", "配置档切换未能验证，但原配置档已恢复。"),
            "test-profile-switch" when exitCode == 14
                => new("Loc_HwExit_ProfileRestoreFailed", "原配置档未能恢复——请用「配置档恢复」应急操作，或用手柄快捷方式手动切回。"),

            "apex4-port-test" when exitCode == 2
                => new("Loc_HwExit_IdentityFailed", "身份校验失败——设备可能被 Flydigi Space Station 等程序占用。"),
            "apex4-port-test" when exitCode == 3
                => new("Loc_HwExit_Apex4Only", "该命令需要已验证的 APEX 4；当前机型不匹配。"),

            "clear" when exitCode == 3
                => new("Loc_HwExit_OpenFailed", "无法打开手柄。"),
            "clear" when exitCode == 4
                => new("Loc_HwExit_ClearFailed", "清除扳机 / 震动效果失败。"),

            "stop-active-sessions" when exitCode == 1
                => new("Loc_HwExit_StopSessionsFailed", "请求停止活动会话失败——可能有更高权限的进程持有会话。"),

            "restore-controller-visibility" when exitCode == 11
                => new("Loc_HwExit_RestoreVisibilityFailed", "恢复手柄可见性失败——可能需要管理员权限。"),

            // 未列出的组合：1 是通用的"参数错误"，其余按通用失败处理
            // ⚠️ 必须用 `_ when`：匹配主体是 commandId（string），
            //    直接写 `1 =>` 会因类型不符编译失败。
            _ when exitCode == 1
                => new ExitMeaning("Loc_HwExit_BadArguments", "参数不被引擎接受（参数拼写或取值范围有误）。"),
            _ => new ExitMeaning("Loc_HwExit_GenericFailure", "命令失败（退出码 {0}，详见原始输出）。"),
        };
    }
}
