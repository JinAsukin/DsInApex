using System.Diagnostics;
using System.IO.MemoryMappedFiles;
using System.Text;
using DsInApex.Core.Localization;

namespace DsInApex.Core.Services;

/// <summary>
/// 桥接会话阶段。数值与 C++ 引擎的 <c>SessionPhase</c>
/// （<c>engine/src/platform/SessionControl.h</c>）**必须逐值一致**。
/// </summary>
public enum SessionPhase : ushort
{
    Empty = 0,
    Starting = 1,
    Ready = 2,
    Stopping = 3,
    Stopped = 4,
    Failed = 5,
}

/// <summary>
/// 一次桥接会话：负责启动引擎进程、建立 IPC 命名对象、等待就绪、请求停止。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Common/BridgeSession.cs</c>（243 行）。
/// <b>本类只做「哑进程管理 + IPC」，不掺任何业务状态</b> ——
/// 业务状态与事件在 <see cref="EngineSessionManager"/>，这个分层是上游刻意设计的，不要合并。
/// </para>
///
/// <para>
/// <b>⚠️ 与 C++ 引擎的硬契约（改一个字都会静默失效）：</b>
/// </para>
/// <list type="bullet">
/// <item>命名对象前缀 <c>Local\ApexSenseBridge.Session.&lt;token&gt;.{Ready|Stop|Status}</c></item>
/// <item>token = <c>Guid.NewGuid().ToString("N")</c>（32 位小写 hex，C++ 侧 isValidSessionToken 校验长度 32 + [0-9a-fA-F]）</item>
/// <item>状态块 512 字节：magic@0(4) = 0x53425341 · version@4(2) = 1 · phase@6(2) · exitCode@8(4) · messageLength@12(4) · message@16(496, UTF-8)</item>
/// </list>
///
/// <para>
/// <b>为什么坚持手写字节偏移而不改成结构体 marshal：</b>跨语言二进制布局下，
/// 手写偏移的失败模式是「读到明显错误的数值」（易发现），而 marshal 的失败模式是
/// 「静默错位」（难发现）。这个取舍已在 <c>docs/05-P2迁移定工报告.md</c> §7.2 定案。
/// </para>
/// </summary>
public sealed class BridgeSession : IDisposable
{
    /// <summary>状态块魔数 <c>"ASBS"</c>（与 C++ <c>kSessionStatusMagic</c> 一致）。</summary>
    private const uint StatusMagic = 0x53425341;

    /// <summary>IPC 协议版本（与 C++ <c>kSessionProtocolVersion</c> 一致）。</summary>
    private const ushort ProtocolVersion = 1;

    /// <summary>状态块字节数（与 C++ <c>kSessionStatusSize</c> 一致）。</summary>
    private const int StatusSize = 512;

    /// <summary>命名对象前缀（与 C++ <c>objectName()</c> 逐字符一致）。</summary>
    private const string ObjectPrefix = "Local\\ApexSenseBridge.Session.";

    /// <summary>
    /// 正常停止的等待上限。**不要为了"快"改小** ——
    /// 引擎要靠这段时间走完 neutralize / restore 流程把手柄还原
    /// （<c>apex_original_restored=yes</c> 是 P8 验收硬指标）。
    /// </summary>
    public static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

    /// <summary>
    /// <b>启动失败路径</b>专用的协同停止等待（远短于 <see cref="DefaultStopTimeout"/>）。
    ///
    /// <para>
    /// 为什么可以短：这条路径上引擎<b>从未报告过 Ready</b>，
    /// 也就不存在"需要时间把手柄从虚拟态还原"的前提 —— 它要么还在初始化、
    /// 要么已经卡死。为它等满 15 秒只会让"点一下启动 → 卡 15 秒 → 失败"的体感更糟。
    /// 上游 issue #15 的修复用的也是 (3s, 5s) 这一对量级。
    /// </para>
    /// </summary>
    public static readonly TimeSpan CooperativeStopOnFailure = TimeSpan.FromSeconds(3);

    /// <summary>强制结束后等待子进程真正消失的时间（配合 <see cref="CooperativeStopOnFailure"/>）。</summary>
    public static readonly TimeSpan ForcedStopOnFailure = TimeSpan.FromSeconds(5);

    private readonly Action<string> logInfo;
    private readonly Action<string> logError;
    private readonly EventWaitHandle readyEvent;
    private readonly EventWaitHandle stopEvent;
    private readonly MemoryMappedFile statusMapping;
    private readonly MemoryMappedViewAccessor statusView;
    private readonly Process process;
    private bool disposed;

    /// <summary>本次会话的 IPC token（32 位 hex）。仅用于诊断展示。</summary>
    public string Token { get; }

    /// <summary>引擎进程 PID；进程已退出时返回 0。</summary>
    public int ProcessId
    {
        get
        {
            try
            {
                return (process is not null && !process.HasExited) ? process.Id : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>读取一次状态块（供仪表盘展示当前 phase）。</summary>
    public SessionStatus ReadStatus() => ReadStatusCore();

    private BridgeSession(
        string token,
        Action<string> logInfo,
        Action<string> logError,
        EventWaitHandle readyEvent,
        EventWaitHandle stopEvent,
        MemoryMappedFile statusMapping,
        MemoryMappedViewAccessor statusView,
        Process process)
    {
        Token = token;
        this.logInfo = logInfo ?? (_ => { });
        this.logError = logError ?? (_ => { });
        this.readyEvent = readyEvent;
        this.stopEvent = stopEvent;
        this.statusMapping = statusMapping;
        this.statusView = statusView;
        this.process = process;
    }

    /// <summary>
    /// 启动引擎进程并等待其报告就绪。
    /// 失败时返回 <c>null</c>，并把可读原因写入 <paramref name="error"/>。
    /// </summary>
    public static BridgeSession? TryStart(
        string executablePath,
        string bridgeArguments,
        TimeSpan timeout,
        Action<string>? logInfo,
        Action<string>? logError,
        out string? error)
    {
        error = null;
        EventWaitHandle? ready = null;
        EventWaitHandle? stop = null;
        MemoryMappedFile? mapping = null;
        MemoryMappedViewAccessor? view = null;
        Process? process = null;
        string token = string.Empty;

        try
        {
            token = Guid.NewGuid().ToString("N");
            string prefix = ObjectPrefix + token;

            ready = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Ready", out bool readyCreated);
            stop = new EventWaitHandle(false, EventResetMode.ManualReset, prefix + ".Stop", out bool stopCreated);
            if (!readyCreated || !stopCreated)
            {
                throw new InvalidOperationException(Loc("Loc_ErrBridgeIpcCollision"));
            }

            // ⚠️ 状态块必须 CreateNew 512 字节：引擎按固定布局打开并写入
            mapping = MemoryMappedFile.CreateNew(prefix + ".Status", StatusSize, MemoryMappedFileAccess.ReadWrite);
            view = mapping.CreateViewAccessor(0, StatusSize, MemoryMappedFileAccess.ReadWrite);

            var startInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = bridgeArguments + " --session-token " + token,
                WorkingDirectory = Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };

            process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) logInfo?.Invoke("[bridge] " + args.Data);
            };
            process.ErrorDataReceived += (_, args) =>
            {
                if (!string.IsNullOrWhiteSpace(args.Data)) logError?.Invoke("[bridge] " + args.Data);
            };

            if (!process.Start())
            {
                throw new InvalidOperationException(Loc("Loc_ErrBridgeProcessNotStarted"));
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var session = new BridgeSession(token, logInfo!, logError!, ready, stop, mapping, view, process);

            // ⚠️ 所有权移交：置空本地引用，catch 块就不会把刚移交的对象 Dispose 掉。
            // 这是上游最容易写错的地方之一，照搬不要"优化"。
            ready = null;
            stop = null;
            mapping = null;
            view = null;
            process = null;

            if (!session.WaitUntilReady(timeout, out error))
            {
                // ⚠️ 这里【不能】只 StopAndWait：启动超时的引擎子进程恰恰是
                // 「最有可能不响应 Stop 事件」的那一类（上游 issue #15）。
                // 协同停不下来时必须强制回收，否则孤儿进程会攥住全局会话锁，
                // 让之后每一次启动都误判成"外部会话正在运行"。
                session.StopAndEnsureExit(CooperativeStopOnFailure, ForcedStopOnFailure);
                session.Dispose();
                return null;
            }

            return session;
        }
        catch (Exception exception)
        {
            error = Loc("Loc_ErrBridgeStartFailed") + exception.Message;
            logError?.Invoke(error);

            // 同上：异常路径也可能留下已经起了一半的子进程，先杀掉再放句柄。
            KillQuietly(process);
            process?.Dispose();
            view?.Dispose();
            mapping?.Dispose();
            stop?.Dispose();
            ready?.Dispose();
            return null;
        }
    }

    /// <summary>异常清理路径：尽力结束子进程，任何失败都不再抛出（此时已在收尾）。</summary>
    private static void KillQuietly(Process? target)
    {
        if (target is null)
        {
            return;
        }

        try
        {
            if (!target.HasExited)
            {
                target.Kill();
                target.WaitForExit((int)ForcedStopOnFailure.TotalMilliseconds);
            }
        }
        catch
        {
            // 已经退出 / 句柄失效 / 权限不足 —— 收尾阶段没有比"继续 Dispose"更好的处理
        }
    }

    /// <summary>
    /// 只置停止事件、**不等待**进程退出。
    ///
    /// <para>
    /// 供「进程自身已经在退出路径上」的场景使用（<c>AppDomain.ProcessExit</c>、
    /// 任务管理器结束进程、系统关机）。那种时刻**绝不能**调 <see cref="StopAndWait"/> ——
    /// 它会阻塞最长 15 秒，而 CLR 会在 ProcessExit 处理器里等这段代码跑完，
    /// 结果就是进程看起来「关不掉」。
    /// </para>
    ///
    /// <para>
    /// 引擎收到 Stop 事件后依然会自行走完 neutralize / restore 流程并退出；
    /// 即使它中途异常，还有引擎自带的 <c>hidhide-watchdog</c> 兜底恢复手柄可见性
    /// （属主进程消失即触发）。所以「不等」不会把手柄卡在虚拟态。
    /// </para>
    /// </summary>
    public void RequestStop()
    {
        if (disposed) return;

        try
        {
            stopEvent.Set();
        }
        catch (ObjectDisposedException)
        {
            // 事件句柄已随会话释放 —— 没有可通知的对象了
        }
    }

    /// <summary>
    /// 请求引擎停止并等待其退出。
    /// <b>正常停止路径</b>：置 Stop 事件 → 等引擎自行走完还原流程；<c>Kill</c> 才是最后手段。
    /// </summary>
    public bool StopAndWait(TimeSpan timeout)
    {
        if (disposed) return true;

        try
        {
            stopEvent.Set();
            if (process.HasExited) return true;

            bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);

            // 进程已退出时，再用**无参重载**等一次：它会等异步输出回调把管道里
            // 剩余数据读完。缺少这一步会偶发丢掉最后几行 —— 而被丢掉的往往正是
            // apex_original_restored / audio_haptics_* 这些验收指标。
            // （同一坑 DIA 的 EngineRunner 已经踩过，见该类注释。）
            if (exited)
            {
                process.WaitForExit();
            }

            return exited;
        }
        catch (Exception exception)
        {
            logError?.Invoke(Loc("Loc_ErrBridgeStopFailed") + exception.Message);
            return false;
        }
    }

    /// <summary>
    /// 协同停止超时后，强制结束<b>本会话自己启动的那一个</b>子进程。
    ///
    /// <para>
    /// 🔴 <b>为什么必须有这个方法（上游 issue #15，我们同款缺陷）：</b>
    /// 启动阶段超时或异常时，如果只是 <c>Dispose()</c> 掉 <see cref="Process"/> 句柄，
    /// 那个<b>没响应 Stop 事件的引擎子进程会成为孤儿</b> —— 它手上还攥着全局会话锁
    /// <c>Local\ApexSenseBridge.ActiveSession.Owner.v1</c>，于是此后每一次启动
    /// 都会被判成"已有外部会话在跑"，用户看到的是"手柄突然再也连不上了"。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 作用域严格限定在<b>由本对象启动的那个进程</b>（不按名字扫、不扫全系统）：
    /// 既避免误杀用户自己开的桥接，也避免误杀飞智空间站之类的无关进程。
    /// </para>
    ///
    /// <para>
    /// 返回值语义与上游一致：<c>true</c> = 协同停止成功；
    /// <c>false</c> = 走了强制路径（调用方应据此报告"非干净退出"），
    /// 但不代表失败 —— 手柄可见性由引擎自带的 <c>hidhide-watchdog</c> 兜底恢复。
    /// </para>
    /// </summary>
    /// <param name="cooperativeTimeout">先给引擎自行收尾的时间。</param>
    /// <param name="forcedTimeout">强制结束后等待进程真正消失的时间。</param>
    public bool StopAndEnsureExit(TimeSpan cooperativeTimeout, TimeSpan forcedTimeout)
    {
        if (StopAndWait(cooperativeTimeout))
        {
            return true;
        }

        try
        {
            if (!process.HasExited)
            {
                logError?.Invoke(Loc("Loc_ErrBridgeStopForced"));
                process.Kill();
                if (!process.WaitForExit((int)forcedTimeout.TotalMilliseconds))
                {
                    logError?.Invoke(Loc("Loc_ErrBridgeStopForcedStuck"));
                }
            }
        }
        catch (Exception exception)
        {
            logError?.Invoke(Loc("Loc_ErrBridgeStopForcedError") + exception.Message);
        }

        return false;
    }

    private bool WaitUntilReady(TimeSpan timeout, out string? error)
    {
        DateTime deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (readyEvent.WaitOne(100))
            {
                return ReadReadyStatus(out error);
            }

            if (process.HasExited)
            {
                error = LocFormat("Loc_ErrBridgeExited", process.ExitCode);
                return false;
            }
        }

        error = LocFormat("Loc_ErrBridgeInitTimeout", (int)timeout.TotalSeconds);
        return false;
    }

    private bool ReadReadyStatus(out string? error)
    {
        SessionStatus status = ReadStatusCore();

        if (status.Magic != StatusMagic || status.ProtocolVersion != ProtocolVersion)
        {
            error = Loc("Loc_ErrBridgeIpcIncompatible");
            return false;
        }

        if (status.Phase == SessionPhase.Ready)
        {
            error = null;
            return true;
        }

        error = string.IsNullOrWhiteSpace(status.Message)
            ? LocFormat("Loc_ErrBridgeInitFailed", status.ExitCode)
            : status.Message;
        return false;
    }

    /// <summary>
    /// 按 C++ 布局手写偏移读取状态块。
    /// 偏移量见类注释；messageLength 上限取 495（引擎侧容量 496，留 1 字节给终止符）。
    /// </summary>
    private SessionStatus ReadStatusCore()
    {
        uint magic = statusView.ReadUInt32(0);
        ushort version = statusView.ReadUInt16(4);
        var phase = (SessionPhase)statusView.ReadUInt16(6);
        int exitCode = statusView.ReadInt32(8);
        uint messageLength = Math.Min(statusView.ReadUInt32(12), 495u);

        var messageBytes = new byte[(int)messageLength];
        if (messageLength > 0)
        {
            statusView.ReadArray(16, messageBytes, 0, messageBytes.Length);
        }

        return new SessionStatus(
            magic, version, phase, exitCode, Encoding.UTF8.GetString(messageBytes));
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;

        try
        {
            process?.Dispose();
            statusView?.Dispose();
            statusMapping?.Dispose();
            stopEvent?.Dispose();
            readyEvent?.Dispose();
        }
        catch
        {
            // 释放阶段的异常无需上报
        }
    }

    private static string Loc(string key) => LocalizationService.Shared.Get(key);

    private static string LocFormat(string key, params object?[] args)
        => LocalizationService.Shared.Format(key, args);
}

/// <summary>状态块的一次读取快照（供诊断与仪表盘展示）。</summary>
public readonly record struct SessionStatus(
    uint Magic,
    ushort ProtocolVersion,
    SessionPhase Phase,
    int ExitCode,
    string Message)
{
    /// <summary>魔数是否合法（<c>"ASBS"</c>）。</summary>
    public bool MagicValid => Magic == 0x53425341;
}
