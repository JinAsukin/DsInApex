using DsInApex.App.Services;
using DsInApex.App.ViewModels;
using DsInApex.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DsInApex.App.Views;

/// <summary>
/// 学习记录页（P3）：进程监控在真实运行中沉淀的「可执行文件 → 游戏」绑定。
///
/// <para>
/// 交互密集的部分（确认对话框、文件保存选择器）刻意留在 code-behind：
/// 它们都需要 <c>XamlRoot</c> / 窗口句柄，放进 ViewModel 只会污染其可测性。
/// </para>
/// </summary>
public sealed partial class LearnedPage : Page
{
    private readonly ILocalizationService _loc;

    /// <summary>⚠️ 必须在 InitializeComponent 之前赋值 —— x:Bind 在解析阶段即求值。</summary>
    public LearnedViewModel ViewModel { get; }

    public LearnedPage()
    {
        ViewModel = AppHost.Current.GetRequiredService<LearnedViewModel>();
        _loc = AppHost.Current.GetRequiredService<ILocalizationService>();
        InitializeComponent();
    }

    private void OnSearchTextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
        => ViewModel.SearchText = sender.Text;

    private void OnSelectAllClick(object sender, RoutedEventArgs e)
        => ViewModel.ToggleSelectAllCommand.Execute(null);

    private async void OnDeleteSelectedClick(object sender, RoutedEventArgs e)
    {
        int count = ViewModel.SelectedCount;
        if (count == 0) return;

        if (await ConfirmDeleteAsync(count))
        {
            ViewModel.DeleteSelected();
        }
    }

    private async void OnDeleteRowClick(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not LearnedRowItem row) return;

        if (await ConfirmDeleteAsync(1))
        {
            ViewModel.DeleteSingle(row);
        }
    }

    private async void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileSavePicker
            {
                SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
                SuggestedFileName = "dsinapex-learned-executables",
            };
            picker.FileTypeChoices.Add("JSON", [".json"]);

            // ⚠️ unpackaged 下必须给选择器绑定窗口句柄，否则弹不出来（Spike S6 结论）。
            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFile? file = await picker.PickSaveFileAsync();
            if (file is null) return;

            bool onlySelected = ViewModel.SelectedCount > 0;
            await ViewModel.ExportAsync(file.Path, onlySelected);
        }
        catch (Exception ex)
        {
            ViewModel.Message = ex.Message;
        }
    }

    private async Task<bool> ConfirmDeleteAsync(int count)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = _loc.Get("Loc_NavLearned"),
            Content = _loc.Format("Loc_LearnedDeleteConfirm", count),
            PrimaryButtonText = _loc.Get("Loc_Confirm"),
            CloseButtonText = _loc.Get("Loc_Cancel"),
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
