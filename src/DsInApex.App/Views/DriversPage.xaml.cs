using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.Views;

/// <summary>
/// 驱动管理页（P4）：检测 USBip / HidHide / ViGEmBus 状态，并提供提权安装入口。
/// </summary>
public sealed partial class DriversPage : Page
{
    /// <summary>⚠️ 必须在 InitializeComponent 之前赋值 —— x:Bind 在解析阶段即求值。</summary>
    public DriversViewModel ViewModel { get; }

    public DriversPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<DriversViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 卡片上的「安装 / 修复」按钮。
    ///
    /// <para>
    /// 用 Click + Tag 而不是在 DataTemplate 里绑命令：WinUI 3 的 <c>DataTemplate</c>
    /// 无法通过 <c>RelativeSource</c> 取到页面级 ViewModel（那是 WPF 的玩法），
    /// 强行写只会在运行时静默失效。
    /// </para>
    /// </summary>
    private void OnInstallClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: DriverCardItem item })
        {
            ViewModel.InstallCommand.Execute(item);
        }
    }
}
