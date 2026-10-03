using DsInApex.App.Services;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
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

    public App()
    {
        InitializeComponent();

        // 兜底：未捕获异常写日志，便于 P8 实测排障时定位
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
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
        _window = new MainWindow();
        _window.Activate();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        AppLog.Error(LogFileName, $"未处理异常：{AppLog.Describe(e.Exception)}");

        // 刻意不设 e.Handled = true：带病继续运行比重启更容易掩盖问题，
        // P1 阶段宁可让它崩掉并留下日志。
    }
}
