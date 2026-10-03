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

        SetupTray();

        // 关闭窗口不退出，隐藏到托盘（桥接需在后台持续工作）
        AppWindow.Closing += OnAppWindowClosing;

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
    /// 验收自检：把「依赖 UI 交互、无法靠人工肉眼留存证据」的项目变成可复查的日志。
    ///
    /// <para>
    /// P1 覆盖：导航壳可用（8 页面路由）、中英热切换生效。
    /// P2 追加：页面内容真的渲染出来了、设置文件读写往返一致。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 顺序有讲究：语言切换会经由 <c>ViewModel.SwitchLanguage</c> 写盘，
    /// 因此**设置往返测试必须排在最后**，否则后续写盘会覆盖掉已恢复的文件。
    /// </para>
    /// </summary>
    private async void RunSelfTestIfRequested()
    {
        if (!SelfTest.IsRequested) return;

        int passedSections = 0;
        int totalSections = 0;

        try
        {
            await Task.Delay(1500);   // 等窗口完全渲染

            SelfTest.LogHeader($"Ds in Apex 自检 · 起始语言={ViewModel.CurrentLanguage}");

            // ══════════ 1. 导航遍历 + 页面内容检查 ══════════
            SelfTest.LogHeader("导航遍历 + 页面内容检查");
            int routeOk = 0;
            int contentOk = 0;

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
                await Task.Delay(250);

                Type? actualType = NavFrame.CurrentSourcePageType;
                bool routePassed = actualType == target.PageType;
                if (routePassed) routeOk++;

                // 读页面内【实际渲染】的文本 —— 证明本地化真的落到 UI 上了，
                // 而不是"字典查得到、界面一片空白"
                string expected = LocalizationService.Shared.Get(target.LocKey);
                List<string> texts = SelfTest.CollectTexts(NavFrame.Content as DependencyObject);
                bool titleFound = texts.Contains(expected);
                bool contentPassed = routePassed && texts.Count >= 2 && titleFound;
                if (contentPassed) contentOk++;

                SelfTest.Log(
                    $"  {(routePassed && contentPassed ? "✓" : "✗")} {target.Tag,-12}" +
                    $" 路由={actualType?.Name ?? "(null)",-18}" +
                    $" 文本={texts.Count,-2}项" +
                    $" 含标题\"{expected}\"={(titleFound ? "是" : "否")}");
            }

            SelfTest.Log($"路由结果：{routeOk}/{ViewModel.NavigationItems.Count} 通过");
            SelfTest.Log($"内容结果：{contentOk}/{ViewModel.NavigationItems.Count} 通过");

            totalSections++;
            if (routeOk == ViewModel.NavigationItems.Count && contentOk == ViewModel.NavigationItems.Count)
            {
                passedSections++;
            }

            // ══════════ 2. 语言热切换 ══════════
            SelfTest.LogHeader("语言热切换");

            // 归位到仪表盘，保证前后读的是同一页
            NavigateTo("dashboard");
            await Task.Delay(300);

            string zhNav = MenuItemText("dashboard");
            string zhPage = PageTitleText();
            SelfTest.Log($"[zh-CN] 导航项文案 = \"{zhNav}\"  页面标题 = \"{zhPage}\"");

            LocalizationService.Shared.SetLanguage(Langs.En);
            await Task.Delay(400);
            string enNav = MenuItemText("dashboard");
            string enPage = PageTitleText();
            SelfTest.Log($"[en]    导航项文案 = \"{enNav}\"  页面标题 = \"{enPage}\"");
            bool navSwitched = zhNav != enNav && enNav.Length > 0;
            bool pageSwitched = enPage.Length > 0 && enPage == enNav;
            SelfTest.Log($"  导航项切换生效     = {(navSwitched ? "是" : "否")}");
            SelfTest.Log($"  页面内容同步切换   = {(pageSwitched ? "是" : "否")}");

            LocalizationService.Shared.SetLanguage(Langs.ZhCN);
            await Task.Delay(400);
            string backNav = MenuItemText("dashboard");
            string backPage = PageTitleText();
            SelfTest.Log($"[zh-CN] 切回后导航项 = \"{backNav}\"  页面标题 = \"{backPage}\"");
            bool backConsistent = backNav == zhNav && backPage == zhPage;
            SelfTest.Log($"  切回中文一致       = {(backConsistent ? "是" : "否")}");

            // 恢复界面选中态（自检改过语言，但未写设置文件）
            SyncLanguageSelection();

            // 顺带验证字典的缺键回退策略没被改坏
            SelfTest.Log($"  缺键回退抽查(不存在的键) = \"{LocalizationService.Shared.Get("Loc___NotExist__")}\"");

            totalSections++;
            if (navSwitched && pageSwitched && backConsistent) passedSections++;

            // ══════════ 3. 托盘（P2 关键结构：与 NavigationView 共存） ══════════
            SelfTest.LogHeader("系统托盘");
            bool trayCreated = TrayIcon.IsCreated;
            int trayMenuItems = (TrayIcon.ContextFlyout as MenuFlyout)?.Items.Count ?? 0;
            SelfTest.Log($"  托盘图标 IsCreated = {trayCreated}");
            SelfTest.Log($"  右键菜单项数       = {trayMenuItems}");
            SelfTest.Log($"  菜单文案(zh-CN)    = \"{LocalizationService.Shared.Get("Loc_TrayOpen")}\"" +
                         $" / \"{LocalizationService.Shared.Get("Loc_TrayExit")}\"");

            totalSections++;
            if (trayCreated && trayMenuItems > 0) passedSections++;

            // ══════════ 4. 通知链路（unpackaged 已知限制，仅记录不判定） ══════════
            SelfTest.LogHeader("Toast 通知（unpackaged · 已知限制）");
            NotificationService notify = AppHost.Current.GetRequiredService<NotificationService>();
            foreach (string line in notify.Diagnose())
            {
                SelfTest.Log("  " + line);
            }

            // ══════════ 5. 设置读写往返 ══════════
            //   必须放最后：这一步会临时改写 tray_settings.json 后原样恢复，
            //   若之后还有别的写盘动作，恢复就白做了。
            SelfTest.LogHeader("设置读写往返");
            (bool settingsPassed, IReadOnlyList<string> settingsLines) = SelfTest.RunSettingsRoundTrip();

            foreach (string line in settingsLines)
            {
                SelfTest.Log(line);
            }

            totalSections++;
            if (settingsPassed) passedSections++;

            // ══════════ 汇总 ══════════
            SelfTest.LogHeader($"自检结束：{passedSections}/{totalSections} 段通过");
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

    /// <summary>
    /// 取当前页面内首个非空 TextBlock 文本。
    /// 占位页的结构里它就是页面标题，用于验证"页面内容跟着语言一起变"。
    /// </summary>
    private string PageTitleText()
    {
        List<string> texts = SelfTest.CollectTexts(NavFrame.Content as DependencyObject);
        return texts.Count > 0 ? texts[0] : "(空)";
    }

    // ────────────────────────── 系统托盘 ──────────────────────────

    /// <summary>
    /// 创建托盘图标。unpackaged 下需显式 <c>ForceCreate()</c>，
    /// 否则图标可能不进入通知区域（Spike 已踩过）。
    /// </summary>
    private void SetupTray()
    {
        try
        {
            TrayIcon.ForceCreate();
            int menuItems = (TrayIcon.ContextFlyout as MenuFlyout)?.Items.Count ?? 0;
            AppLog.Info(LogFileName, $"托盘图标已创建：IsCreated={TrayIcon.IsCreated}，菜单项={menuItems}");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"托盘初始化失败：{AppLog.Describe(ex)}");
        }
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"从托盘恢复窗口失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 托盘菜单里做中英对切，并同步窗口内的单选按钮选中态。
    ///
    /// 注意这里**直接调语言服务**而非 ViewModel 的 SetLanguage 路径：
    /// 托盘切换不应写 tray_settings.json（与设置页切换的语义不同，
    /// 后者是"用户显式改设置"，前者是快捷操作）。此处保持与设置页一致的持久化行为。
    /// </summary>
    private void OnTrayLanguageClick(object sender, RoutedEventArgs e)
    {
        bool isEn = string.Equals(ViewModel.CurrentLanguage, Langs.En, StringComparison.OrdinalIgnoreCase);
        ViewModel.IsChineseSelected = !isEn;
        SyncLanguageSelection();

        AppLog.Info(LogFileName, $"托盘切换语言 → {ViewModel.CurrentLanguage}");
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        AppLog.Info(LogFileName, "从托盘菜单退出");
        Application.Current.Exit();
    }

    /// <summary>
    /// 拦截窗口关闭：隐藏到托盘而不是结束进程。
    /// 桥接需要在后台持续工作，点 X 不该把服务一起带走。
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        args.Cancel = true;
        sender.Hide();
        AppLog.Info(LogFileName, "窗口已隐藏到托盘（进程继续运行）");
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
