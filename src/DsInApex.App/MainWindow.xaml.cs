using DsInApex.App.Diagnostics;
using DsInApex.App.Models;
using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;

namespace DsInApex.App;

/// <summary>
/// 主窗口：标题栏 + NavigationView 导航壳 + 语言/主题切换。
/// </summary>
public sealed partial class MainWindow : Window
{
    private const string LogFileName = "dsinapex_app.log";

    /// <summary>
    /// 初始化护栏。RadioButton.Checked 会在 XAML 解析阶段抢先触发，
    /// 此时控件树尚未就绪，必须屏蔽 —— 上游 WPF 版同样有 isInitialized 护栏。
    /// </summary>
    private bool _ready;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = AppHost.Current.GetRequiredService<MainWindowViewModel>();

        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        // 语言选中态必须在本窗口 _ready 置位之前同步，否则会被误判为一次用户切换
        SyncLanguageSelection();

        ViewModel.ThemeChangeRequested += OnThemeChangeRequested;

        _ready = true;

        // 默认落在第一个导航页
        NavigationViewItem? firstItem = NavView.MenuItems.OfType<NavigationViewItem>().FirstOrDefault();
        if (firstItem is not null)
        {
            NavView.SelectedItem = firstItem;
            NavigateTo(firstItem.Tag as string);
        }

        ViewModel.ApplySavedTheme();

        AppLog.Info(LogFileName, "主窗口已就绪");

        RunSelfTestIfRequested();
    }

    // ────────────────────────── 自检（DIA_SELFTEST=1） ──────────────────────────

    /// <summary>
    /// P1 验收用自检：验证「导航壳可用」与「中英热切换生效」。
    /// 这两项都依赖 UI 交互，没有钩子就只能靠人工点、留不下可复查的证据。
    ///
    /// 自检**不修改** tray_settings.json —— 语言只临时切换后切回。
    /// </summary>
    private async void RunSelfTestIfRequested()
    {
        if (!SelfTest.IsRequested) return;

        try
        {
            await Task.Delay(1500);   // 等窗口完全渲染

            SelfTest.LogHeader($"Ds in Apex 自检 · 起始语言={ViewModel.CurrentLanguage}");

            // ── 1. 导航遍历：逐个选中，核对 Frame 真的切过去了 ──
            SelfTest.LogHeader("导航遍历");
            int ok = 0;
            foreach (NavigationItem target in ViewModel.NavigationItems)
            {
                NavigationViewItem? menuItem = NavView.MenuItems
                    .OfType<NavigationViewItem>()
                    .FirstOrDefault(m => (m.Tag as string) == target.Tag);

                if (menuItem is null)
                {
                    SelfTest.Log($"  ✗ {target.Tag,-12} 菜单项缺失");
                    continue;
                }

                NavView.SelectedItem = menuItem;
                await Task.Delay(200);

                Type? actual = NavFrame.CurrentSourcePageType;
                bool passed = actual == target.PageType;
                SelfTest.Log($"  {(passed ? "✓" : "✗")} {target.Tag,-12} → 期望 {target.PageType.Name,-20} 实际 {actual?.Name ?? "(null)"}");
                if (passed) ok++;
            }
            SelfTest.Log($"导航结果：{ok}/{ViewModel.NavigationItems.Count} 通过");

            // ── 2. 语言热切换：读菜单项【实际显示文案】，而非字典返回值 ──
            SelfTest.LogHeader("语言热切换");
            string zh = MenuItemText("dashboard");
            SelfTest.Log($"[zh-CN] 仪表盘导航项实际文案 = \"{zh}\"");

            LocalizationService.Shared.SetLanguage(Langs.En);
            await Task.Delay(400);
            string en = MenuItemText("dashboard");
            SelfTest.Log($"[en]    仪表盘导航项实际文案 = \"{en}\"");
            SelfTest.Log($"  切换到英文生效 = {(zh != en && en.Length > 0 ? "是" : "否")}");

            LocalizationService.Shared.SetLanguage(Langs.ZhCN);
            await Task.Delay(400);
            string back = MenuItemText("dashboard");
            SelfTest.Log($"[zh-CN] 切回后实际文案 = \"{back}\"");
            SelfTest.Log($"  切回中文一致 = {(back == zh ? "是" : "否")}");

            // 语言键值抽查（确认字典本身也切了）
            SelfTest.Log($"  字典抽查 Loc_Language: zh-CN=\"{LocalizationService.Shared.Get("Loc_Language")}\"");

            // 恢复界面选中态（自检改过语言但未写设置文件）
            SyncLanguageSelection();

            SelfTest.LogHeader("自检结束");
        }
        catch (Exception ex)
        {
            SelfTest.Log($"自检异常：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>按 Tag 读取导航菜单项的【实际显示文案】（Content 已被 Localize 覆写为真实文案）。</summary>
    private string MenuItemText(string tag)
    {
        NavigationViewItem? item = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(m => (m.Tag as string) == tag);

        return item?.Content?.ToString() ?? "(未找到)";
    }

    // ────────────────────────── 导航 ──────────────────────────

    private void OnNavigationSelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;
        NavigateTo(item.Tag as string);
    }

    /// <summary>
    /// 按 Tag 查路由表并导航。
    ///
    /// 注意参数类型：<c>Frame.Navigate</c> 的第一个参数是 <c>Type</c>，
    /// 而 <c>NavigationViewItem.Tag</c> 是 <c>object</c>（XAML 里给的是字符串），
    /// 所以必须经 ViewModel 的路由表做映射，不能把 Tag 直接传给 Navigate。
    /// </summary>
    private void NavigateTo(string? tag)
    {
        if (string.IsNullOrEmpty(tag)) return;

        NavigationItem? target = ViewModel.NavigationItems.FirstOrDefault(n => n.Tag == tag);
        if (target is null)
        {
            AppLog.Warn(LogFileName, $"未知导航 Tag：{tag}");
            return;
        }

        ViewModel.SelectedItem = target;

        if (NavFrame.CurrentSourcePageType == target.PageType)
        {
            return;   // 已在目标页，避免重复入栈
        }

        NavFrame.Navigate(target.PageType, null, new EntranceNavigationTransitionInfo());
    }

    private void OnTitleBarBackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void OnTitleBarPaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    // ────────────────────────── 语言 ──────────────────────────

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        ViewModel.IsChineseSelected = RadZh.IsChecked == true;
    }

    private void SyncLanguageSelection()
    {
        if (ViewModel.IsChineseSelected) RadZh.IsChecked = true;
        else RadEn.IsChecked = true;
    }

    // ────────────────────────── 主题 ──────────────────────────

    private void OnThemeChangeRequested(object? sender, string theme)
    {
        if (Content is not FrameworkElement root) return;

        root.RequestedTheme = theme switch
        {
            "light" => ElementTheme.Light,
            "dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,   // 跟随系统
        };

        AppLog.Info(LogFileName, $"主题已应用：{theme}");
    }
}
