using System.Runtime.InteropServices;
using DsInApex.Core.Logging;
using Microsoft.Windows.AppNotifications;
using Microsoft.Windows.AppNotifications.Builder;

namespace DsInApex.App.Services;

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
/// （上游 <c>App.xaml.cs</c> 用的是 <c>NotifyIcon.ShowBalloonTip</c>，
/// 从 Windows Toast 通道迁移过来）。托盘气泡走 <c>Shell_NotifyIcon(NIF_INFO)</c>，
/// <b>完全不依赖 WindowsAppRuntime 通知通道</b>，且行为与官方版一致。
/// </para>
///
/// <para>
/// 本类保留 <see cref="AppNotificationManager"/> 通道的实现，仅用于：
/// 1. 诊断（确认该通道确实不可用，避免未来误判为代码 bug）；
/// 2. 万一将来改回 framework-dependent 部署，可零成本切回 Toast。
/// <b>托盘气泡通道在 P6 接入托盘图标时实现</b>（需要 <c>TaskbarIcon</c> 实例）。
/// </para>
/// </summary>
public sealed class NotificationService : IDisposable
{
    private const string LogFileName = "dsinapex_notify.log";

    private bool _disposed;

    /// <summary>是否已成功注册 Toast 通道（本项目部署模式下预期为 false）。</summary>
    public bool IsRegistered { get; private set; }

    /// <summary>注册失败时的原因（正常为 null）。</summary>
    public string? RegisterError { get; private set; }

    /// <summary>
    /// Toast 通道是否可用。为 false 时应走托盘气泡（见类注释）。
    /// </summary>
    public bool IsToastAvailable => IsRegistered;

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

    /// <summary>发送一条通知。返回是否成功交给系统。</summary>
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
        bool ok = TryRegister();

        var lines = new List<string>
        {
            $"Toast 通道    : {(ok ? "可用" : "不可用（预期）")}",
            $"当前 AUMID    : {CurrentAumid}",
        };

        if (!ok)
        {
            lines.Add($"失败原因      : {RegisterError}");
            lines.Add("结论          : self-contained 缺 WindowsAppRuntime.Insights.Resource.dll，属已知限制。");
            lines.Add("                DIA 改用托盘气泡通知（与上游 ShowBalloonTip 一致），不依赖本通道。");
        }

        return lines;
    }

    private void OnNotificationInvoked(AppNotificationManager sender, AppNotificationActivatedEventArgs args)
    {
        // P2 会在这里处理「点击通知打开对应页面」；当前只记日志
        AppLog.Info(LogFileName, $"通知被激活：{args.Argument}");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

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
