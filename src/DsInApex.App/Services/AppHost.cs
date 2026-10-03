using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.Extensions.DependencyInjection;

namespace DsInApex.App.Services;

/// <summary>
/// 应用服务宿主：封装 DI 容器，App 启动时构建一次。
/// </summary>
public sealed class AppHost
{
    private const string LogFileName = "dsinapex_app.log";

    private static AppHost? _current;

    /// <summary>
    /// 当前宿主实例。未初始化即访问会抛出 —— 早失败优于静默拿到空依赖。
    /// </summary>
    public static AppHost Current =>
        _current ?? throw new InvalidOperationException("AppHost 尚未初始化，请先调用 AppHost.Build()。");

    public IServiceProvider Services { get; }

    private AppHost(IServiceProvider services) => Services = services;

    /// <summary>构建容器并完成静态入口接线。</summary>
    public static AppHost Build()
    {
        var services = new ServiceCollection();
        ConfigureServices(services);

        AppHost host = new(services.BuildServiceProvider());
        _current = host;

        // ⚠️ 关键接线：XAML 附加属性（Localize）没有 DI 上下文，
        // 只能通过静态入口取语言服务。若不在此处对齐，
        // Localize 刷新用的会是另一个「无人通知」的实例，导致界面文案永不更新。
        LocalizationService.Shared = host.Services.GetRequiredService<LocalizationService>();

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

        // ─────────── App 层 ───────────
        services.AddSingleton<NotificationService>();
        services.AddSingleton<MainWindowViewModel>();
    }
}
