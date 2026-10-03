using DsInApex.Spike.Localization;
using Microsoft.UI.Xaml;

namespace DsInApex.Spike;

/// <summary>
/// Spike 应用入口。
/// S1：unpackaged + self-contained 模式下能正常出窗口。
/// S2：语言在 LocalizationManager 中初始化，窗口元素随后自动取到正确文案。
/// S5：用命名 Mutex 做单实例，二次启动直接退出。
/// </summary>
public partial class App : Application
{
    /// <summary>必须存为静态字段，否则会被 GC 回收导致互斥量失效。</summary>
    private static Mutex? _singleInstanceMutex;

    private Window? _window;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // ── S5：单实例守卫 ──
        // 用 Local\ 前缀（非 Global\），避免在非管理员会话下因权限不足失败。
        _singleInstanceMutex = new Mutex(
            initiallyOwned: true,
            name: @"Local\DsInApex.Spike.SingleInstance",
            createdNew: out bool createdNew);

        if (!createdNew)
        {
            // 已有实例在运行 —— 直接退出，不创建第二个窗口。
            Exit();
            return;
        }

        // 必须在窗口创建之前完成语言初始化 —— 附加属性在 XAML 解析时即取文案。
        // 上游 tray_settings.json 的 Language 字段读取在 P1 接入，Spike 先用 "auto"。
        LocalizationManager.Initialize(Langs.Auto);

        _window = new MainWindow();
        _window.Activate();
    }
}
