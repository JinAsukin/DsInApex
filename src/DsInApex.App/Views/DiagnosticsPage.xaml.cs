using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.Views;

/// <summary>
/// 诊断面板页（P5）：一键收集系统 / HID / PnP / XInput 诊断并导出 ZIP。
/// </summary>
public sealed partial class DiagnosticsPage : Page
{
    public DiagnosticsViewModel ViewModel { get; }

    public DiagnosticsPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<DiagnosticsViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 在资源管理器中定位刚生成的 ZIP。
    /// <para>
    /// 走 <c>explorer /select</c> 而不是自己实现文件操作 ——
    /// 用户想要的通常是"看到那个文件"，而不是"打开它"。
    /// </para>
    /// </summary>
    private void OnRevealArchiveClick(object sender, RoutedEventArgs e)
        => ViewModel.RevealArchiveCommand.Execute(null);
}
