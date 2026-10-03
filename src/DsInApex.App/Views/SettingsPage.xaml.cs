using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.Views;

/// <summary>
/// 设置页：检测策略、通知、强制激活、阈值、外观（P2）+ 启动与后台、便携部署、恢复状态（P6）。
///
/// <para>
/// P6 新增的三个区块都有「动作按钮」（修复自启 / 立即检查 / 刷新 / 恢复可见性），
/// 它们的共同点是<b>会碰系统状态或网络</b>，因此一律走 code-behind：
/// VM 只提供数据与文案，UI 交互（ContentDialog 需要 XamlRoot）留在页面里。
/// </para>
/// </summary>
public sealed partial class SettingsPage : Page
{
    private const string LogFileName = "dsinapex_settings.log";

    public SettingsViewModel ViewModel { get; }

    public SettingsPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<SettingsViewModel>();
        InitializeComponent();

        // 从托盘切换过语言/自启之后，本页的缓存快照可能已经过期 → 每次进入重采一次
        Loaded += (_, _) => ViewModel.RefreshAutoStart();
    }

    // ══════════════════════ P6 · 启动与后台 ══════════════════════

    /// <summary>
    /// 一键修复自启路径（便携版换目录后的标准动作）。
    /// 修复后立刻回读注册表，成败都如实反映在提示条上。
    /// </summary>
    private void OnRepairAutoStartClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RepairAutoStart();

        AppLog.Info(LogFileName, $"设置页修复自启路径 → 当前状态 {ViewModel.StartWithWindows}");
    }

    private async void OnCheckUpdatesClick(object sender, RoutedEventArgs e)
    {
        try
        {
            string message = await ViewModel.CheckUpdatesNowAsync();
            await ShowMessageAsync(LocalizationService.Shared.Get("Loc_UpdateDialogTitle"), message);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"手动检查更新失败：{AppLog.Describe(ex)}");
            await ShowMessageAsync(
                LocalizationService.Shared.Get("Loc_UpdateDialogTitle"),
                LocalizationService.Shared.Get("Loc_UpdateError") + AppLog.Describe(ex));
        }
    }

    // ══════════════════════ P6 · 控制器恢复状态 ══════════════════════

    private void OnRefreshRecoveryClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshRecovery();
        AppLog.Info(LogFileName, $"设置页刷新恢复状态 → {ViewModel.RecoverySummary}");
    }

    /// <summary>
    /// 手动兜底：执行引擎的 <c>restore-controller-visibility</c>。
    ///
    /// <para>
    /// ⚠️ 与托盘里的同名入口共用同一条引擎命令，<b>绝不自己改 HidHide 配置</b> ——
    /// 恢复逻辑的权威实现只有一个（引擎），DIA 只负责触发与呈现结果。
    /// </para>
    /// </summary>
    private async void OnRestoreVisibilityClick(object sender, RoutedEventArgs e)
    {
        var confirm = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = LocalizationService.Shared.Get("Loc_RecoveryRestore"),
            Content = LocalizationService.Shared.Get("Loc_RecoveryPending"),
            PrimaryButtonText = LocalizationService.Shared.Get("Loc_RecoveryRestore"),
            CloseButtonText = LocalizationService.Shared.Get("Loc_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirm.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        try
        {
            HardwareTestService hardware = AppHost.Current.GetRequiredService<HardwareTestService>();
            EngineCommandOutcome outcome = await hardware.RunAsync("restore-controller-visibility");

            EngineCommandCatalog.ExitMeaning meaning = EngineCommandCatalog.DescribeExitCode(
                "restore-controller-visibility", outcome.ExitCode);

            AppLog.Info(LogFileName,
                $"设置页恢复手柄可见性：退出码={outcome.ExitCode} 成功={outcome.Succeeded}");

            ViewModel.RefreshRecovery();

            await ShowMessageAsync(
                LocalizationService.Shared.Get("Loc_RecoveryRestore"),
                LocalizationService.Shared.Get(meaning.Key));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"设置页恢复手柄可见性失败：{AppLog.Describe(ex)}");
            await ShowMessageAsync(
                LocalizationService.Shared.Get("Loc_RecoveryRestore"),
                AppLog.Describe(ex));
        }
    }

    // ══════════════════════ 工具 ══════════════════════

    private async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock
            {
                Text = message,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
            },
            CloseButtonText = LocalizationService.Shared.Get("Loc_Close"),
        };

        await dialog.ShowAsync();
    }
}
