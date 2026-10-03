using System.Runtime.InteropServices;
using DsInApex.Core.Logging;
using H.NotifyIcon;
using H.NotifyIcon.Core;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DsInApex.App.Services;

/// <summary>通知的严重级别（决定气泡图标）。</summary>
public enum NotificationSeverity
{
    Info,
    Warning,
    Error,
}

/// <summary>
/// 通知服务。
///
/// <para>
/// <b>⚠️ 重要结论（2026-10-03 实测，P2 障碍清除时发现）：</b>
/// <b>Toast 通知（<see cref="AppNotificationManager"/>）在本项目的部署模式下不可用。</b>
/// </para>
///
/// <para>
/// <b>实测证据：</b><c>Register()</c> 抛
/// <c>COMException: 找不到指定的模块。Unable to load resource dll.
/// Microsoft.WindowsAppRuntime.Insights.Resource.dll</c>。
/// 追查确认：该 DLL <b>在 NuGet 包里根本不存在</b>（只有 <c>WindowsAppRuntimeInsights.h</c> 头文件），
/// 它属于 Windows App Runtime <b>安装包</b>的资源。而本机虽已安装 Windows App Runtime 2.5.1，
/// <b>self-contained 模式不查系统安装的运行时</b>，只用自己的副本 —— 副本里缺这个文件。
/// </para>
///
/// <para>
/// <b>因此 DIA 采用与上游一致的方案：托盘气泡通知</b>
/// （上游 <c>App.xaml.cs</c> 用的是 <c>NotifyIcon.ShowBalloonTip</c>）。
/// 托盘气泡走 <c>Shell_NotifyIcon(NIF_INFO)</c>，<b>完全不依赖 WindowsAppRuntime 通知通道</b>，
/// 零额外依赖，且行为与官方版一致。
/// </para>
///
/// <para>
/// <b>P6 落地：</b>通道由主窗口在创建托盘图标后经 <see cref="AttachTrayIcon"/> 注入。
/// 本类刻意<b>不</b>持有窗口引用，只在需要时调用托盘图标的方法 ——
/// 避免把 WinUI 窗口生命周期粘进一个 DI 单例里。
/// </para>
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const string LogFileName = "dsinapex_notify.log";

    /// <summary>
    /// 托盘图标的弱引用持有方式：这里用强引用，但由 <see cref="DetachTrayIcon"/> 在窗口销毁时解开。
    ///
    /// <para>
    /// 为什么不用 <c>WeakReference</c>：通知服务是 DI 单例，
    /// 而托盘图标是窗口的生命周期成员；只要窗口还活着就该拿到它。
    /// 窗口没了（正常退出路径）会显式 Detach，不存在泄漏窗口。
    /// </para>
    /// </summary>
    private TaskbarIcon? _trayIcon;

    private bool _disposed;

    /// <summary>是否已成功注册 Toast 通道（本项目部署模式下预期为 false）。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>注册失败时的原因（正常为 null）。</summary>
    public string? RegisterError { get; private set; }

    /// <summary>
    /// Toast 通道是否可用。为 false 时应走托盘气泡（见类注释）。
    /// </summary>
    public bool IsToastAvailable => IsRegistered;

    /// <summary>托盘气泡通道是否可用（P6 起为实际使用的通道）。</summary>
    public bool IsTrayAvailable => _trayIcon is { IsCreated: true };

    /// <summary>累计发送成功的气泡数（自检与排障用）。</summary>
    public int TrayNotificationsSent { get; private set; }

    /// <summary>把主窗口的托盘图标接入通知通道。重复接入以最后一次为准。</summary>
    public void AttachTrayIcon(TaskbarIcon trayIcon)
    {
        _trayIcon = trayIcon;
        AppLog.Info(LogFileName, $"通知通道已接入托盘图标（IsCreated={trayIcon.IsCreated}）");
    }

    /// <summary>窗口销毁时解除引用，避免单例继续指向已死的托盘图标。</summary>
    public void DetachTrayIcon()
    {
        _trayIcon = null;
    }

    /// <summary>
    /// 当前进程的显式 AUMID。
    ///
    /// ⚠️ 注意：WinUI/WindowsAppSDK **没有**托管 API 可读 AUMID
    ///（`AppNotificationManager` 上不存在 `GetCurrentAumid`），
    /// 只能走 Win32 的 <c>GetCurrentProcessExplicitAppUserModelID</c>。
    /// unpackaged 应用若从未显式设置过，这里会返回「未显式设置」——
    /// 这本身是有价值的诊断信息（说明 AUMID 由系统按 exe 推导）。
    /// </summary>
    public static string CurrentAumid
    {
        get
        {
            IntPtr ptr = IntPtr.Zero;
            try
            {
                int hr = GetCurrentProcessExplicitAppUserModelID(out ptr);
                if (hr < 0 || ptr == IntPtr.Zero)
                {
                    return "(未显式设置 AUMID，由系统按 exe 推导)";
                }

                string? aumid = Marshal.PtrToStringUni(ptr);
                return string.IsNullOrEmpty(aumid) ? "(空)" : aumid;
            }
            catch (Exception ex)
            {
                return $"(取不到: {ex.GetType().Name})";
            }
            finally
            {
                if (ptr != IntPtr.Zero) Marshal.FreeCoTaskMem(ptr);
            }
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int GetCurrentProcessExplicitAppUserModelID(out IntPtr appId);

    // ══════════════════════ 托盘气泡（P6 起的主通道） ══════════════════════

    /// <summary>
    /// 发一条托盘气泡通知。返回是否成功交给系统。
    ///
    /// <para>
    /// ⚠️ 必须在 UI 线程调用：<c>Shell_NotifyIcon</c> 的消息窗口归创建它的线程所有。
    /// 调用方（会话事件来自后台线程）需自行经 <c>DispatcherQueue</c> 回到 UI 线程。
    /// 本方法<b>不</b>帮忙做线程调度 —— 静默切换线程会让调用方误以为同步完成。
    /// </para>
    /// </summary>
    public bool Notify(NotificationSeverity severity, string title, string message)
    {
        if (_trayIcon is null || !_trayIcon.IsCreated)
        {
            AppLog.Warn(LogFileName, $"托盘通知通道不可用，丢弃通知：{title} / {message}");
            return false;
        }

        try
        {
            _trayIcon.ShowNotification(title, message, MapIcon(severity));
            TrayNotificationsSent++;

            AppLog.Info(LogFileName, $"托盘气泡已发送：{title} / {message.Replace('\n', ' ')}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"托盘气泡发送失败：{AppLog.Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 受开关约束的通知：<paramref name="enabled"/> 为 false 时直接跳过。
    ///
    /// <para>
    /// 把「有没有开通知」的判断收在这一处，而不是散落在每个事件处理器里 ——
    /// 上游 WPF 版每个订阅点都写了一遍 <c>if (settings.EnableNotifications)</c>，
    /// 漏一处就会出现「明明关了通知还在弹」。
    /// </para>
    /// </summary>
    public bool NotifyIfEnabled(bool enabled, NotificationSeverity severity, string title, string message)
        => enabled && Notify(severity, title, message);

    private static NotificationIcon MapIcon(NotificationSeverity severity) => severity switch
    {
        NotificationSeverity.Warning => NotificationIcon.Warning,
        NotificationSeverity.Error => NotificationIcon.Error,
        _ => NotificationIcon.Info,
    };

    // ══════════════════════ Toast 通道（保留但不用） ══════════════════════

    /// <summary>
    /// 注册通知通道并挂接激活回调。必须在发送通知之前调用一次。
    /// </summary>
    public bool TryRegister()
    {
        if (IsRegistered) return true;

        try
        {
            AppNotificationManager manager = AppNotificationManager.Default;
            manager.NotificationInvoked += OnNotificationInvoked;
            manager.Register();

            IsRegistered = true;
            RegisterError = null;
            AppLog.Info(LogFileName, $"通知通道已注册，AUMID={CurrentAumid}");
            return true;
        }
        catch (Exception ex)
        {
            IsRegistered = false;
            RegisterError = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"通知通道注册失败：{RegisterError}");
            return false;
        }
    }

    /// <summary>发送一条 Toast 通知。返回是否成功交给系统。</summary>
    public bool TryShow(string title, string message)
    {
        if (!TryRegister()) return false;

        try
        {
            AppNotification notification = new AppNotificationBuilder()
                .AddText(title)
                .AddText(message)
                .BuildNotification();

            AppNotificationManager.Default.Show(notification);
            AppLog.Info(LogFileName, $"通知已发送：{title} / {message}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"通知发送失败：{AppLog.Describe(ex)}");
            return false;
        }
    }

    /// <summary>
    /// 输出可核查的诊断快照（供自检与 P8 排障使用）。
    /// </summary>
    public IReadOnlyList<string> Diagnose()
    {
        bool toastOk = TryRegister();

        var lines = new List<string>
        {
            $"Toast 通道    : {(toastOk ? "可用" : "不可用（预期）")}",
            $"托盘气泡通道  : {(IsTrayAvailable ? "已接入且可用" : "未接入（窗口尚未创建托盘图标）")}",
            $"已发送气泡数  : {TrayNotificationsSent}",
            $"当前 AUMID    : {CurrentAumid}",
        };

        if (!toastOk)
        {
            lines.Add($"Toast 失败原因: {RegisterError}");
            lines.Add("结论          : self-contained 缺 WindowsAppRuntime.Insights.Resource.dll，属已知限制。");
            lines.Add("                DIA 走托盘气泡通知（与上游 ShowBalloonTip 一致），不依赖本通道。");
        }

        return lines;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // Toast 通道未启用；保留回调以便将来切回 framework-dependent 部署时可用
        AppLog.Info(LogFileName, $"通知被激活：{args.Argument}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        DetachTrayIcon();

        if (!IsRegistered) return;

        try
        {
            AppNotificationManager.Default.NotificationInvoked -= OnNotificationInvoked;
            AppNotificationManager.Default.Unregister();
            AppLog.Info(LogFileName, "通知通道已注销");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"通知通道注销失败：{AppLog.Describe(ex)}");
        }

        IsRegistered = false;
    }
}
