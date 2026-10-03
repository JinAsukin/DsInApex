using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace DsInApex.App.Views;

/// <summary>
/// 仪表盘页（P2）：桥接状态徽章、当前游戏、扳机/触觉指示灯、快捷操作。
/// </summary>
public sealed partial class DashboardPage : Page
{
    public DashboardViewModel ViewModel { get; }

    public DashboardPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<DashboardViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 每次进入页面同步一次设置项（指示灯）与语言文案 ——
    /// 设置页与仪表盘是两个 VM，用「进入时拉取」代替跨 VM 事件订阅，简单且不会漏。
    /// </summary>
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.RefreshFromSettings();
    }
}
