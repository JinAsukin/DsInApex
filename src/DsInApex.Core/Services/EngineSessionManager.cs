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
    /// 停止当前会话。**必须走 StopAndWait 的正常路径**，
    /// 让引擎把手柄还原回原始状态。
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

        sessionToStop.StopAndWait(BridgeSession.DefaultStopTimeout);
        sessionToStop.Dispose();

        SessionStopped?.Invoke(reason);
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

        return string.Join(" ", args);
    }

    private static string Loc(string key) => LocalizationService.Shared.Get(key);

    private static string LocFormat(string key, params object?[] args)
        => LocalizationService.Shared.Format(key, args);
}
