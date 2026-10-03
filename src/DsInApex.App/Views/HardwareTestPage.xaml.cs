using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.Views;

/// <summary>
/// 硬件测试页（P5）：设备识别、扳机 / 震动测试、输入监视、应急操作。
///
/// <para>
/// 引用 ViewModel 的写法与驱动页一致 —— <b>必须在 <c>InitializeComponent</c> 之前赋值</b>，
/// 因为 <c>x:Bind</c> 在 XAML 解析阶段就会求值。
/// </para>
/// </summary>
public sealed partial class HardwareTestPage : Page
{
    public HardwareTestViewModel ViewModel { get; }

    public HardwareTestPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<HardwareTestViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 停止所有活动会话。
    /// <para>
    /// 这个动作会连<b>其他程序</b>（Playnite 插件 / 官方托盘）持有的会话一起停掉，
    /// 因此必须二次确认 —— 不是防误触，是防"以为只停自己的"。
    /// </para>
    /// </summary>
    private async void OnStopSessionsClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Loc_HwConfirmStopSessions"))
        {
            ViewModel.StopSessionsCommand.Execute(null);
        }
    }

    /// <summary>
    /// 恢复手柄可见性并还原配置档。
    /// <para>用于异常退出后手柄"消失"的救场场景，同样需要确认。</para>
    /// </summary>
    private async void OnRestoreVisibilityClick(object sender, RoutedEventArgs e)
    {
        if (await ConfirmAsync("Loc_HwConfirmRestoreVisibility"))
        {
            ViewModel.RestoreVisibilityCommand.Execute(null);
        }
    }

    /// <summary>泛用确认对话框（默认焦点在「取消」，回车不会误执行）。</summary>
    private async Task<bool> ConfirmAsync(string contentKey)
    {
        ILocalizationService loc = LocalizationService.Shared;

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = loc.Get("Loc_HwConfirmTitle"),
            Content = loc.Get(contentKey),
            PrimaryButtonText = loc.Get("Loc_Confirm"),
            CloseButtonText = loc.Get("Loc_Cancel"),
            // ⚠️ 默认落在「取消」：应急操作不能靠一次回车就执行
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
