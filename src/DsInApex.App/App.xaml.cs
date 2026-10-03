using DsInApex.App.Services;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Xaml;

namespace DsInApex.App;

/// <summary>
/// 应用入口。
///
/// 启动顺序（不可调换）：
/// 1. 单实例守卫
/// 2. 构建 DI 容器
/// 3. **初始化语言** —— 必须在创建窗口之前，
///    因为 XAML 附加属性在解析阶段就要取到正确文案
/// 4. 创建并激活主窗口
/// </summary>
public partial class App : Application
{
    private const string LogFileName = "dsinapex_app.log";

    /// <summary>
    /// 单实例互斥量。**必须是静态字段** —— 存为局部变量会被 GC 回收，
    /// 守卫随即失效（Spike 已踩过这个坑）。
    /// </summary>
    private static Mutex? _singleInstanceMutex;

    private Window? _window;

    /// <summary>
    /// 主窗口静态引用。
    ///
    /// <para>
    /// 用途：unpackaged 下 WinRT 选择器（<c>FileSavePicker</c> 等）必须经
    /// <c>InitializeWithWindow.Initialize(picker, hwnd)</c> 绑定一个窗口句柄，
    /// 而页面无法访问 <see cref="Application"/> 的私有窗口字段。
    /// </para>
    /// </summary>
    public static Window? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();

        // 兜底：未捕获异常写日志，便于 P8 实测排障时定位
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ── 0. 辅助模式（P4）：提权安装驱动 ──
        //     被自身以管理员身份重启时走这条路：不创建窗口，装完即退出。
        //     ⚠️ 必须排在单实例守卫**之前**，否则提权实例会被主实例的互斥量挡掉。
        string? helperDriverId = GetDriverActionTarget();
        if (helperDriverId is not null)
        {
            RunDriverInstallHelper(helperDriverId);
            return;
        }

        // ── 1. 单实例守卫（P1-6） ──
        // 用 Local\ 前缀而非 Global\，避免非管理员会话下因权限不足而失败
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\DsInApex.SingleInstance",
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            AppLog.Info(LogFileName, "检测到已有实例在运行，本次启动直接退出");
            Exit();
            return;
        }

        // ── 2. 构建 DI 容器 ──
        AppHost host = AppHost.Build();

        // ── 3. 语言初始化（必须在窗口创建前） ──
        TraySettings settings = host.GetRequiredService<TraySettings>();
        LocalizationService localization = host.GetRequiredService<LocalizationService>();
        localization.Initialize(settings.Language);

        AppLog.Info(LogFileName,
            $"启动：语言={localization.CurrentLanguage} 主题={settings.Theme} " +
            $"设置文件={TraySettings.FilePath}");

        // ── 4. 主窗口 ──
        var mainWindow = new MainWindow();
        _window = mainWindow;
        MainWindow = _window;
        _window.Activate();

        // ── 5. 开机自启模式（P6）：窗口建好后立刻藏进托盘 ──
        //     顺序不可调换 —— 必须等 Activate() 走完（窗口资源真正就绪）再隐藏，
        //     否则托盘图标与后续导航可能踩到未初始化的窗口状态。
        bool autoStarted = AutoStartService.StartedByAutoStart();
        if (autoStarted)
        {
            AppLog.Info(LogFileName, "以 --autostart 启动：转入托盘后台模式");
            mainWindow.HideToTray();
        }

        AppLog.Info(LogFileName, $"启动完成（自启模式={autoStarted}）");
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error(LogFileName, $"未处理异常：{AppLog.Describe(e.Exception)}");

        // 刻意不设 e.Handled = true：带病继续运行比重启更容易掩盖问题，
        // P1 阶段宁可让它崩掉并留下日志。
    }

    // ────────────────────────── P4 · 驱动安装辅助模式 ──────────────────────────

    /// <summary>
    /// 解析 <c>--driver-action=install --driver=&lt;id&gt;</c>。
    /// 命中返回驱动 id；不是辅助模式则返回 <c>null</c>（按普通启动处理）。
    /// </summary>
    private static string? GetDriverActionTarget()
    {
        try
        {
            string[] arguments = Environment.GetCommandLineArgs();

            bool isInstallAction = arguments.Any(a =>
                a.Equals("--driver-action=install", StringComparison.OrdinalIgnoreCase));
            if (!isInstallAction) return null;

            const string prefix = "--driver=";
            foreach (string argument in arguments)
            {
                if (argument.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    string id = argument[prefix.Length..].Trim('"').Trim();
                    return string.IsNullOrWhiteSpace(id) ? null : id;
                }
            }
        }
        catch
        {
            // 参数解析失败就当成普通启动，不要因为一个畸形参数拒绝启动
        }
        return null;
    }

    /// <summary>
    /// 辅助模式主体：以管理员身份安装驱动，把结果写进结果文件后退出。
    /// 主实例在 <c>DriverInstallerService.LaunchElevatedHelperAsync</c> 里等待并回读该文件。
    ///
    /// <para>
    /// ⚠️ 这里用 <see cref="Environment.Exit"/> 而不是 <c>Application.Exit()</c>：
    /// 辅助模式没有窗口也没有消息循环，必须立刻确定性地结束进程。
    /// </para>
    /// </summary>
    private static void RunDriverInstallHelper(string driverId)
    {
        int exitCode = 1;

        try
        {
            AppLog.Info(LogFileName,
                $"辅助模式启动：安装驱动 {driverId}（已提权={DriverDetectionService.IsElevated}）");

            // 轻量模式：不拉起游戏库与进程监控 —— 这个进程只为装一个驱动而存在
            AppHost host = AppHost.Build(initializeRuntime: false);
            DriverManifestService manifest = host.GetRequiredService<DriverManifestService>();
            DriverInstallerService installer = host.GetRequiredService<DriverInstallerService>();

            DriverInfo? info = manifest.Find(driverId);
            if (info is null)
            {
                DriverInstallerService.WriteResultFile(new DriverInstallResult
                {
                    DriverId = driverId,
                    Success = false,
                    Message = $"驱动清单中不存在：{driverId}",
                });
            }
            else
            {
                DriverInstallResult result = installer.ExecuteInstallCoreAsync(info)
                    .GetAwaiter().GetResult();

                DriverInstallerService.WriteResultFile(result);
                exitCode = result.Success ? 0 : 1;

                AppLog.Info(LogFileName,
                    $"辅助模式完成：成功={result.Success} 退出码={result.ExitCode} 需重启={result.NeedsRestart}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error(LogFileName, $"辅助模式异常：{AppLog.Describe(ex)}");
            try
            {
                DriverInstallerService.WriteResultFile(new DriverInstallResult
                {
                    DriverId = driverId,
                    Success = false,
                    Message = AppLog.Describe(ex),
                });
            }
            catch
            {
                // 结果文件写不出去也没别的办法了
            }
        }

        Environment.Exit(exitCode);
    }
}
