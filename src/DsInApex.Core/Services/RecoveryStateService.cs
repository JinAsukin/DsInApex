using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Win32;

namespace DsInApex.Core.Services;

/// <summary>
/// 控制器隔离恢复状态探查（P6，只读）。
///
/// <para>
/// 引擎在会话期间会做三件事（源码 <c>WindowsPhysicalControllerIsolation.cpp</c>）：
/// 写恢复快照、写 RunOnce 兜底项、拉起 <c>hidhide-watchdog</c> 子进程。
/// 正常收尾时这些痕迹会被 <c>clearRecoveryRegistration()</c> 一并清掉。
/// <b>所以「痕迹还在、属主进程却没了」= 上次会话是异常终止的</b>，
/// 此时手柄可能仍对系统隐藏 —— 这正是本服务要暴露的隐患。
/// </para>
///
/// <para>
/// 本服务<b>只读注册表，不写、不删、不恢复</b>。
/// 恢复动作一律交给引擎自己的命令（<c>restore-controller-visibility</c> /
/// <c>stop-active-sessions</c>），由 App 层在用户显式点击后执行 ——
/// 这样 DIA 永远不会与引擎的状态机抢方向盘。
/// </para>
/// </summary>
public static class RecoveryStateService
{
    private const string LogFileName = "dsinapex_recovery.log";

    /// <summary>恢复快照键（HKCU 相对路径）。与引擎 <c>kRecoveryKey</c> 逐字一致，不可改。</summary>
    public const string MarkerKeyPath = @"Software\ApexSenseBridge\PhysicalControllerIsolation";

    /// <summary>RunOnce 键（HKCU 相对路径）。</summary>
    public const string RunOnceKeyPath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    /// <summary>RunOnce 值名。与引擎 <c>kRunOnceValue</c> 一致（前导 <c>!</c> 是「失败也删」语义）。</summary>
    public const string RunOnceValueName = "!ApexSenseBridgeRestoreControllerVisibility";

    /// <summary>引擎写入的标记版本（v1 无 APEX 配置档字段，v2 有）。</summary>
    private const uint SupportedMarkerVersionMin = 1;
    private const uint SupportedMarkerVersionMax = 2;

    /// <summary>引擎的阶段语义：0 = 已准备，1 = 配置可能已变更。</summary>
    private const uint PhasePrepared = 0;

    /// <summary>采集恢复状态（只读）。</summary>
    public static RecoveryStatus Collect()
    {
        bool runOnceRegistered = QueryRunOnceRegistered();

        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(MarkerKeyPath, writable: false);

            if (key is null)
            {
                return Clean(runOnceRegistered);
            }

            uint version = ReadDword(key, "Version");
            if (version < SupportedMarkerVersionMin || version > SupportedMarkerVersionMax)
            {
                return new RecoveryStatus
                {
                    State = RecoveryMarkerState.Unreadable,
                    OwnerProcessId = 0,
                    OwnerStillRunning = false,
                    Phase = 0,
                    ProfileRestorePending = false,
                    RunOnceRegistered = runOnceRegistered,
                    OriginalWhitelistCount = 0,
                    OriginalBlacklistCount = 0,
                    MarkerVersion = version,
                    Error = $"恢复标记版本 {version} 不受支持（期望 {SupportedMarkerVersionMin}–{SupportedMarkerVersionMax}）。",
                    MarkerKeyPath = MarkerKeyPath,
                    RunOnceValueName = RunOnceValueName,
                };
            }

            uint ownerPid = ReadDword(key, "OwnerProcessId");
            uint phase = ReadDword(key, "Phase");
            bool profilePending = ReadDword(key, "ProfileRestorePending") != 0;
            int whitelistCount = ReadMultiString(key, "OriginalWhitelist").Length;
            int blacklistCount = ReadMultiString(key, "OriginalBlacklist").Length;

            bool ownerAlive = IsProcessAlive(ownerPid);

            return new RecoveryStatus
            {
                State = ownerAlive ? RecoveryMarkerState.ActiveSession : RecoveryMarkerState.PendingRecovery,
                OwnerProcessId = ownerPid,
                OwnerStillRunning = ownerAlive,
                Phase = phase,
                ProfileRestorePending = profilePending,
                RunOnceRegistered = runOnceRegistered,
                OriginalWhitelistCount = whitelistCount,
                OriginalBlacklistCount = blacklistCount,
                MarkerVersion = version,
                MarkerKeyPath = MarkerKeyPath,
                RunOnceValueName = RunOnceValueName,
            };
        }
        catch (Exception ex)
        {
            string reason = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"读取恢复标记失败：{reason}");

            return new RecoveryStatus
            {
                State = RecoveryMarkerState.Unreadable,
                OwnerProcessId = 0,
                OwnerStillRunning = false,
                Phase = 0,
                ProfileRestorePending = false,
                RunOnceRegistered = runOnceRegistered,
                OriginalWhitelistCount = 0,
                OriginalBlacklistCount = 0,
                MarkerVersion = 0,
                Error = reason,
                MarkerKeyPath = MarkerKeyPath,
                RunOnceValueName = RunOnceValueName,
            };
        }
    }

    /// <summary>
    /// 把状态翻译成可读的多行说明（界面与自检共用同一份文案来源，避免两处说法不一致）。
    /// </summary>
    public static IReadOnlyList<string> Describe(RecoveryStatus status)
    {
        var lines = new List<string>
        {
            $"恢复标记     : {MarkerKeyPath}",
            $"下次登录兜底 : {(status.RunOnceRegistered ? "已登记（RunOnce）" : "未登记")}",
        };

        switch (status.State)
        {
            case RecoveryMarkerState.Clean:
                lines.Add("状态         : 干净 —— 无残留标记，上次会话正常收尾。");
                break;

            case RecoveryMarkerState.ActiveSession:
                lines.Add($"状态         : 会话进行中（属主 PID {status.OwnerProcessId} 仍存活）。");
                lines.Add($"隔离阶段     : {DescribePhase(status.Phase)}");
                break;

            case RecoveryMarkerState.PendingRecovery:
                lines.Add($"状态         : ⚠ 检测到上次会话异常遗留（属主 PID {status.OwnerProcessId} 已不存在）。");
                lines.Add($"隔离阶段     : {DescribePhase(status.Phase)}");
                lines.Add($"白/黑名单    : 原白名单 {status.OriginalWhitelistCount} 条 / 原黑名单 {status.OriginalBlacklistCount} 条");
                if (status.ProfileRestorePending)
                {
                    lines.Add("APEX 配置档  : ⚠ 尚欠一次恢复（ProfileRestorePending=1）");
                }
                lines.Add("建议         : 若手柄在游戏里「消失」，点「一键恢复控制器可见性」即可。");
                break;

            case RecoveryMarkerState.Unreadable:
                lines.Add($"状态         : 无法判定 — {status.Error}");
                break;
        }

        return lines;
    }

    private static string DescribePhase(uint phase) => phase switch
    {
        PhasePrepared => "已准备（0）",
        1 => "配置可能已变更（1）",
        _ => $"未知（{phase}）",
    };

    private static RecoveryStatus Clean(bool runOnceRegistered) => new()
    {
        State = RecoveryMarkerState.Clean,
        OwnerProcessId = 0,
        OwnerStillRunning = false,
        Phase = 0,
        ProfileRestorePending = false,
        RunOnceRegistered = runOnceRegistered,
        OriginalWhitelistCount = 0,
        OriginalBlacklistCount = 0,
        MarkerVersion = 0,
        MarkerKeyPath = MarkerKeyPath,
        RunOnceValueName = RunOnceValueName,
    };

    private static bool QueryRunOnceRegistered()
    {
        try
        {
            using RegistryKey? key = Registry.CurrentUser.OpenSubKey(RunOnceKeyPath, writable: false);
            return key?.GetValue(RunOnceValueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    private static uint ReadDword(RegistryKey key, string name)
    {
        object? raw = key.GetValue(name);
        return raw switch
        {
            int i => unchecked((uint)i),
            long l => unchecked((uint)l),
            _ => 0,
        };
    }

    private static string[] ReadMultiString(RegistryKey key, string name)
        => key.GetValue(name) as string[] ?? [];

    /// <summary>
    /// 属主进程是否仍在运行。
    ///
    /// <para>
    /// ⚠️ 只认「进程存在」这一件事，不校验镜像名 ——
    /// PID 会被复用，理论上可能把「属主已死但 PID 被别的进程占用」误判成会话进行中。
    /// 这个方向是<b>刻意选的保守侧</b>：误判成「进行中」只会让提示少一次，
    /// 而误判成「异常遗留」会在用户正打游戏时弹出撤销提示，那才叫坏事。
    /// </para>
    /// </summary>
    private static bool IsProcessAlive(uint processId)
    {
        if (processId == 0)
        {
            return false;
        }

        try
        {
            using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById((int)processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
