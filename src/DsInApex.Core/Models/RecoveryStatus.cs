namespace DsInApex.Core.Models;

/// <summary>控制器隔离的恢复标记状态。</summary>
public enum RecoveryMarkerState
{
    /// <summary>无恢复标记 —— 上次会话正常收尾，一切干净。</summary>
    Clean,

    /// <summary>有恢复标记，且标记里的属主进程仍在运行 —— 会话进行中，属正常。</summary>
    ActiveSession,

    /// <summary>
    /// 有恢复标记，但属主进程已不存在 —— <b>上次会话异常终止</b>。
    ///
    /// <para>
    /// 手柄可能仍处于「对系统隐藏」的状态（HidHide 过滤未撤）。
    /// 引擎的看门狗会在下次登录时靠 <c>RunOnce</c> 项补救，
    /// 但在那之前用户看到的就是「手柄在游戏里彻底不见了」。
    /// 这个状态就是要把这种隐患显式暴露出来，并提供手动兜底。
    /// </para>
    /// </summary>
    PendingRecovery,

    /// <summary>标记存在但内容不可读（版本不认识/权限不足）。</summary>
    Unreadable,
}

/// <summary>
/// 控制器隔离恢复状态快照（P6）。
///
/// <para>
/// <b>这不是「我重新实现了一个看门狗」，而是「把引擎已有的看门狗机制讲清楚」。</b>
/// 引擎侧（<c>WindowsPhysicalControllerIsolation.cpp</c>）的完整闭环是：
/// </para>
/// <list type="number">
/// <item>会话启动 → 在 <c>HKCU\Software\ApexSenseBridge\PhysicalControllerIsolation</c>
/// 写一份快照（原白/黑名单、Phase、属主 PID、APEX 配置档恢复待办等）；</item>
/// <item>同时写 <c>HKCU\...\CurrentVersion\RunOnce</c> 的
/// <c>!ApexSenseBridgeRestoreControllerVisibility</c> —— 保证下次登录时兜底恢复；</item>
/// <item>再拉起 <c>hidhide-watchdog &lt;pid&gt; &lt;token&gt;</c> 子进程盯着属主进程，
/// 属主意外死亡即代为恢复；</item>
/// <item>会话正常结束 → <c>clearRecoveryRegistration()</c> 把上面两处一起清掉。</item>
/// </list>
///
/// <para>
/// 于是 DIA 侧唯一该做的事就是<b>只读这两处注册表</b>，把状态翻译给用户看，
/// 并在发现异常遗留时提供一键兜底（调引擎的只读恢复命令）。
/// 不重造轮子，也不碰引擎的判定逻辑。
/// </para>
/// </summary>
public sealed record RecoveryStatus
{
    public required RecoveryMarkerState State { get; init; }

    /// <summary>标记里的属主进程 ID（无标记时为 0）。</summary>
    public required uint OwnerProcessId { get; init; }

    /// <summary>属主进程是否仍在运行。</summary>
    public required bool OwnerStillRunning { get; init; }

    /// <summary>引擎写入的隔离阶段：0=已准备，1=配置可能已变更。</summary>
    public required uint Phase { get; init; }

    /// <summary>APEX 板载配置档是否还欠一次恢复。</summary>
    public required bool ProfileRestorePending { get; init; }

    /// <summary>下次登录的兜底恢复项是否已登记（RunOnce）。</summary>
    public required bool RunOnceRegistered { get; init; }

    /// <summary>原白名单条目数（只报数量，不外泄内容）。</summary>
    public required int OriginalWhitelistCount { get; init; }

    /// <summary>原黑名单条目数。</summary>
    public required int OriginalBlacklistCount { get; init; }

    /// <summary>标记版本（0 表示无标记）。</summary>
    public required uint MarkerVersion { get; init; }

    /// <summary>读取失败或异常说明。</summary>
    public string? Error { get; init; }

    /// <summary>恢复标记键路径（界面展示用）。</summary>
    public required string MarkerKeyPath { get; init; }

    /// <summary>RunOnce 项名（界面展示用）。</summary>
    public required string RunOnceValueName { get; init; }

    /// <summary>是否存在需要用户介入的遗留状态。</summary>
    public bool NeedsUserAttention => State == RecoveryMarkerState.PendingRecovery;
}
