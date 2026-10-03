using System;
using System.Threading;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 会话归属探测 —— 与 DIA 主程序、官方托盘 <b>共用同一个命名互斥体</b>，
    /// 让三方互相感知，谁也不去抢手柄。
    ///
    /// <para>
    /// 互斥体由 <b>C++ 引擎自己</b>在有会话时创建并持有
    /// （<c>engine/src/platform/windows/WindowsSessionControl.cpp</c> 的
    /// <c>kGlobalOwnerMutexName</c>），不是应用层造的 ——
    /// 所以只要各方启动的都是同一个引擎二进制，互斥就自动成立。
    /// </para>
    ///
    /// <para>
    /// <b>⚠️ 名字一个字都不能改</b>：<c>Local\ApexSenseBridge.ActiveSession.Owner.v1</c>。
    /// 语义要点照搬 DIA 的 <c>EngineSessionManager.IsExternalSessionActive</c>：
    /// </para>
    /// <list type="bullet">
    /// <item>互斥体不存在 → 无人持有，可启动；</item>
    /// <item>能抢到 → 上一个持有者已退出，释放后视为空闲；</item>
    /// <item><c>AbandonedMutexException</c> → 上一个引擎<b>崩了</b>；本线程此时已获得互斥体，
    /// 释放并放行，让引擎自己的恢复标记去做清理；</item>
    /// <item><c>UnauthorizedAccessException</c> → <b>故障即拒绝</b>（fail closed）：
    /// 无法确认归属时，贸然起第二个引擎会抢手柄，宁可不启动。</item>
    /// </list>
    /// </summary>
    internal static class SessionOwnership
    {
        /// <summary>会话所有者互斥名（与 C++ 引擎、DIA 主程序逐字符一致）。</summary>
        internal const string MutexName = @"Local\ApexSenseBridge.ActiveSession.Owner.v1";

        /// <summary>是否已有别的程序持有会话。</summary>
        internal static bool IsExternalSessionActive()
        {
            try
            {
                using (Mutex sessionMutex = Mutex.OpenExisting(MutexName))
                {
                    try
                    {
                        if (!sessionMutex.WaitOne(0))
                        {
                            return true;
                        }
                        sessionMutex.ReleaseMutex();
                        return false;
                    }
                    catch (AbandonedMutexException)
                    {
                        sessionMutex.ReleaseMutex();
                        return false;
                    }
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
    }
}
