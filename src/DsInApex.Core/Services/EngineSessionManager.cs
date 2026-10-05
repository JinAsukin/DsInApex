using System.Globalization;
using DsInApex.Core.Localization;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 桥接会话状态机：负责「起 / 停 / 当前游戏 / 当前配置档」，并通过事件对外广播。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/EngineSessionManager.cs</c>（269 行）。
/// 分层与 <see cref="BridgeSession"/> 的关系保持不变：
/// <b>BridgeSession 管进程与 IPC，本类管业务状态与事件。</b>
/// </para>
///
/// <para>
/// <b>⚠️ 两条硬契约（改动会导致 DIA 与官方版 / Playnite 插件互相打架）：</b>
/// </para>
/// <list type="bullet">
/// <item>会话互斥名 <c>Local\ApexSenseBridge.ActiveSession.Owner.v1</c> 必须沿用上游 ——
/// 这样 DIA、Playnite 插件与官方托盘能互相感知，不会抢手柄；</item>
/// <item><c>BuildArguments()</c> 拼出的 <c>bridge-triggers</c> 参数序列必须与 C++ 解析器逐字对应。</item>
/// </list>
/// </summary>
public sealed class EngineSessionManager
{
    /// <summary>
    /// 会话所有者互斥名。<b>不可改</b> —— 与上游 / Playnite 插件的跨应用握手依据。
    /// </summary>
    private const string EngineSessionMutexName = @"Local\ApexSenseBridge.ActiveSession.Owner.v1";

    private readonly object syncLock = new();
    private BridgeSession? activeSession;
    private string? activeGameTitle;
    private string? activeProfile;
    private bool isStarting;

    /// <summary>是否有会话处于活动状态。</summary>
    public bool IsSessionActive
    {
        get { lock (syncLock) { return activeSession is not null; } }
    }

    /// <summary>会话是否健康（进程仍活着）。</summary>
    public bool IsSessionHealthy
    {
        get { lock (syncLock) { return activeSession is not null && activeSession.ProcessId != 0; } }
    }

    /// <summary>当前游戏标题；无会话时返回本地化的「无」。</summary>
    public string ActiveGameTitle
    {
        get { lock (syncLock) { return activeGameTitle ?? Loc("Loc_None"); } }
    }

    /// <summary>当前触摸板配置档；无会话时返回 <c>none</c>。</summary>
    public string ActiveProfile
    {
        get { lock (syncLock) { return activeProfile ?? "none"; } }
    }

    /// <summary>当前引擎进程 PID；无会话时为 0。</summary>
    public int ActiveProcessId
    {
        get
        {
            lock (syncLock)
            {
                return activeSession?.ProcessId ?? 0;
            }
        }
    }

    /// <summary>当前会话的 IPC token（诊断展示用）。</summary>
    public string? ActiveToken
    {
        get
        {
            lock (syncLock)
            {
                return activeSession?.Token;
            }
        }
    }

    /// <summary>会话已启动（参数：游戏标题、配置档名）。</summary>
    public event Action<string, string>? SessionStarted;

    /// <summary>会话已停止（参数：停止原因）。</summary>
    public event Action<string>? SessionStopped;

    /// <summary>会话出错（参数：可读错误信息）。</summary>
    public event Action<string>? SessionError;

    /// <summary>过程日志（含引擎 stdout/stderr 转发）。</summary>
    public event Action<string>? LogMessage;

    public bool StartSession(string gameTitle, string profileName, TraySettings settings, out string? error)
        => StartSession(gameTitle, profileName, settings, 0, out error);

    /// <summary>
    /// 启动桥接会话。已在 <c>--apex-profile</c> 之外的部分不支持并发（单会话模型）。
    /// </summary>
    public bool StartSession(string gameTitle, string profileName, TraySettings settings,
                             int apexProfileSlot, out string? error)
    {
        error = null;

        lock (syncLock)
        {
            if (activeSession is not null || isStarting)
            {
                error = Loc("Loc_ErrSessionAlreadyActive");
                return false;
            }
            isStarting = true;
        }

        try
        {
            if (IsExternalSessionActive())
            {
                error = Loc("Loc_ErrExternalSessionActive");
                RaiseLogMessage(error + " " + Loc("Loc_ErrExternalSessionActiveHint"));
                return false;
            }

            string enginePath = EngineLocator.ResolveEngine();
            if (string.IsNullOrWhiteSpace(enginePath))
            {
                error = Loc("Loc_ErrEngineNotFound");
                RaiseSessionError(error);
                return false;
            }

            string args = BuildArguments(profileName, settings, apexProfileSlot);
            RaiseLogMessage(LocFormat("Loc_LogStartingBridge", enginePath, args));

            int timeoutSec = settings?.InitializationTimeoutSeconds ?? 20;
            BridgeSession? session = BridgeSession.TryStart(
                enginePath,
                args,
                TimeSpan.FromSeconds(timeoutSec),
                RaiseLogMessage,
                err => RaiseLogMessage("[ERROR] " + err),
                out error);

            if (session is null)
            {
                RaiseSessionError(error ?? Loc("Loc_ErrBridgeStartFailedShort"));
                return false;
            }

            lock (syncLock)
            {
                activeSession = session;
                activeGameTitle = gameTitle;
                activeProfile = profileName;
            }

            SessionStarted?.Invoke(gameTitle, profileName);
            return true;
        }
        finally
        {
            lock (syncLock)
            {
                isStarting = false;
            }
        }
    }

    /// <summary>
    /// 停止当前会话。**正常路径仍然优先协同停止**
    /// （15 秒上限，让引擎把手柄还原回原始状态），
    /// 但协同失败时会强制回收本次会话自己启动的子进程。
    ///
    /// <para>
    /// 🔴 <b>为什么不能只 StopAndWait（上游 issue #15 / 我们同款缺陷）：</b>
    /// 引擎若不响应 Stop 事件，<c>StopAndWait</c> 返回 false 之后我们就
    /// <c>Dispose()</c> 掉句柄 —— 那个孤儿进程会继续攥着全局会话锁
    /// <c>Local\ApexSenseBridge.ActiveSession.Owner.v1</c>，
    /// 于是用户看到的现象是"手柄突然再也启动不了桥接"，
    /// 而且重启 DIA 也没用（锁的属主还活着）。
    /// 强制回收的作用域严格限定在本次启动的那一个进程，不扫全系统。
    /// </para>
    /// </summary>
    public void StopSession(string reason)
    {
        BridgeSession? sessionToStop;
        lock (syncLock)
        {
            if (activeSession is null) return;
            sessionToStop = activeSession;
            activeSession = null;
            activeGameTitle = null;
            activeProfile = null;
        }

        RaiseLogMessage(LocFormat("Loc_LogStoppingSession", reason));

        bool clean = sessionToStop.StopAndEnsureExit(
            BridgeSession.DefaultStopTimeout, BridgeSession.ForcedStopOnFailure);

        if (!clean)
        {
            // 非干净退出要留痕：后续若出现"手柄不再被隐藏/会话锁残留"的报表，
            // 这条日志就是第一现场证据。
            RaiseLogMessage(Loc("Loc_ErrBridgeStopForced"));
        }

        sessionToStop.Dispose();

        SessionStopped?.Invoke(reason);
    }

    /// <summary>
    /// 只置停止事件、不等引擎退出。
    ///
    /// <para>
    /// 与 <see cref="StopSession"/> 的分工：
    /// 后者会阻塞到引擎走完 restore 流程（最长 15 秒，<c>apex_original_restored=yes</c>
    /// 这个验收硬指标依赖这段等待），因此**只能在线程池上调用**；
    /// 本方法只发信号，专给「进程自身正在退出、不允许再阻塞」的场景用
    /// （<c>AppDomain.ProcessExit</c> / 强杀 / 关机）。
    /// </para>
    ///
    /// <para>
    /// 刻意<span>不</span>把 <c>activeSession</c> 置空：进程马上就要结束，
    /// 没有后续查询需要一致性，反而留着一份引用便于日志留痕。
    /// </para>
    /// </summary>
    public void RequestStop(string reason)
    {
        BridgeSession? session;
        lock (syncLock)
        {
            session = activeSession;
        }

        if (session is null) return;

        RaiseLogMessage(LocFormat("Loc_LogStoppingSession", reason));
        session.RequestStop();
    }

    private void RaiseSessionError(string err) => SessionError?.Invoke(err);
    private void RaiseLogMessage(string msg) => LogMessage?.Invoke(msg);

    /// <summary>
    /// 探测「是否已有别的程序（Playnite 插件 / 官方托盘）持有会话」。
    /// 公开供诊断与自检使用，逻辑不变。
    ///
    /// <para>
    /// 语义要点（照搬上游，勿"简化"）：
    /// </para>
    /// <list type="bullet">
    /// <item>互斥体不存在 → 无人持有，可启动；</item>
    /// <item>能抢到 → 上一个持有者已退出，释放后视为空闲；</item>
    /// <item><c>AbandonedMutexException</c> → 上一个引擎**崩了**。
    /// 此时本线程已获得该互斥体，释放它并放行，让引擎自己的恢复标记去做清理；</item>
    /// <item><c>UnauthorizedAccessException</c> → **故障即拒绝**（fail closed）：
    /// 无法确认归属时，贸然起第二个引擎会抢手柄，宁可不启动。</item>
    /// </list>
    /// </summary>
    public static bool IsExternalSessionActive()
    {
        try
        {
            using Mutex sessionMutex = Mutex.OpenExisting(EngineSessionMutexName);
            try
            {
                if (!sessionMutex.WaitOne(0)) return true;
                sessionMutex.ReleaseMutex();
                return false;
            }
            catch (AbandonedMutexException)
            {
                sessionMutex.ReleaseMutex();
                return false;
            }
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return true;
        }
    }

    /// <summary>
    /// 拼装 <c>bridge-triggers</c> 参数。
    ///
    /// <para>
    /// <b>⚠️ 这是与 C++ 引擎（<c>engine/src/cli/BridgeCommand.cpp</c>）的硬契约，
    /// 参数顺序与拼写一个字符都不能动。</b>
    /// <c>--session-token</c> 由 <see cref="BridgeSession.TryStart"/> 追加，此处不要加。
    /// </para>
    /// </summary>
    private static string BuildArguments(string? profileName, TraySettings? settings, int apexProfileSlot)
    {
        var args = new List<string> { "bridge-triggers" };

        string profile = profileName?.ToLowerInvariant() ?? "standard";
        args.Add(profile switch
        {
            "spider-man-2" => "--touchpad-profile spider-man-2",
            "miles-morales" => "--touchpad-profile miles-morales",
            "ghost-of-tsushima" => "--touchpad-profile ghost-of-tsushima",
            "warframe" => "--touchpad-profile warframe",
            _ => "--touchpad-profile none",
        });

        if (settings is not null && settings.EnableRumble)
        {
            args.Add("--rumble");
            args.Add("--haptic-threshold");
            args.Add(settings.HapticThresholdPercent.ToString(CultureInfo.InvariantCulture));
        }

        if (apexProfileSlot is >= 1 and <= 4)
        {
            args.Add("--apex-profile");
            args.Add(apexProfileSlot.ToString(CultureInfo.InvariantCulture));
        }

        // APEX 4 陀螺仪灵敏度（引擎 1.0.0-beta.10 起，上游 issue #10）。
        // ⚠️ 只在非默认值时才追加：100 就是引擎默认值，不传与传 100 完全等价，
        //    少两个参数能让日志与旧版逐字可比（便于回归时对照 tray_bridge.log）。
        // ⚠️ 必须在这里夹紧而不是只靠界面：tray_settings.json 是用户可手改的，
        //    而引擎对越界值是【拒绝启动】——把一次误编辑变成"手柄连不上"太不划算。
        if (settings is not null)
        {
            int gyro = ClampGyroPercent(settings.Apex4GyroStrengthPercent);
            if (gyro != 100)
            {
                args.Add("--apex4-gyro-strength");
                args.Add(gyro.ToString(CultureInfo.InvariantCulture));
            }

            int gyroYaw = ClampGyroPercent(settings.Apex4GyroYawStrengthPercent);
            if (gyroYaw != 100)
            {
                args.Add("--apex4-gyro-yaw-strength");
                args.Add(gyroYaw.ToString(CultureInfo.InvariantCulture));
            }
        }

        return string.Join(" ", args);
    }

    /// <summary>陀螺仪灵敏度下限（与 C++ <c>BridgeOptions.cpp</c> 逐字一致）。</summary>
    public const int GyroPercentMin = 25;

    /// <summary>陀螺仪灵敏度上限（与 C++ <c>BridgeOptions.cpp</c> 逐字一致）。</summary>
    public const int GyroPercentMax = 400;

    /// <summary>把陀螺仪灵敏度夹到引擎允许的 25–400。越界会让引擎拒绝启动，必须夹。</summary>
    public static int ClampGyroPercent(int value)
        => value < GyroPercentMin ? GyroPercentMin
         : value > GyroPercentMax ? GyroPercentMax
         : value;

    private static string Loc(string key) => LocalizationService.Shared.Get(key);

    private static string LocFormat(string key, params object?[] args)
        => LocalizationService.Shared.Format(key, args);
}
