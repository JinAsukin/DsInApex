using System;
using System.Diagnostics;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Threading;
using Playnite.SDK;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 桥接会话阶段。数值与 C++ 引擎的 <c>SessionPhase</c>
    /// （<c>engine/src/platform/SessionControl.h</c>）<b>必须逐值一致</b>。
    /// </summary>
    internal enum SessionPhase : ushort
    {
        Empty = 0,
        Starting = 1,
        Ready = 2,
        Stopping = 3,
        Stopped = 4,
        Failed = 5
    }

    /// <summary>
    /// 一次桥接会话：启动引擎进程、建立 IPC 命名对象、等待就绪、请求停止。
    ///
    /// <para>
    /// <b>本类只做「哑进程管理 + IPC」，不掺业务状态</b> —— 业务状态在
    /// <see cref="DsInApexPlugin"/>。这个分层是上游刻意设计的，不要合并。
    /// </para>
    ///
    /// <para><b>⚠️ 与 C++ 引擎的硬契约（改一个字都会静默失效）：</b></para>
    /// <list type="bullet">
    /// <item>命名对象前缀 <c>Local\ApexSenseBridge.Session.&lt;token&gt;.{Ready|Stop|Status}</c></item>
    /// <item>token = <c>Guid.NewGuid().ToString("N")</c>（32 位小写 hex）</item>
    /// <item>状态块 512 字节：magic@0(4)=0x53425341 · version@4(2)=1 · phase@6(2) ·
    /// exitCode@8(4) · messageLength@12(4) · message@16(496, UTF-8)</item>
    /// </list>
    ///
    /// <para>
    /// <b>为什么手写字节偏移而不改成结构体 marshal：</b>跨语言二进制布局下，
    /// 手写偏移的失败模式是「读到明显错误的数值」（易发现），marshal 的失败模式是
    /// 「静默错位」（难发现）。此取舍已在 <c>docs/05-P2迁移定工报告.md</c> §7.2 定案。
    /// </para>
    /// </summary>
    internal sealed class BridgeSession : IDisposable
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
        /// 正常停止的等待上限。<b>不要为了"快"改小</b> ——
        /// 引擎要靠这段时间走完 neutralize / restore 流程把手柄还原
        /// （<c>apex_original_restored=yes</c> 是 P8 验收硬指标）。
        /// </summary>
        internal static readonly TimeSpan DefaultStopTimeout = TimeSpan.FromSeconds(15);

        private readonly ILogger logger;
        private readonly EventWaitHandle readyEvent;
        private readonly EventWaitHandle stopEvent;
        private readonly MemoryMappedFile statusMapping;
        private readonly MemoryMappedViewAccessor statusView;
        private readonly Process process;
        private bool disposed;

        /// <summary>本次会话的 IPC token（32 位 hex），仅用于诊断展示。</summary>
        internal string Token { get; private set; }

        /// <summary>引擎进程 PID；进程已退出时返回 0。</summary>
        internal int ProcessId
        {
            get
            {
                try
                {
                    return (process != null && !process.HasExited) ? process.Id : 0;
                }
                catch
                {
                    return 0;
                }
            }
        }

        private BridgeSession(
            string token,
            ILogger logger,
            EventWaitHandle readyEvent,
            EventWaitHandle stopEvent,
            MemoryMappedFile statusMapping,
            MemoryMappedViewAccessor statusView,
            Process process)
        {
            Token = token;
            this.logger = logger;
            this.readyEvent = readyEvent;
            this.stopEvent = stopEvent;
            this.statusMapping = statusMapping;
            this.statusView = statusView;
            this.process = process;
        }

        /// <summary>
        /// 启动引擎进程并等待其报告就绪。失败时返回 <c>null</c>，
        /// 并把可读原因（已本地化）写入 <paramref name="error"/>。
        /// </summary>
        internal static BridgeSession TryStart(
            string executablePath,
            string bridgeArguments,
            TimeSpan timeout,
            ILogger logger,
            out string error)
        {
            error = null;
            EventWaitHandle ready = null;
            EventWaitHandle stop = null;
            MemoryMappedFile mapping = null;
            MemoryMappedViewAccessor view = null;
            Process process = null;
            string token = string.Empty;

            try
            {
                token = Guid.NewGuid().ToString("N");
                string prefix = ObjectPrefix + token;

                bool readyCreated;
                bool stopCreated;
                ready = new EventWaitHandle(false, EventResetMode.ManualReset,
                                            prefix + ".Ready", out readyCreated);
                stop = new EventWaitHandle(false, EventResetMode.ManualReset,
                                           prefix + ".Stop", out stopCreated);
                if (!readyCreated || !stopCreated)
                {
                    throw new InvalidOperationException(Loc.Get("LOCDsInApex_BridgeIpcCollision"));
                }

                // ⚠️ 状态块必须 CreateNew 512 字节：引擎按固定布局打开并写入
                mapping = MemoryMappedFile.CreateNew(prefix + ".Status", StatusSize,
                                                     MemoryMappedFileAccess.ReadWrite);
                view = mapping.CreateViewAccessor(0, StatusSize, MemoryMappedFileAccess.ReadWrite);

                var startInfo = new ProcessStartInfo
                {
                    FileName = executablePath,
                    Arguments = bridgeArguments + " --session-token " + token,
                    WorkingDirectory = Path.GetDirectoryName(executablePath),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    StandardOutputEncoding = Encoding.UTF8,
                    StandardErrorEncoding = Encoding.UTF8
                };

                process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
                process.OutputDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        logger.Debug("[bridge] " + args.Data);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        logger.Error("[bridge] " + args.Data);
                    }
                };

                if (!process.Start())
                {
                    throw new InvalidOperationException(Loc.Get("LOCDsInApex_BridgeProcessNotStarted"));
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                var session = new BridgeSession(token, logger, ready, stop, mapping, view, process);

                // ⚠️ 所有权移交：置空本地引用，catch 块就不会把刚移交的对象 Dispose 掉。
                // 这是上游最容易写错的地方之一，照搬不要"优化"。
                ready = null;
                stop = null;
                mapping = null;
                view = null;
                process = null;

                if (!session.WaitUntilReady(timeout, out error))
                {
                    session.StopAndWait(DefaultStopTimeout);
                    session.Dispose();
                    return null;
                }

                return session;
            }
            catch (Exception exception)
            {
                error = Loc.Get("LOCDsInApex_ErrBridgeStartFailed") + " " + exception.Message;
                logger.Error(exception, error);

                if (process != null) process.Dispose();
                if (view != null) view.Dispose();
                if (mapping != null) mapping.Dispose();
                if (stop != null) stop.Dispose();
                if (ready != null) ready.Dispose();
                return null;
            }
        }

        /// <summary>
        /// 请求引擎停止并等待其退出。
        /// <b>正常停止路径</b>：置 Stop 事件 → 等引擎自行走完还原流程；<c>Kill</c> 才是最后手段。
        /// </summary>
        internal bool StopAndWait(TimeSpan timeout)
        {
            if (disposed)
            {
                return true;
            }

            try
            {
                stopEvent.Set();
                if (process.HasExited)
                {
                    return true;
                }

                bool exited = process.WaitForExit((int)timeout.TotalMilliseconds);

                // 进程已退出时，再用**无参重载**等一次：它会等异步输出回调把管道里
                // 剩余数据读完。缺少这一步会偶发丢掉最后几行 —— 而被丢掉的往往正是
                // apex_original_restored / audio_haptics_* 这些验收指标。
                // （同一坑 DIA 的 EngineRunner 已经踩过。）
                if (exited)
                {
                    process.WaitForExit();
                }

                return exited;
            }
            catch (Exception exception)
            {
                logger.Error(exception, "Failed to stop the Ds in Apex bridge session.");
                return false;
            }
        }

        private bool WaitUntilReady(TimeSpan timeout, out string error)
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
                    error = Loc.Format("LOCDsInApex_BridgeExited", process.ExitCode);
                    return false;
                }
            }

            error = Loc.Format("LOCDsInApex_BridgeInitTimeout", (int)timeout.TotalSeconds);
            return false;
        }

        private bool ReadReadyStatus(out string error)
        {
            uint magic = statusView.ReadUInt32(0);
            ushort version = statusView.ReadUInt16(4);
            var phase = (SessionPhase)statusView.ReadUInt16(6);
            int exitCode = statusView.ReadInt32(8);
            uint messageLength = Math.Min(statusView.ReadUInt32(12), 495u);

            string message = string.Empty;
            if (messageLength > 0)
            {
                var messageBytes = new byte[(int)messageLength];
                statusView.ReadArray(16, messageBytes, 0, messageBytes.Length);
                message = Encoding.UTF8.GetString(messageBytes);
            }

            if (magic != StatusMagic || version != ProtocolVersion)
            {
                error = Loc.Get("LOCDsInApex_BridgeIpcIncompatible");
                return false;
            }

            if (phase == SessionPhase.Ready)
            {
                error = null;
                return true;
            }

            error = string.IsNullOrWhiteSpace(message)
                ? Loc.Format("LOCDsInApex_BridgeInitFailed", exitCode)
                : message;
            return false;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }
            disposed = true;

            try
            {
                process.Dispose();
                statusView.Dispose();
                statusMapping.Dispose();
                stopEvent.Dispose();
                readyEvent.Dispose();
            }
            catch
            {
                // 释放阶段的异常无需上报
            }
        }
    }
}
