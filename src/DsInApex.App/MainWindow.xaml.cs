using DsInApex.App.Diagnostics;
using DsInApex.App.Models;
using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;
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

    private readonly EngineSessionManager _session;
    private readonly TraySettings _settings;
    private readonly NotificationService _notify;
    private readonly ProcessMonitorService _monitor;
    private readonly UpdateCheckerService _updateChecker;

    /// <summary>
    /// 最近一次采样的控制器恢复状态（P6）。
    ///
    /// <para>
    /// 刻意缓存而不是每次开菜单都重查：查一次要读注册表 + 打开进程句柄，
    /// 而托盘菜单弹出是高频动作，菜单弹出卡顿比状态晚几秒难看得多。
    /// 缓存只在三个时机刷新：窗口就绪、会话结束、用户点「恢复」之后。
    /// </para>
    /// </summary>
    private RecoveryStatus _recovery = null!;

    /// <summary>最近一次采样的开机自启状态（注册表是真源，这里只是显示快照）。</summary>
    private AutoStartStatus _autoStart = null!;

    public MainWindowViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = AppHost.Current.GetRequiredService<MainWindowViewModel>();
        _session = AppHost.Current.GetRequiredService<EngineSessionManager>();
        _settings = AppHost.Current.GetRequiredService<TraySettings>();
        _notify = AppHost.Current.GetRequiredService<NotificationService>();
        _monitor = AppHost.Current.GetRequiredService<ProcessMonitorService>();
        _updateChecker = AppHost.Current.GetRequiredService<UpdateCheckerService>();

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

        // P6：通知链路 + 后台行为接线
        SetupNotifications();
        SyncTrayPreferences();
        CheckAutoStartOnStartup();
        CheckRecoveryOnStartup();

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

        AppLog.Info(LogFileName,
            $"主窗口已就绪（版本 {_updateChecker.CurrentVersion}，" +
            $"开机自启={_autoStart.State}，恢复状态={_recovery.State}）");

        // P6：启动时的静默更新检查（不阻塞界面）
        if (_settings.CheckUpdatesOnStartup)
        {
            _ = CheckForUpdatesAsync(silent: true);
        }

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

            // ══════════ 5. 引擎定位与会话管理器（P2） ══════════
            SelfTest.LogHeader("P2 · 引擎定位与会话管理器");

            string enginePath = EngineLocator.ResolveEngineWithTrace(out IReadOnlyList<string> trace);
            foreach (string line in trace)
            {
                SelfTest.Log("  " + line);
            }
            bool engineFound = !string.IsNullOrWhiteSpace(enginePath);
            SelfTest.Log($"  引擎路径          = {(engineFound ? enginePath : "(未找到)")}");
            SelfTest.Log($"  外部会话占用      = {EngineSessionManager.IsExternalSessionActive()}");
            SelfTest.Log($"  当前会话活动      = {_session.IsSessionActive}");
            SelfTest.Log($"  当前会话健康      = {_session.IsSessionHealthy}");
            SelfTest.Log($"  当前游戏（空态）  = \"{_session.ActiveGameTitle}\"");
            SelfTest.Log($"  当前配置档（空态）= \"{_session.ActiveProfile}\"");

            // 仪表盘 / 设置页的真实绑定抽查（不写盘）
            NavigateTo("dashboard");
            await Task.Delay(350);
            DashboardViewModel dash = AppHost.Current.GetRequiredService<DashboardViewModel>();
            SelfTest.Log($"  仪表盘 引擎已定位 = {dash.EngineFound}");
            SelfTest.Log($"  仪表盘 会话阶段   = \"{dash.SessionStateText}\"");
            SelfTest.Log($"  仪表盘 可启动/停止= {dash.CanStartBridge} / {dash.CanStopBridge}");
            SelfTest.Log($"  指示灯 扳机={dash.TriggersEnabled} 触觉={dash.HapticsEnabled}");

            NavigateTo("settings");
            await Task.Delay(350);
            SettingsViewModel settingsVm = AppHost.Current.GetRequiredService<SettingsViewModel>();
            SelfTest.Log($"  设置页 主题索引   = {settingsVm.ThemeIndex}（0=系统 1=浅 2=深）");
            SelfTest.Log($"  设置页 语言索引   = {settingsVm.LanguageIndex}（0=zh-CN 1=en）");
            SelfTest.Log($"  设置页 阈值/超时  = {settingsVm.HapticThreshold} / {settingsVm.InitTimeoutSeconds}");
            SelfTest.Log($"  设置文件          = {settingsVm.SettingsFilePath}");

            totalSections++;
            if (engineFound && !_session.IsSessionActive) passedSections++;

            // ══════════ 6. 真实会话冒烟（DIA_SESSION_SMOKE=1，会接管手柄） ══════════
            if (SelfTest.SessionSmokeRequested)
            {
                SelfTest.LogHeader("P2 · 真实会话冒烟（会接管手柄）");

                long logOffset = SelfTest.CurrentBridgeLogLength();
                string smokeTitle = LocalizationService.Shared.Get("Loc_ManualBridgeGameTitle");
                string? startError = null;

                bool started = await Task.Run(() =>
                {
                    bool ok2 = _session.StartSession(smokeTitle, "standard", _settings, 0, out string? err);
                    startError = err;
                    return ok2;
                });

                SelfTest.Log($"  启动会话          = {(started ? "成功" : "失败")}");
                if (!started)
                {
                    SelfTest.Log($"  失败原因          = {startError}");
                }
                SelfTest.Log($"  IsSessionActive   = {_session.IsSessionActive}");
                SelfTest.Log($"  IsSessionHealthy  = {_session.IsSessionHealthy}");
                SelfTest.Log($"  引擎 PID          = {_session.ActiveProcessId}");
                SelfTest.Log($"  当前游戏          = \"{_session.ActiveGameTitle}\"");
                SelfTest.Log($"  配置档            = \"{_session.ActiveProfile}\"");

                // 托盘状态同步（会话事件驱动，走 DispatcherQueue，故需等一拍）
                await Task.Delay(400);
                SelfTest.Log($"  托盘提示文字      = \"{TrayIcon.ToolTipText}\"");
                SelfTest.Log($"  托盘[启动]可见    = {TrayStartItem.Visibility}（激活时应为 Collapsed）");
                SelfTest.Log($"  托盘[停止]可见    = {TrayStopItem.Visibility}（激活时应为 Visible）");

                if (started)
                {
                    await Task.Delay(5000);   // 留出时间产生扳机 / 触觉活动
                    await Task.Run(() => _session.StopSession("self-test-smoke"));
                    await Task.Delay(2000);   // 等引擎写完 neutralize / restore 日志
                }

                await Task.Delay(400);
                SelfTest.Log($"  停止后托盘提示    = \"{TrayIcon.ToolTipText}\"");
                SelfTest.Log($"  停止后[启动]可见  = {TrayStartItem.Visibility}（回到待机应为 Visible）");

                (bool smokePassed, IReadOnlyList<string> smokeLines) = SelfTest.VerifyBridgeLogSince(logOffset);
                foreach (string line in smokeLines)
                {
                    SelfTest.Log(line);
                }

                SelfTest.Log($"  停止后会话活动    = {_session.IsSessionActive}");

                totalSections++;
                if (started && smokePassed) passedSections++;
            }

            // ══════════ 7. P3 · 游戏库与学习记录 ══════════
            //   只做只读检查（搜索 / 筛选 / 计数），刻意不碰 tray_settings.json，
            //   以免破坏紧随其后的设置往返。
            SelfTest.LogHeader("P3 · 游戏库与学习记录");

            CloudGameListService gameListSvc = AppHost.Current.GetRequiredService<CloudGameListService>();
            GameLibraryViewModel libVm = AppHost.Current.GetRequiredService<GameLibraryViewModel>();

            SelfTest.Log($"  游戏库已载入     = {gameListSvc.TotalGamesLoaded} 款（本地缓存 / 嵌入资源）");
            SelfTest.Log($"  库中总条数       = {libVm.TotalGameCount}");
            SelfTest.Log($"  当前显示条数     = {libVm.Games.Count}");

            // 搜索语义
            libVm.SearchText = "ace";
            await Task.Delay(150);
            int aceHits = libVm.Games.Count;
            SelfTest.Log($"  搜索 \"ace\" 命中  = {aceHits} 款");

            libVm.SearchText = string.Empty;
            await Task.Delay(150);
            SelfTest.Log($"  清空搜索后       = {libVm.Games.Count} 款");

            // 筛选语义（0=全部 1=自适应 2=触觉 3=已排除）
            libVm.FilterIndex = 1;
            await Task.Delay(150);
            int adaptiveOnly = libVm.Games.Count;
            SelfTest.Log($"  筛选·仅自适应    = {adaptiveOnly} 款");

            libVm.FilterIndex = 2;
            await Task.Delay(150);
            int hapticOnly = libVm.Games.Count;
            SelfTest.Log($"  筛选·仅触觉      = {hapticOnly} 款");

            libVm.FilterIndex = 0;
            await Task.Delay(150);
            SelfTest.Log($"  筛选复位后       = {libVm.Games.Count} 款");

            // 学习记录（与官方版共用的 learned_executables.json）
            LearnedViewModel learnedVm = AppHost.Current.GetRequiredService<LearnedViewModel>();
            SelfTest.Log($"  学习记录条数     = {learnedVm.Items.Count}");

            bool libraryOk =
                gameListSvc.TotalGamesLoaded >= 200 &&
                libVm.TotalGameCount >= 200 &&
                aceHits > 0 &&
                adaptiveOnly > 0 && adaptiveOnly <= libVm.TotalGameCount &&
                hapticOnly <= libVm.TotalGameCount;
            SelfTest.Log($"  游戏库判定       = {(libraryOk ? "通过（含搜索与筛选语义）" : "未通过")}");

            totalSections++;
            if (libraryOk) passedSections++;

            // ══════════ 8. P4 · 驱动检测（只读） ══════════
            //   只检测、不安装 —— 安装会改系统且需要 UAC 确认，绝不能混进自检。
            SelfTest.LogHeader("P4 · 驱动检测（只读）");

            DriverManifestService driverManifest = AppHost.Current.GetRequiredService<DriverManifestService>();
            DriverDetectionService driverDetection = AppHost.Current.GetRequiredService<DriverDetectionService>();

            IReadOnlyList<DriverInfo> driverInfos = driverManifest.Load();
            SelfTest.Log($"  清单驱动力数     = {driverInfos.Count}");
            SelfTest.Log($"  当前已是管理员   = {DriverDetectionService.IsElevated}");
            SelfTest.Log($"  安装器目录       = {driverDetection.PrerequisitesDirectory}");

            IReadOnlyList<DriverStatus> driverStatuses = driverDetection.DetectAll();
            foreach (DriverStatus status in driverStatuses)
            {
                SelfTest.Log(
                    $"  [{status.Info.Id}] 状态={status.State}" +
                    $" 已装={status.InstalledVersion ?? "(无)"}" +
                    $" 期望={status.Info.Version}" +
                    $" 服务={string.Join("/", status.Services.Select(kv => $"{kv.Key}:{kv.Value}"))}" +
                    $" 安装器={(status.InstallerPresent ? (status.InstallerVerified ? "已校验" : "校验失败") : "缺失")}");
            }

            int requiredDriverCount = driverStatuses.Count(x => x.Info.IsRequired);
            int verifiedInstallers = driverStatuses.Count(x => x.Info.IsRequired && x.InstallerVerified);

            bool driversOk = driverInfos.Count >= 3
                             && driverStatuses.Count == driverInfos.Count
                             && requiredDriverCount >= 2
                             && verifiedInstallers == requiredDriverCount;

            SelfTest.Log($"  必需驱动 {requiredDriverCount} 项，安装器校验通过 {verifiedInstallers} 项");
            SelfTest.Log($"  驱动检测判定     = {(driversOk ? "通过" : "未通过")}");

            totalSections++;
            if (driversOk) passedSections++;

            // ══════════ 9. P5 · 硬件测试与诊断（只读） ══════════
            //   ⚠️ 这一段只跑只读命令。绝不碰 test-rt / test-rumble / apex4-port-test
            //      （会真实驱动扳机与马达），也绝不碰 clear / stop-active-sessions
            //      （会改变全局状态）—— 自检不允许产生用户可感知的物理动作。
            SelfTest.LogHeader("P5 · 硬件测试与诊断（只读）");

            HardwareTestService hardwareSvc = AppHost.Current.GetRequiredService<HardwareTestService>();
            DiagnosticsCollectorService diagnosticsSvc = AppHost.Current.GetRequiredService<DiagnosticsCollectorService>();

            IReadOnlyList<EngineCommandSpec> catalog = EngineCommandCatalog.All;
            SelfTest.Log($"  命令目录条目     = {catalog.Count} 条");
            foreach (IGrouping<EngineCommandCategory, EngineCommandSpec> group in
                     catalog.GroupBy(spec => spec.Category))
            {
                SelfTest.Log($"    {group.Key,-14} = {group.Count()} 条");
            }

            int readOnlyCount = catalog.Count(spec => spec.Risk == EngineCommandRisk.ReadOnly);
            int stateChanging = catalog.Count(spec => spec.Risk != EngineCommandRisk.ReadOnly);
            SelfTest.Log($"  只读 / 会改状态  = {readOnlyCount} / {stateChanging}");

            // 面向用户的命令必须齐全。
            // 引擎另有 bridge-triggers（由会话管理器专职驱动）、hidhide-watchdog（内部看门狗）、
            // help 等，它们不属于本页目录 —— 因此这里断言的是 14 条，而不是引擎的全部命令数。
            string[] requiredCommands =
            [
                "list", "identify", "diagnose",
                "input-status", "xinput-status",
                "virtual-ds",
                "test-rt", "test-rumble", "test-profile-switch", "apex4-port-test",
                "clear", "stop-active-sessions", "restore-controller-visibility",
                "dry-run",
            ];
            bool catalogComplete =
                catalog.Count == requiredCommands.Length
                && requiredCommands.All(id => EngineCommandCatalog.Get(id) is not null);

            SelfTest.Log($"  命令目录完整性   = {(catalogComplete ? "通过" : "有缺失")}" +
                         $"（期望 {requiredCommands.Length} 条，实际 {catalog.Count} 条）");
            SelfTest.Log($"  引擎可用         = {hardwareSvc.EngineAvailable}");
            SelfTest.Log($"  会话互斥已生效   = {hardwareSvc.IsSessionActive}（会话活动时写硬件命令会被直接拒绝）");

            // list：只读枚举
            EngineCommandOutcome listProbe = await hardwareSvc.RunAsync("list");
            int deviceCount = listProbe.Parsed is IReadOnlyList<FlydigiDeviceInfo> probedDevices
                ? probedDevices.Count : 0;
            SelfTest.Log($"  list          退出码={listProbe.ExitCode}" +
                         $" 解析={(listProbe.IsParsed ? "成功" : "降级原始文本")}" +
                         $" 设备数={deviceCount}（自检不要求手柄在位）" +
                         $" 耗时={listProbe.Duration.TotalMilliseconds:F0} ms");
            SelfTest.Log($"  list 结论       = {listProbe.Summary}");

            // diagnose --json：只读 HID 枚举，验证 JSON 解析层
            EngineCommandOutcome diagnoseProbe =
                await hardwareSvc.RunAsync("diagnose", new EngineCommandOptions(Json: true));
            int hidInterfaceCount = diagnoseProbe.Parsed is HidDiagnosticReport hidReport ? hidReport.Count : 0;
            int apex4InterfaceCount = diagnoseProbe.Parsed is HidDiagnosticReport apex4Report
                ? apex4Report.Apex4Interfaces.Count : 0;
            SelfTest.Log($"  diagnose      退出码={diagnoseProbe.ExitCode}" +
                         $" 解析={(diagnoseProbe.IsParsed ? "成功" : "降级原始文本")}" +
                         $" 接口={hidInterfaceCount} APEX4接口={apex4InterfaceCount}");

            // xinput-status：只读系统查询
            EngineCommandOutcome xinputProbe = await hardwareSvc.RunAsync("xinput-status");
            int xinputSlotCount = xinputProbe.Parsed is IReadOnlyList<int> slots ? slots.Count : 0;
            SelfTest.Log($"  xinput-status 退出码={xinputProbe.ExitCode}" +
                         $" 解析={(xinputProbe.IsParsed ? "成功" : "降级原始文本")}" +
                         $" 槽位={xinputSlotCount}");

            // PnP 拓扑：诊断链路的核心（WMI 查设备 + 注册表读容器 ID）
            string probeRoot = Path.GetTempPath();
            PnpDiagnostics pnpProbe = new PnpTopologyCollector().Collect(probeRoot);
            SelfTest.Log($"  PnP 拓扑可用     = {pnpProbe.Available}" +
                         $"（种子 {pnpProbe.SeedCount} / 相关设备 {pnpProbe.RelatedCount}）");
            SelfTest.Log($"  诊断收集器       = 已注册（{diagnosticsSvc.GetType().Name}）");

            // 清理探测过程中落盘的临时文件（自检不应留下垃圾）
            foreach (string probeFileName in new[] { "pnp-devices.json", "pnp-devices.txt" })
            {
                try
                {
                    string probePath = Path.Combine(probeRoot, probeFileName);
                    if (File.Exists(probePath)) File.Delete(probePath);
                }
                catch
                {
                    // 清理失败不影响判定
                }
            }

            bool hardwareOk =
                catalogComplete
                && readOnlyCount > 0
                && stateChanging > 0
                && hardwareSvc.EngineAvailable
                && listProbe.ExitCode is 0 or 2        // 2 = 没插手柄，属正常结果
                && diagnoseProbe.IsParsed
                && xinputProbe.IsParsed
                && pnpProbe.Available;

            SelfTest.Log($"  硬件测试/诊断判定 = {(hardwareOk ? "通过" : "未通过")}");

            totalSections++;
            if (hardwareOk) passedSections++;

            // ══════════ 9b. P5 · 诊断链路冒烟（DIA_DIAGNOSTICS_SMOKE=1） ══════════
            //   主体自检停留在只读子集；这一段才把「采样 → 解析 → 收集 → 打包」
            //   整条链路真的走完。所用命令仍然全部只读，产物只落在临时目录。
            if (SelfTest.DiagnosticsSmokeRequested)
            {
                SelfTest.LogHeader("P5 · 诊断链路冒烟（只读）");

                int smokePassed = 0;
                int smokeTotal = 0;

                // ── 1) input-status：JSON 解析 + 真实采样 ──
                EngineCommandOutcome inputSmoke = await hardwareSvc.RunAsync(
                    "input-status", new EngineCommandOptions(Json: true, Seconds: 1));

                bool inputSmokeOk = false;
                if (inputSmoke.Parsed is InputStatusReport inputReport)
                {
                    inputSmokeOk = inputReport.ReceivedState && inputReport.Reports > 0;
                    SelfTest.Log($"  input-status 退出码={inputSmoke.ExitCode}" +
                                 $" 报文={inputReport.Reports} 状态变化={inputReport.StateChanges}" +
                                 $" 超时={inputReport.Timeouts} 后端={inputReport.Backend}" +
                                 $" 按键={inputReport.Buttons} 摇杆={inputReport.LeftStickX},{inputReport.LeftStickY}");
                }
                else
                {
                    SelfTest.Log($"  input-status 退出码={inputSmoke.ExitCode} 未解析：{inputSmoke.ParseError}");
                    SelfTest.Log($"    原始输出：{inputSmoke.RawText.Trim()}");
                }
                SelfTest.Log($"  输入采样判定  = {(inputSmokeOk ? "通过" : "未通过")}");
                smokeTotal++;
                if (inputSmokeOk) smokePassed++;

                // ── 2) virtual-ds：JSON 解析 + 虚拟设备创建 ──
                EngineCommandOutcome vdsSmoke = await hardwareSvc.RunAsync(
                    "virtual-ds", new EngineCommandOptions(Json: true, Seconds: 1));

                bool vdsSmokeOk = false;
                if (vdsSmoke.Parsed is VirtualDsReport vdsReport)
                {
                    vdsSmokeOk = vdsReport.Connected;
                    SelfTest.Log($"  virtual-ds   退出码={vdsSmoke.ExitCode} 已连接={vdsReport.Connected}" +
                                 $" 后端={vdsReport.Backend} 固件={vdsReport.DualSenseFirmware}" +
                                 $" 输出报告={vdsReport.OutputReports}");
                }
                else
                {
                    SelfTest.Log($"  virtual-ds   退出码={vdsSmoke.ExitCode} 未解析：{vdsSmoke.ParseError}");
                }
                SelfTest.Log($"  虚拟设备判定  = {(vdsSmokeOk ? "通过" : "未通过")}");
                smokeTotal++;
                if (vdsSmokeOk) smokePassed++;

                // ── 3) 完整诊断收集（7 步 + ZIP） ──
                string smokeOutputDirectory = Path.Combine(Path.GetTempPath(), "DsInApex-Smoke");
                DiagnosticCollectionResult collectResult = await diagnosticsSvc.CollectAsync(
                    new DiagnosticCollectionOptions(
                        OutputDirectory: smokeOutputDirectory,
                        InputSampleSeconds: 3,
                        SkipInputSample: true,        // 冒烟不做交互式采样，缩短耗时
                        KeepWorkingDirectory: false));

                foreach (string line in collectResult.LogLines)
                {
                    SelfTest.Log("    " + line);
                }

                bool collectOk = collectResult.Success
                                 && !string.IsNullOrWhiteSpace(collectResult.ArchivePath)
                                 && File.Exists(collectResult.ArchivePath);

                if (collectOk && collectResult.Summary is { } summary)
                {
                    SelfTest.Log($"  诊断包        = {collectResult.ArchivePath}");
                    SelfTest.Log($"  步骤          = {summary.Steps.Count} 步，" +
                                 $"成功 {summary.Steps.Count(step => step.Success)} 步");
                    SelfTest.Log($"  警告          = {summary.Warnings.Count} 条");
                    SelfTest.Log($"  机型判定      = {summary.ModelAssessment?.Status ?? "(无)"}");
                    SelfTest.Log($"  PnP           = 可用={summary.PnpAvailable}" +
                                 $" 种子={summary.PnpSeedCount} 相关={summary.PnpRelatedCount}");
                    SelfTest.Log($"  XInput        = 可用={summary.XInputAvailable}" +
                                 $" 设备={summary.XInputDeviceCount}");
                }

                SelfTest.Log($"  诊断收集判定  = {(collectOk ? "通过" : "未通过")}");
                smokeTotal++;
                if (collectOk) smokePassed++;

                SelfTest.Log($"  冒烟结果      = {smokePassed}/{smokeTotal} 项通过");

                totalSections++;
                if (smokePassed == smokeTotal) passedSections++;
            }

            // ══════════ 10. P6 · 托盘后台 / 通知 / 自启 / 便携部署 / 恢复状态 ══════════
            //   ⚠️ 这一段全程只读：不写注册表、不联网、不改设置文件。
            //      「登记自启」与「恢复手柄」这两条写入路径只由用户显式点击驱动，
            //      自检绝不代劳 —— 否则一次自检就会擅自改系统状态。
            SelfTest.LogHeader("P6 · 托盘后台与便携部署（只读）");

            // ── 1) 托盘菜单结构 ──
            MenuFlyout? trayMenu = TrayIcon.ContextFlyout as MenuFlyout;
            IList<MenuFlyoutItemBase> trayItems = trayMenu?.Items ?? new List<MenuFlyoutItemBase>();
            SelfTest.Log($"  托盘 IsCreated    = {TrayIcon.IsCreated}");
            SelfTest.Log($"  托盘菜单项数      = {trayItems.Count}");
            foreach (MenuFlyoutItemBase item in trayItems)
            {
                string label = item switch
                {
                    MenuFlyoutSeparator => "──── 分隔线",
                    MenuFlyoutItem menuItem =>
                        $"{(menuItem.Visibility == Visibility.Visible ? "" : "[隐藏] ")}" +
                        $"{(menuItem.IsEnabled ? "" : "[只读] ")}{menuItem.Text}",
                    _ => item.GetType().Name,
                };
                SelfTest.Log($"    {label}");
            }

            bool trayStructureOk =
                TrayIcon.IsCreated
                && trayItems.Count >= 14
                && TrayStatusItem.Text.Length > 0
                && !string.IsNullOrWhiteSpace(TrayOpenItem.Text)
                && TrayLangZhItem.IsChecked != TrayLangEnItem.IsChecked;

            SelfTest.Log($"  托盘结构判定      = {(trayStructureOk ? "通过" : "未通过")}" +
                         $"（状态行=\"{TrayStatusItem.Text}\"）");

            // ── 2) 通知通道（Toast 预期不可用，气泡才是实际通道） ──
            SelfTest.Log($"  托盘气泡通道      = {(_notify.IsTrayAvailable ? "已接入且可用" : "不可用")}");
            SelfTest.Log($"  Toast 通道        = {(_notify.IsToastAvailable ? "可用" : "不可用（self-contained 已知限制，预期）")}");
            SelfTest.Log($"  已发送气泡数      = {_notify.TrayNotificationsSent}");

            // ── 3) 开机自启（只读注册表） ──
            AutoStartStatus autoStart = AutoStartService.Query();
            SelfTest.Log($"  自启状态          = {autoStart.State}");
            SelfTest.Log($"  登记命令          = {autoStart.RegisteredCommand ?? "(未登记)"}");
            SelfTest.Log($"  本机命令          = {autoStart.ExpectedCommand}");
            SelfTest.Log($"  HKCU 值名         = {autoStart.ValueName} @ {autoStart.KeyPath}");

            // ── 4) 便携部署布局 ──
            PortableLayoutReport portable = PortableLayoutService.Collect();
            foreach (string line in portable.Notes)
            {
                SelfTest.Log("  " + line);
            }
            SelfTest.Log($"  部署可用          = {portable.IsDeploymentUsable}" +
                         $"（引擎={portable.EnginePresent} 许可证={portable.LicensePresent}）");
            SelfTest.Log($"  程序目录可写      = {portable.AppDirectoryWritable}");

            // ── 5) 控制器恢复状态（引擎看门狗机制的只读呈现） ──
            RecoveryStatus recovery = RecoveryStateService.Collect();
            foreach (string line in RecoveryStateService.Describe(recovery))
            {
                SelfTest.Log("  " + line);
            }

            // ── 6) 更新检查器（只报告配置，不发请求 —— 自检不该依赖网络） ──
            SelfTest.Log($"  本机版本          = {_updateChecker.CurrentVersion}");
            SelfTest.Log($"  更新来源          = {UpdateCheckerService.RepoOwner}/{UpdateCheckerService.RepoName}" +
                         $"（api 优先 jsDelivr 清单，无发布时安静降级）");
            SelfTest.Log($"  发布页            = {UpdateCheckerService.ReleasesPageUrl}");
            SelfTest.Log($"  启动检查开关      = {_settings.CheckUpdatesOnStartup}");

            // ── 7) 托盘文案随语言热切换（读控件真实值，而不是查字典） ──
            LocalizationService.Shared.SetLanguage(Langs.En);
            await Task.Delay(250);
            UpdateTrayState();
            string openItemEn = TrayOpenItem.Text;
            string statusItemEn = TrayStatusItem.Text;
            SelfTest.Log($"[en]    托盘「打开界面」= \"{openItemEn}\"  状态行 = \"{statusItemEn}\"");

            LocalizationService.Shared.SetLanguage(Langs.ZhCN);
            await Task.Delay(250);
            UpdateTrayState();
            string openItemZh = TrayOpenItem.Text;
            SelfTest.Log($"[zh-CN] 托盘「打开界面」= \"{openItemZh}\"");

            bool trayLocalized = !string.IsNullOrWhiteSpace(openItemEn)
                                 && !string.IsNullOrWhiteSpace(openItemZh)
                                 && !string.Equals(openItemEn, openItemZh, StringComparison.Ordinal);
            SelfTest.Log($"  托盘文案热切换    = {(trayLocalized ? "通过" : "未通过")}");

            SyncLanguageSelection();

            bool p6Ok =
                trayStructureOk
                && _notify.IsTrayAvailable
                && autoStart.State != AutoStartState.Unreadable
                && portable.EnginePresent
                && !string.IsNullOrWhiteSpace(_updateChecker.CurrentVersion)
                && trayLocalized;

            SelfTest.Log($"  P6 判定           = {(p6Ok ? "通过" : "未通过")}");

            totalSections++;
            if (p6Ok) passedSections++;

            // ══════════ 11. 设置读写往返 ══════════
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
    ///
    /// <para>
    /// P2 追加：把会话状态接入托盘 —— 图标提示文字与「启动/停止桥接」菜单项
    /// 随会话状态变化，菜单文案随语言热切换。
    /// </para>
    /// </summary>
    private void SetupTray()
    {
        try
        {
            TrayIcon.ForceCreate();

            // 双击图标打开界面（与上游 notifyIcon.DoubleClick 行为一致）。
            // 用 Command 属性而不是 TrayMouseDoubleClick 事件：后者的委托签名
            // 在 H.NotifyIcon 各版本间变动过，而 DoubleClickCommand（ICommand）是稳定契约。
            TrayIcon.DoubleClickCommand = new CommunityToolkit.Mvvm.Input.RelayCommand(ShowWindow);

            // 兜底清理：进程被任务管理器强杀 / 系统关机时不会走窗口关闭路径，
            // 靠它保证托盘图标被销毁、活动会话被停止（否则手柄会卡在虚拟态）。
            AppDomain.CurrentDomain.ProcessExit += (_, _) => ShutdownCleanup("process-exit");

            // 会话事件可能来自后台线程（启动动作跑在 Task.Run 里），必须回 UI 线程改控件
            _session.SessionStarted += (_, _) => OnUi(UpdateTrayState);
            _session.SessionStopped += _ => OnUi(() =>
            {
                UpdateTrayState();
                // 会话结束正是「恢复标记该被清掉」的时刻 → 顺便重新采样一次
                RefreshRecoveryState(notify: false);
            });
            _session.SessionError += _ => OnUi(UpdateTrayState);

            LocalizationService.Shared.LanguageChanged += (_, _) => OnUi(UpdateTrayState);

            TrayAutoDetectItem.IsChecked = _settings.AutoDetectGames;

            UpdateTrayState();

            int menuItems = (TrayIcon.ContextFlyout as MenuFlyout)?.Items.Count ?? 0;
            AppLog.Info(LogFileName, $"托盘图标已创建：IsCreated={TrayIcon.IsCreated}，菜单项={menuItems}");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"托盘初始化失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// P6 · 通知接线。
    ///
    /// <para>
    /// 上游 WPF 版把这些订阅全塞在 <c>App.xaml.cs</c> 里；DIA 放进主窗口，
    /// 因为托盘图标（气泡的实际载体）是窗口的成员。
    /// 但订阅的<b>事件源</b>与上游一致：会话管理器 + 进程监控 + 更新检查器。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 三类事件<b>都来自后台线程</b>（会话跑在 Task.Run，进程监控跑在 WMI/定时器），
    /// 而 <c>Shell_NotifyIcon</c> 的消息窗口属于创建它的线程 → 必须 OnUi 回主线程，
    /// 否则气泡静默不弹（甚至偶发抛异常）。
    /// </para>
    /// </summary>
    private void SetupNotifications()
    {
        try
        {
            _notify.AttachTrayIcon(TrayIcon);

            // ── 1. 游戏识别（上游 App.xaml.cs:109 的同款行为） ──
            _monitor.GameDetected += (game, _) => OnUi(() =>
            {
                string title = string.IsNullOrWhiteSpace(game?.Title)
                    ? LocalizationService.Shared.Get("Loc_NotificationGame")
                    : game.Title;

                string profile = string.IsNullOrWhiteSpace(game?.Profile)
                    ? LocalizationService.Shared.Get("Loc_NotificationProfileStandard")
                    : game.Profile;

                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Info,
                    LocalizationService.Shared.Get("Loc_NotificationActivated"),
                    LocalizationService.Shared.Format("Loc_NotificationGameProfile", title, profile));
            });

            // ── 2. 会话生命周期 ──
            _session.SessionStarted += (game, profile) => OnUi(() =>
            {
                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Info,
                    LocalizationService.Shared.Get("Loc_NotifyBridgeStartedTitle"),
                    LocalizationService.Shared.Format("Loc_NotifyBridgeStartedBody", game, profile));
            });

            _session.SessionStopped += reason => OnUi(() =>
            {
                // 用户主动停止是预期动作，只记日志不打扰；自检冒烟同理
                string normalized = (reason ?? string.Empty).Trim();
                bool selfInflicted = normalized.Contains("self-test", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.Contains("tray-menu", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.Contains("Tray exiting", StringComparison.OrdinalIgnoreCase) ||
                                     normalized.Contains("Tray app closing", StringComparison.OrdinalIgnoreCase);

                if (selfInflicted)
                {
                    return;
                }

                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Info,
                    LocalizationService.Shared.Get("Loc_NotifyBridgeStoppedTitle"),
                    LocalizationService.Shared.Format("Loc_NotifyBridgeStoppedBody", normalized));
            });

            _session.SessionError += error => OnUi(() =>
            {
                if (string.IsNullOrWhiteSpace(error))
                {
                    return;
                }

                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Warning,
                    LocalizationService.Shared.Get("Loc_NotifyBridgeErrorTitle"),
                    error);
            });

            // ── 3. 更新可用（由 UpdateCheckerService 在后台触发） ──
            _updateChecker.UpdateAvailable += info => OnUi(() =>
            {
                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Info,
                    LocalizationService.Shared.Get("Loc_NotifyUpdateTitle"),
                    LocalizationService.Shared.Format(
                        "Loc_NotifyUpdateBody", info.CurrentVersion, info.LatestVersion ?? "?"));
            });

            AppLog.Info(LogFileName, "通知链路已接线（游戏识别 / 会话起停 / 会话错误 / 更新可用）");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"通知接线失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// P6 · 启动时校正开机自启。
    ///
    /// <para>
    /// 只在「已登记但路径漂移」时自动改写：便携版被搬走之后，
    /// 注册表里那条死链会一直让用户以为"自启坏了"。本机路径就是当前运行位置，
    /// 用当前进程路径去纠偏是唯一权威的来源。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 本来没开自启的用户<b>不会</b>被悄悄打开 —— 那是越权，不是贴心。
    /// </para>
    /// </summary>
    private void CheckAutoStartOnStartup()
    {
        try
        {
            _autoStart = AutoStartService.RepairIfStale();

            if (_autoStart.IsStale)
            {
                AppLog.Warn(LogFileName, $"开机自启路径仍失效：{_autoStart.RegisteredCommand}");
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"自启状态检查失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// P6 · 启动时检查控制器恢复标记。
    ///
    /// <para>
    /// 发现「上次会话异常遗留」时弹一条气泡 —— 用户此刻最可能的困惑是
    /// "我的手柄在游戏里怎么不见了"，这条通知要在他去翻日志之前就到达。
    /// </para>
    /// </summary>
    private void CheckRecoveryOnStartup()
    {
        try
        {
            _recovery = RecoveryStateService.Collect();

            foreach (string line in RecoveryStateService.Describe(_recovery))
            {
                AppLog.Info(LogFileName, "  " + line);
            }

            if (_recovery.NeedsUserAttention)
            {
                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Warning,
                    LocalizationService.Shared.Get("Loc_NotifyRecoveryTitle"),
                    LocalizationService.Shared.Get("Loc_NotifyRecoveryBody"));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"恢复状态检查失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>重新采样恢复状态，并按需刷新托盘菜单里的恢复项。</summary>
    private void RefreshRecoveryState(bool notify)
    {
        try
        {
            RecoveryStatus previous = _recovery;
            _recovery = RecoveryStateService.Collect();

            bool wasPending = previous?.NeedsUserAttention == true;
            if (notify && !wasPending && _recovery.NeedsUserAttention)
            {
                _notify.NotifyIfEnabled(
                    _settings.EnableNotifications,
                    NotificationSeverity.Warning,
                    LocalizationService.Shared.Get("Loc_NotifyRecoveryTitle"),
                    LocalizationService.Shared.Get("Loc_NotifyRecoveryBody"));
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"刷新恢复状态失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>把设置里与托盘勾选态相关的开关同步到控件（启动时与外部改动后调用）。</summary>
    private void SyncTrayPreferences()
    {
        try
        {
            TrayAutoDetectItem.IsChecked = _settings.AutoDetectGames;
            TrayAutoStartItem.IsChecked = _autoStart?.IsEffective == true;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"同步托盘开关失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 按当前会话状态刷新托盘提示文字与菜单项可见性。
    /// 菜单项的【文案】由 <c>loc:Localize.Key</c> 负责（会跟着语言热切换），
    /// 这里只管「该显示哪一个」和提示文字。
    /// </summary>
    private void UpdateTrayState()
    {
        try
        {
            bool active = _session.IsSessionActive;

            TrayStartItem.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
            TrayStopItem.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

            // ⚠️ TaskbarIcon.ToolTipText 是自己的属性，不走 ToolTipService，
            // 因此 loc:Localize 附加属性管不到它，要手工设。
            string tooltip = active
                ? LocalizationService.Shared.Format("Loc_TrayStatusActive", _session.ActiveGameTitle)
                : LocalizationService.Shared.Get("Loc_TrayTooltipStandby");

            // 上游对超长提示做过截断（App.xaml.cs:352）—— NOTIFYICONDATA 的 szTip
            // 有 128 字符上限，游戏名长一点就会被系统截得很难看，这里沿用同样处理。
            TrayIcon.ToolTipText = tooltip.Length > 63 ? tooltip[..60] + "..." : tooltip;

            // ── P6：状态行（禁用项，只读展示；带游戏名所以不走 loc） ──
            TrayStatusItem.Text = active
                ? LocalizationService.Shared.Format("Loc_TrayStatusActive", _session.ActiveGameTitle)
                : LocalizationService.Shared.Get("Loc_TrayStatusStandby");

            // ── P6：恢复项只在真有遗留时出现 ──
            bool needRecovery = _recovery?.NeedsUserAttention == true;
            TrayRecoveryItem.Visibility = needRecovery ? Visibility.Visible : Visibility.Collapsed;
            if (needRecovery)
            {
                TrayRecoveryItem.Text = LocalizationService.Shared.Get("Loc_TrayRecoveryPending");
            }

            // ── P6：两个开关的勾选态都从真源重读（设置文件 / 注册表） ──
            TrayAutoDetectItem.IsChecked = _settings.AutoDetectGames;
            TrayAutoStartItem.IsChecked = _autoStart?.IsEffective == true;

            // ── P6：语言互斥项 ──
            // RadioMenuFlyoutItem 会自行处理同组互斥，但选中态要在语言变更时对齐，
            // 否则从设置页切语言后托盘菜单会停在旧勾选上。
            bool isEn = string.Equals(
                LocalizationService.Shared.CurrentLanguage, Langs.En, StringComparison.OrdinalIgnoreCase);
            TrayLangZhItem.IsChecked = !isEn;
            TrayLangEnItem.IsChecked = isEn;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"刷新托盘状态失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>菜单每次弹出前同步一次状态，避免事件漏收导致菜单停在旧状态。</summary>
    private void OnTrayMenuOpening(object sender, object e) => UpdateTrayState();

    private void OnTrayStartBridgeClick(object sender, RoutedEventArgs e)
    {
        if (_session.IsSessionActive)
        {
            return;
        }

        string gameTitle = LocalizationService.Shared.Get("Loc_ManualBridgeGameTitle");

        // 就绪等待最长可到 20 秒（可配），放后台线程避免卡住 UI
        _ = Task.Run(() =>
        {
            bool ok = _session.StartSession(gameTitle, "standard", _settings, 0, out string? error);
            AppLog.Info(LogFileName, ok
                ? $"托盘启动桥接成功：{gameTitle}"
                : $"托盘启动桥接失败：{error}");
            OnUi(UpdateTrayState);
        });
    }

    private void OnTrayStopBridgeClick(object sender, RoutedEventArgs e)
    {
        if (!_session.IsSessionActive)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            _session.StopSession("tray-menu");
            OnUi(UpdateTrayState);
        });
    }

    private void OnTrayAutoDetectClick(object sender, RoutedEventArgs e)
    {
        _settings.AutoDetectGames = TrayAutoDetectItem.IsChecked;
        _settings.Save();

        AppLog.Info(LogFileName, $"托盘切换自动检测 = {_settings.AutoDetectGames}");
    }

    /// <summary>把动作切回 UI 线程执行。</summary>
    private void OnUi(Action action)
    {
        DispatcherQueue? queue = DispatcherQueue;
        if (queue is null || queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e) => ShowWindow();

    /// <summary>显示并激活主窗口（托盘多处共用，避免各写一份 try/catch）。</summary>
    private void ShowWindow()
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
    /// <c>--autostart</c> 启动时由 <see cref="App"/> 调用：窗口建好即藏进托盘。
    ///
    /// <para>
    /// 之所以「先建后藏」而不是「干脆不 Activate」：WinUI 3 的窗口在首次
    /// <c>Activate()</c> 之前，部分资源（Mica backdrop、标题栏、DispatcherQueue 绑定）
    /// 并未真正就绪，托盘与后续导航可能踩到空引用。
    /// 走「完整初始化 → 立刻隐藏」是唯一稳妥的顺序，代价只是启动瞬间一次极短的窗口闪现。
    /// </para>
    /// </summary>
    public void HideToTray()
    {
        try
        {
            AppWindow.Hide();
            AppLog.Info(LogFileName, "自启模式：窗口已隐藏，仅驻留托盘与进程监控");
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"自启隐藏窗口失败：{AppLog.Describe(ex)}");
        }
    }

    // ── P6：托盘菜单里新增的几个入口 ──

    /// <summary>托盘切开机自启。写的是 HKCU Run 键，立即回读真源并同步勾选态。</summary>
    private void OnTrayAutoStartClick(object sender, RoutedEventArgs e)
    {
        bool enable = TrayAutoStartItem.IsChecked;
        string? error = null;

        bool ok = enable
            ? AutoStartService.Enable(out error)
            : AutoStartService.Disable(out error);

        if (!ok)
        {
            // 写失败 → 勾选态必须回到真源，否则界面在撒谎
            _autoStart = AutoStartService.Query();
            TrayAutoStartItem.IsChecked = _autoStart.IsEffective;

            _notify.Notify(
                NotificationSeverity.Error,
                LocalizationService.Shared.Get("Loc_StartWithWindows"),
                error ?? string.Empty);
            return;
        }

        _autoStart = AutoStartService.Query();
        TrayAutoStartItem.IsChecked = _autoStart.IsEffective;

        AppLog.Info(LogFileName, $"托盘切换开机自启 → {_autoStart.State}");
    }

    private void OnTrayHardwareTestClick(object sender, RoutedEventArgs e) => OpenPage("hardware");

    private void OnTrayDiagnosticsClick(object sender, RoutedEventArgs e) => OpenPage("diagnostics");

    private void OnTrayPortableInfoClick(object sender, RoutedEventArgs e) => OpenPage("settings");

    /// <summary>打开界面并跳到指定页面（托盘快捷入口共用）。</summary>
    private void OpenPage(string tag)
    {
        ShowWindow();

        NavigationViewItem? menuItem = NavView.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(m => (m.Tag as string) == tag);

        if (menuItem is null)
        {
            AppLog.Warn(LogFileName, $"托盘跳转失败：导航项不存在（{tag}）");
            return;
        }

        NavView.SelectedItem = menuItem;
        NavigateTo(tag);
    }

    private void OnTrayCheckUpdatesClick(object sender, RoutedEventArgs e)
        => _ = CheckForUpdatesAsync(silent: false);

    /// <summary>
    /// 检查更新并给出反馈。
    ///
    /// <para>
    /// 有更新时不在这里弹气泡 —— 那是 <c>UpdateAvailable</c> 事件的职责
    /// （启动时的静默检查也走同一条路），避免同一次检查弹两条。
    /// 这里只负责「无更新 / 没发布 / 检查失败」三种<b>用户主动点击</b>才需要的反馈。
    /// </para>
    /// </summary>
    private async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            UpdateInfo info = await _updateChecker.CheckAsync(silent);

            if (info.HasUpdate)
            {
                return;   // 气泡由 UpdateAvailable 事件发出
            }

            if (silent)
            {
                return;   // 静默检查：没新版本就完全不打扰
            }

            string title = LocalizationService.Shared.Get("Loc_UpdateDialogTitle");
            string message;

            if (!info.CheckSucceeded)
            {
                message = LocalizationService.Shared.Get("Loc_UpdateError") + (info.Error ?? string.Empty);
            }
            else if (info.IsNoReleaseYet)
            {
                message = LocalizationService.Shared.Format("Loc_UpdateNoRelease", info.CurrentVersion);
            }
            else
            {
                message = LocalizationService.Shared.Format("Loc_UpdateUpToDate", info.CurrentVersion);
            }

            _notify.Notify(NotificationSeverity.Info, title, message);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"检查更新异常：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 托盘的兜底恢复入口：确认后执行引擎的 <c>restore-controller-visibility</c>。
    ///
    /// <para>
    /// ⚠️ 这**不是**在看门狗之外另起一套恢复逻辑。引擎的 <c>hidhide-watchdog</c>
    /// 负责自动恢复，这里只是给用户一个「我现在就要撤掉手柄隐藏」的手动开关 ——
    /// 场景是：看门狗已经死了/或者用户等不到下次登录。
    /// </para>
    ///
    /// <para>
    /// 走确认对话框而不是直接执行：恢复动作会改 HidHide 的全局白名单，
    /// 属于系统状态写入，不该由一个菜单点击不加确认地触发。
    /// </para>
    /// </summary>
    private async void OnTrayRecoveryClick(object sender, RoutedEventArgs e)
    {
        // ContentDialog 需要一个可见的 XamlRoot —— 托盘点击时窗口大概率是隐藏的
        ShowWindow();
        await Task.Delay(150);

        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = Content.XamlRoot,
                Title = LocalizationService.Shared.Get("Loc_RecoveryRestore"),
                Content = LocalizationService.Shared.Get("Loc_RecoveryPending"),
                PrimaryButtonText = LocalizationService.Shared.Get("Loc_RecoveryRestore"),
                CloseButtonText = LocalizationService.Shared.Get("Loc_Cancel"),
                DefaultButton = ContentDialogButton.Close,
            };

            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }

            HardwareTestService hardware = AppHost.Current.GetRequiredService<HardwareTestService>();
            EngineCommandOutcome outcome = await hardware.RunAsync("restore-controller-visibility");

            AppLog.Info(LogFileName,
                $"托盘恢复手柄可见性：退出码={outcome.ExitCode} 成功={outcome.Succeeded}");

            RefreshRecoveryState(notify: false);
            UpdateTrayState();

            EngineCommandCatalog.ExitMeaning meaning = EngineCommandCatalog.DescribeExitCode(
                "restore-controller-visibility", outcome.ExitCode);

            _notify.Notify(
                outcome.Succeeded ? NotificationSeverity.Info : NotificationSeverity.Warning,
                LocalizationService.Shared.Get("Loc_RecoveryRestore"),
                LocalizationService.Shared.Get(meaning.Key));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"托盘恢复手柄可见性失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 托盘菜单里选择界面语言（P6 起是「简体中文 / English」两个互斥项）。
    ///
    /// <para>
    /// 走 <see cref="MainWindowViewModel"/> 的路径而不是直接调语言服务：
    /// 该路径会一并写进 <c>tray_settings.json</c>，与设置页切换语言的语义完全一致
    /// —— 两处都是「用户显式改设置」。此前这里留过一句「托盘切换不应写盘」的注释，
    /// 与实现自相矛盾；P6 统一为「都写」。
    /// </para>
    /// </summary>
    private void OnTrayLanguageClick(object sender, RoutedEventArgs e)
    {
        bool wantEnglish = ReferenceEquals(sender, TrayLangEnItem);
        ViewModel.IsChineseSelected = !wantEnglish;
        SyncLanguageSelection();
        UpdateTrayState();

        AppLog.Info(LogFileName, $"托盘切换语言 → {ViewModel.CurrentLanguage}");
    }

    /// <summary>退出清理是否已跑过（防止「点 X 退出」与「托盘退出」两条路径重复清理）。</summary>
    private bool _shuttingDown;

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        AppLog.Info(LogFileName, "从托盘菜单退出");
        ShutdownCleanup("tray-exit");
        Application.Current.Exit();
    }

    /// <summary>
    /// 退出前的收尾（P6）。
    ///
    /// <para>
    /// 三件事，缺一不可：
    /// <list type="number">
    /// <item><b>停掉活动会话</b> —— 否则用户直接退出后，手柄会卡在虚拟态，
    /// 得等引擎看门狗发现属主进程死了才恢复。上游 WPF 版两处退出路径都做了这件事。</item>
    /// <item><b>解除通知引用</b> —— 让单例不再指向即将销毁的托盘图标。</item>
    /// <item><b>销毁托盘图标</b> —— 不销毁的话 Windows 会留一个「幽灵图标」，
    /// 鼠标划过之前它都还在，点它没有任何反应。</item>
    /// </list>
    /// </para>
    /// </summary>
    private void ShutdownCleanup(string reason)
    {
        if (_shuttingDown)
        {
            return;
        }
        _shuttingDown = true;

        try
        {
            if (_session.IsSessionActive)
            {
                AppLog.Info(LogFileName, $"退出清理：停止活动会话（原因：{reason}）");
                _session.StopSession(reason);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"退出时停止会话失败：{AppLog.Describe(ex)}");
        }

        try
        {
            _notify.DetachTrayIcon();
        }
        catch
        {
            // 解除引用失败不影响退出
        }

        try
        {
            TrayIcon.Dispose();
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"销毁托盘图标失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>
    /// 拦截窗口关闭。
    ///
    /// <para>
    /// 默认隐藏到托盘而不是结束进程 —— 桥接需要在后台持续工作，点 X 不该把服务一起带走
    /// （与上游 WPF 版 <c>MainWindow.xaml.cs:141</c> 的 <c>Hide()</c> 一致）。
    /// </para>
    ///
    /// <para>
    /// P6 新增：该行为可由设置页的「关闭窗口时隐藏到托盘」关掉。
    /// 关掉之后点 X 就是真的要退出 —— 此时必须显式走一遍清理，
    /// 否则托盘图标与活动会话都会以「进程被静默结束」的方式留下残渣。
    /// </para>
    /// </summary>
    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (!_settings.CloseWindowToTray)
        {
            AppLog.Info(LogFileName, "关闭窗口 = 退出（用户已关闭「隐藏到托盘」）");
            ShutdownCleanup("window-close");
            Application.Current.Exit();
            return;
        }

        args.Cancel = true;
        sender.Hide();
        AppLog.Info(LogFileName, "窗口已隐藏到托盘（进程继续运行）");

        _notify.NotifyIfEnabled(
            _settings.EnableNotifications,
            NotificationSeverity.Info,
            LocalizationService.Shared.Get("Loc_NotifyMinimizedTitle"),
            LocalizationService.Shared.Get("Loc_NotifyMinimizedBody"));
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
