using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 学习记录 ViewModel：已识别的「可执行文件 → 游戏」绑定列表。
///
/// <para>
/// 迁移自上游 <c>GameListWindow.xaml.cs</c> 的「Learned Executables」部分
/// 与 <c>LearnedExecutablesWindow</c>。DIA 把它做成独立导航页。
/// </para>
///
/// <para>
/// 数据来源 <see cref="ExecutableLearningService"/>：进程监控在真实运行中沉淀的绑定，
/// 存放在 <c>%LOCALAPPDATA%\ApexSenseBridge\learned_executables.json</c>（与官方版共用）。
/// </para>
/// </summary>
public partial class LearnedViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_app.log";

    private readonly ExecutableLearningService _learning;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<LearnedRowItem> _all = [];

    public ObservableCollection<LearnedRowItem> Items { get; } = [];

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Message { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool CanDelete { get; set; }

    [ObservableProperty]
    public partial bool CanExport { get; set; }

    public bool IsEmpty => _all.Count == 0;

    /// <summary>请求界面弹出删除确认（VM 拿不到 XamlRoot，交给 Page 处理）。</summary>
    public event Action<int>? DeleteConfirmationRequested;

    public LearnedViewModel(ExecutableLearningService learning, ILocalizationService loc)
    {
        _learning = learning;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _learning.BindingsChanged += OnBindingsChanged;
        _learning.StateChanged += OnBindingsChanged;
        _loc.LanguageChanged += OnLanguageChanged;

        Reload();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    private void OnBindingsChanged() => OnUi(Reload);

    private void OnLanguageChanged(object? sender, string lang) => OnUi(() =>
    {
        ApplyFilter();
        UpdateButtons();
    });

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>全选 / 全不选（按当前是否有全部选中来切换）。</summary>
    [RelayCommand]
    private void ToggleSelectAll()
    {
        bool allSelected = Items.Count > 0 && Items.All(x => x.IsSelected);
        foreach (LearnedRowItem item in Items)
        {
            item.IsSelected = !allSelected;
        }
        UpdateButtons();
    }

    /// <summary>删除选中的绑定 —— 先请求界面确认。</summary>
    [RelayCommand]
    private void RequestDelete()
    {
        int count = _all.Count(x => x.IsSelected);
        if (count == 0) return;
        DeleteConfirmationRequested?.Invoke(count);
    }

    /// <summary>当前选中的条数（供界面拼删除确认文案）。</summary>
    public int SelectedCount => _all.Count(x => x.IsSelected);

    /// <summary>确认后执行删除（由 Page 在用户点「确定」后调用）。</summary>
    public void DeleteSelected()
    {
        string[] paths = _all.Where(x => x.IsSelected).Select(x => x.Path).ToArray();
        if (paths.Length == 0) return;

        int deleted = _learning.DeleteBindings(paths);
        AppLog.Info(LogFileName, $"已删除 {deleted} 条学习记录");
        Reload();
    }

    /// <summary>删除单条记录（行内删除按钮）。</summary>
    public void DeleteSingle(LearnedRowItem? row)
    {
        if (row is null) return;
        _learning.DeleteBindings([row.Path]);
        Reload();
    }

    /// <summary>把选中（或全部）绑定导出到指定文件。</summary>
    public async Task ExportAsync(string outputPath, bool onlySelected)
    {
        LearnedExecutableBinding[] selected = onlySelected
            ? _all.Where(x => x.IsSelected).Select(x => x.Binding).ToArray()
            : _all.Select(x => x.Binding).ToArray();

        (bool ok, string? error) = await Task.Run(() =>
        {
            bool success = _learning.ExportBindings(selected, outputPath, out string? err);
            return (success, err);
        }).ConfigureAwait(true);

        Message = ok
            ? _loc.Get("Loc_LearnedExportSuccess")
            : _loc.Format("Loc_LearnedExportFailed", error ?? string.Empty);

        if (!ok)
        {
            AppLog.Warn(LogFileName, $"学习记录导出失败：{error}");
        }
    }

    private void Reload()
    {
        _all.Clear();

        foreach (LearnedExecutableBinding binding in _learning.GetBindings()
                     .OrderByDescending(x => x.SuccessfulSessions)
                     .ThenBy(x => x.GameTitle, StringComparer.CurrentCultureIgnoreCase))
        {
            var row = new LearnedRowItem(binding);
            row.PropertyChanged += OnRowPropertyChanged;
            _all.Add(row);
        }

        ApplyFilter();
    }

    private void ApplyFilter()
    {
        string search = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        IEnumerable<LearnedRowItem> query = _all;
        if (search.Length > 0)
        {
            query = query.Where(x =>
                x.GameTitle.ToLowerInvariant().Contains(search) ||
                x.Executable.ToLowerInvariant().Contains(search) ||
                x.Path.ToLowerInvariant().Contains(search));
        }

        Items.Clear();
        foreach (LearnedRowItem row in query)
        {
            Items.Add(row);
        }

        int count = Items.Count;
        StatsText = _loc.Format("Loc_LearnedCount", count);

        int pending = _learning.PendingCount;
        if (pending > 0)
        {
            StatsText += " · " + _loc.Format("Loc_LearningPendingCount", pending);
        }

        OnPropertyChanged(nameof(IsEmpty));
        UpdateButtons();
    }

    private void OnRowPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LearnedRowItem.IsSelected))
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        int selectedCount = _all.Count(x => x.IsSelected);
        CanDelete = selectedCount > 0;
        CanExport = _all.Count > 0;
    }

    private void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}

/// <summary>学习记录表格中的一行。</summary>
public partial class LearnedRowItem : ObservableObject
{
    public LearnedRowItem(LearnedExecutableBinding binding) => Binding = binding;

    public LearnedExecutableBinding Binding { get; }

    public string GameTitle => !string.IsNullOrWhiteSpace(Binding.GameTitle)
        ? Binding.GameTitle
        : Binding.GameNormalized;

    public string Executable => Binding.Executable;

    public string Path => Binding.Path;

    /// <summary>识别方式；空值时回落为「自动」。</summary>
    public string DetectionMethod => string.IsNullOrWhiteSpace(Binding.DetectionMethod)
        ? "auto"
        : Binding.DetectionMethod;

    public int SuccessfulSessions => Binding.SuccessfulSessions;

    /// <summary>会话数以字符串呈现（x:Bind 不做 int→string 隐式转换）。</summary>
    public string SessionsDisplay => SuccessfulSessions.ToString(CultureInfo.CurrentCulture);

    public string LastSeenDisplay
    {
        get
        {
            if (DateTime.TryParse(
                Binding.LastSeenUtc,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out DateTime lastSeen))
            {
                return lastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
            }
            return Binding.LastSeenUtc;
        }
    }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }
}
