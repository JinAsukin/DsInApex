using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.Views;

/// <summary>游戏库页（P3）：官方支持游戏列表与逐游戏的排除 / APEX 配置档绑定。</summary>
public sealed partial class GameLibraryPage : Page
{
    /// <summary>⚠️ 必须在 InitializeComponent 之前赋值 —— x:Bind 在解析阶段即求值。</summary>
    public GameLibraryViewModel ViewModel { get; }

    public GameLibraryPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<GameLibraryViewModel>();
        InitializeComponent();
    }

    /// <summary>
    /// 搜索文本变化即过滤。
    /// 用事件而非 x:Bind TwoWay：AutoSuggestBox.Text 的默认更新时机是失焦，
    /// 实时过滤必须自己接 TextChanged。
    /// </summary>
    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ViewModel.SearchText = sender.Text;
    }
}
