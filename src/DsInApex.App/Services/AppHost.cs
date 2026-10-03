using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.Extensions.DependencyInjection;

namespace DsInApex.App.Services;

/// <summary>
/// 应用服务宿主：封装 DI 容器，App 启动时构建一次。
/// </summary>
public sealed class AppHost
{
    private const string LogFileName = "dsinapex_app.log";

    /// <summary>
    /// 引擎会话日志文件名。**与上游同名同目录**，P8 验收脚本按这个名字取指标。
    /// </summary>
    private const string BridgeLogFileName = "tray_bridge.log";

    private static AppHost? _current;

    /// <summary>
    /// 当前宿主实例。未初始化即访问会抛出 —— 早失败优于静默拿到空依赖。
    /// </summary>
    public static AppHost Current =>
        _current ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 AppHost.Build()。");

    public IServiceProvider Services { get; }

    private AppHost(IServiceProvider services) => Services = services;

    /// <summary>
    /// 构建容器并完成静态入口接线。
    /// </summary>
    /// <param name="initializeRuntime">
    /// 是否初始化「运行时服务」（游戏库载入 + 进程监控）。
    /// 提权辅助模式（<c>--driver-action</c>）传 <c>false</c> ——
    /// 那个进程只做一件安装事就退出，没必要拉起进程监控与 WMI 监听。
    /// </param>
    public static AppHost Build(bool initializeRuntime = true)
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        AppHost host = new(services.BuildServiceProvider());
        _current = host;

        // ⚠️ 关键接线：XAML 附加属性（Localize）没有 DI 上下文，
        // 只能通过静态入口取语言服务。若不在此处对齐，
        // Localize 刷新用的会是另一个「无人通知」的实例，导致界面文案永不更新。
        LocalizationService.Shared = host.Services.GetRequiredService<LocalizationService>();

        // ⚠️ 关键接线（P8 验收工具链依赖）：把会话过程日志写进 `tray_bridge.log`。
        //
        // 上游 App.xaml.cs:179 也是这么挂的，且**文件名与目录必须一致** ——
        // 该文件是 P8 端到端验收的取证来源（audio_haptics_active_percent /
        // apex_original_restored 等指标都从这里读）。DIA 侧改名就等于让验收脚本失明。
        EngineSessionManager session = host.Services.GetRequiredService<EngineSessionManager>();
        session.LogMessage += msg => AppLog.WriteLine(BridgeLogFileName, msg);

        if (!initializeRuntime)
        {
            AppLog.Info(LogFileName, "DI 容器已构建（轻量模式：跳过游戏库与进程监控初始化）");
            return host;
        }

        // ── P3 接线 ──
        // 1) 游戏库：先读本地缓存，缺失/损坏则回落嵌入资源（215 条）。
        CloudGameListService gameList = host.Services.GetRequiredService<CloudGameListService>();
        gameList.Initialize();

        // 2) 学习缓存异步载入，不阻塞启动。
        host.Services.GetRequiredService<ExecutableLearningService>().InitializeAsync();

        // 3) 解析 ProcessMonitorService 本身即完成「启动监控」——
        //    它在构造函数里注册 WMI 事件并启动 250ms 轮询。
        //    ⚠️ 不要删掉这一步：少了它，游戏库能看，但永远不会自动识别游戏。
        _ = host.Services.GetRequiredService<ProcessMonitorService>();
        TraySettings traySettings = host.Services.GetRequiredService<TraySettings>();
        AppLog.Info(LogFileName,
            $"进程监控已启动：游戏库 {gameList.TotalGamesLoaded} 款，自动检测={traySettings.AutoDetectGames}");

        AppLog.Info(LogFileName, "DI 容器已构建");
        return host;
    }

    public T GetRequiredService<T>() where T : notnull
        => Services.GetRequiredService<T>();

    private static void ConfigureServices(IServiceCollection services)
    {
        // ─────────── Core 层 ───────────
        // 设置：单例（全应用共享同一份，保存时写回同一文件）
        services.AddSingleton(_ => TraySettings.Load());

        // 语言服务：注册为具体类型 + 接口别名，保证两处拿到同一实例
        services.AddSingleton<LocalizationService>();
        services.AddSingleton<ILocalizationService>(sp => sp.GetRequiredService<LocalizationService>());

        // 桥接会话状态机：**必须是单例** —— 会话是全局唯一资源，
        // 仪表盘、托盘、设置页都要读同一份状态，多实例会导致状态分裂。
        services.AddSingleton<EngineSessionManager>();

        // ─────────── App 层 ───────────
        services.AddSingleton<NotificationService>();
        services.AddSingleton<MainWindowViewModel>();

        // 页面 VM 也用单例：跨页面切换时保持会话订阅与展示状态，
        // 避免每次导航都重新订阅事件（会累积重复订阅）。
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<SettingsViewModel>();

        // ─────────── P3：游戏库 + 进程监控 ───────────
        // 游戏库：云同步成功会广播 GamesUpdated，必须单例。
        services.AddSingleton<CloudGameListService>();

        // 学习缓存：与官方版共用同一份 learned_executables.json，单例避免并发写坏。
        services.AddSingleton<ExecutableLearningService>();

        // 进程监控：**必须单例** —— 持有 WMI watcher 与 250ms 轮询定时器，
        // 多实例会导致重复识别、重复起会话。
        services.AddSingleton<ProcessMonitorService>();

        services.AddSingleton<GameLibraryViewModel>();
        services.AddSingleton<LearnedViewModel>();

        // ─────────── P4：驱动管理 ───────────
        // 三层分明：清单只读数据、检测只读系统、安装才写系统。
        services.AddSingleton<DriverManifestService>();
        services.AddSingleton<DriverDetectionService>();
        services.AddSingleton<DriverInstallerService>();
        services.AddSingleton<DriversViewModel>();

        // ─────────── P5：硬件测试 + 诊断 ───────────
        // 硬件测试服务持有引擎命令的安全边界（会话互斥、只读重试），无状态，单例即可。
        services.AddSingleton<HardwareTestService>();

        // 诊断收集器：一次收集会开临时目录、跑 WMI 与 XInput 采样，
        // 必须单例 —— 两个实例并发收集会同时抢 XInput 采样窗口。
        services.AddSingleton<DiagnosticsCollectorService>();

        services.AddSingleton<HardwareTestViewModel>();
        services.AddSingleton<DiagnosticsViewModel>();

        // ─────────── P6：托盘与后台行为 ───────────
        // 更新检查：无状态但有事件订阅方（托盘气泡），单例保证订阅关系唯一。
        // ⚠️ 每次检查都会新建 HttpClient（与 CloudGameListService 同款做法），
        //    因此不存在「长命客户端 DNS 过期」的问题。
        services.AddSingleton<UpdateCheckerService>();
    }
}
