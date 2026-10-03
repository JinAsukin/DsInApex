using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.App.Services;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 游戏库 ViewModel：215 条官方支持数据的搜索 / 筛选 / 排序 / 排除 / APEX 配置档绑定。
///
/// <para>
/// 迁移自上游 <c>GameListWindow.xaml.cs</c> 的「Certified Games」部分（646 行的一半）。
/// DIA 把上游「单窗口双 tab」拆成两个独立导航页（游戏库 / 学习记录），
/// 因此这里只负责游戏库本身。
/// </para>
///
/// <para>
/// <b>持久化契约：</b>「排除」写入 <c>tray_settings.json</c> 的 <c>ExcludedGames</c>，
/// 「配置档绑定」写入 <c>ApexProfileSlots</c> —— 两者都由 <see cref="TraySettings"/> 承担，
/// 与官方版共用同一份设置文件。
/// </para>
/// </summary>
public partial class GameLibraryViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_app.log";

    private readonly CloudGameListService _gameList;
    private readonly TraySettings _settings;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;
    private readonly List<GameCardItem> _all = [];

    public ObservableCollection<GameCardItem> Games { get; } = [];

    // ─────────────── 查询条件 ───────────────

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    /// <summary>0=全部 · 1=仅自适应扳机 · 2=仅触觉反馈 · 3=已排除。</summary>
    [ObservableProperty]
    public partial int FilterIndex { get; set; }

    /// <summary>0=名称 · 1=Steam AppID。</summary>
    [ObservableProperty]
    public partial int SortIndex { get; set; }

    // ─────────────── 展示状态 ───────────────

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string StatsText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string SyncStatusText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsSyncing { get; set; }

    public bool IsEmpty => Games.Count == 0;

    /// <summary>库中游戏总数（不受搜索 / 筛选影响）。供自检与诊断使用。</summary>
    public int TotalGameCount => _all.Count;

    public GameLibraryViewModel(
        CloudGameListService gameList,
        TraySettings settings,
        ILocalizationService loc)
    {
        _gameList = gameList;
        _settings = settings;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _gameList.GamesUpdated += OnGamesUpdated;
        _loc.LanguageChanged += OnLanguageChanged;

        LoadGames();
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();

    partial void OnFilterIndexChanged(int value) => ApplyFilter();

    partial void OnSortIndexChanged(int value) => ApplyFilter();

    private void OnLanguageChanged(object? sender, string lang) => OnUi(UpdateStats);

    /// <summary>云同步完成后重载列表（可能来自后台线程）。</summary>
    private void OnGamesUpdated() => OnUi(LoadGames);

    [RelayCommand]
    private void ClearSearch() => SearchText = string.Empty;

    /// <summary>从 GitHub / jsDelivr 拉取最新游戏库。失败时保留现有数据（离线降级）。</summary>
    [RelayCommand]
    private async Task SyncCloudAsync()
    {
        if (IsSyncing) return;

        IsSyncing = true;
        SyncStatusText = _loc.Get("Loc_GamesSyncing");

        try
        {
            bool ok = await Task.Run(() => _gameList.FetchLatestFromCloudAsync()).ConfigureAwait(true);
            SyncStatusText = ok
                ? _loc.Format("Loc_GamesSyncOk", _gameList.TotalGamesLoaded)
                : _loc.Get("Loc_GamesSyncFail");

            if (!ok)
            {
                AppLog.Warn(LogFileName, "游戏库云同步失败（保留本地数据）");
            }
        }
        catch (Exception ex)
        {
            SyncStatusText = _loc.Get("Loc_GamesSyncFail");
            AppLog.Warn(LogFileName, $"游戏库云同步异常：{AppLog.Describe(ex)}");
        }
        finally
        {
            IsSyncing = false;
        }
    }

    private void LoadGames()
    {
        _all.Clear();

        IReadOnlyList<SupportedGame> raw = _gameList.GetAllGames();
        foreach (SupportedGame g in raw)
        {
            var item = new GameCardItem(g);
            item.ExcludeChanged += OnItemExcludeChanged;
            item.ProfileSlotChanged += OnItemProfileSlotChanged;

            // 初始化期间不触发持久化（否则会把「读到的值」原样再写一遍盘）
            item.InitializeState(
                _settings.IsGameExcluded(g.Normalized) || _settings.IsGameExcluded(g.Title),
                _settings.GetApexProfileSlot(g.Normalized));

            _all.Add(item);
        }

        ApplyFilter();

        if (_all.Count == 0)
        {
            AppLog.Warn(LogFileName, "游戏库为空 —— 嵌入资源是否已正确打包？");
        }
    }

    private void ApplyFilter()
    {
        string search = SearchText?.Trim().ToLowerInvariant() ?? string.Empty;

        IEnumerable<GameCardItem> query = _all;

        query = FilterIndex switch
        {
            1 => query.Where(x => x.AdaptiveTriggers),
            2 => query.Where(x => x.HapticFeedback),
            3 => query.Where(x => x.IsExcluded),
            _ => query,
        };

        if (search.Length > 0)
        {
            query = query.Where(x =>
                x.Title.ToLowerInvariant().Contains(search) ||
                x.Profile.ToLowerInvariant().Contains(search));
        }

        query = SortIndex switch
        {
            1 => query.OrderBy(x => x.Game.SteamAppId).ThenBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderBy(x => x.Title, StringComparer.CurrentCultureIgnoreCase),
        };

        Games.Clear();
        foreach (GameCardItem item in query)
        {
            Games.Add(item);
        }

        // 封面按需加载（已缓存者同步命中，未缓存者后台下载后回调）
        foreach (GameCardItem item in Games)
        {
            item.EnsureCover();
        }

        UpdateStats();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void UpdateStats()
    {
        int shown = Games.Count;
        int excluded = _all.Count(x => x.IsExcluded);

        StatsText = excluded > 0
            ? _loc.Format("Loc_GamesShown", shown) + " · " + _loc.Format("Loc_GamesExcludedCount", excluded)
            : _loc.Format("Loc_GamesShown", shown);
    }

    private void OnItemExcludeChanged(GameCardItem item)
    {
        _settings.SetGameExcluded(item.Normalized, item.IsExcluded);
        _settings.Save();

        ApplyFilter();
    }

    private void OnItemProfileSlotChanged(GameCardItem item)
    {
        _settings.SetApexProfileSlot(item.Normalized, item.SelectedApexProfileSlot);
        _settings.Save();
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

/// <summary>
/// 游戏卡片的展示模型。
///
/// <para>
/// 对应上游 <c>GameItemViewModel</c>。差异：封面类型改为 WinUI <see cref="ImageSource"/>；
/// 用 <c>InitializeState</c> 抑制初始化期间的持久化回写。
/// </para>
/// </summary>
public partial class GameCardItem : ObservableObject
{
    /// <summary>首字母占位块的配色（取自上游 PlayStation 风格调色板）。</summary>
    private static readonly string[] Palette =
    [
        "#1A2F55", "#143C32", "#3C1941", "#461E1E", "#183446", "#282837",
    ];

    private bool _suppressPersist;

    public GameCardItem(SupportedGame game) => Game = game;

    public SupportedGame Game { get; }

    public string Title => Game.Title;

    public string Normalized => Game.Normalized;

    public string Profile => string.IsNullOrWhiteSpace(Game.Profile) ? "standard" : Game.Profile;

    public bool AdaptiveTriggers => Game.AdaptiveTriggers;

    public bool HapticFeedback => Game.HapticFeedback;

    /// <summary>是否使用了非标准的触摸板映射（界面显示「自定义映射」徽章）。</summary>
    public bool HasCustomRemapping
    {
        get
        {
            string p = Profile.ToLowerInvariant();
            return p is not ("standard" or "none" or "default");
        }
    }

    public bool HasSteamAppId => Game.SteamAppId > 0;

    public string SteamAppIdText => Game.SteamAppId > 0
        ? "AppID " + Game.SteamAppId
        : string.Empty;

    /// <summary>封面缺失时显示的首字母块文本。</summary>
    public string Initials
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Title)) return "?";
            string[] parts = Title.Split([' ', ':', '-', '\'', '\u2019'], StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length >= 1 && parts[1].Length >= 1)
            {
                return (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
            }
            return Title.Length >= 2 ? Title[..2].ToUpperInvariant() : Title.ToUpperInvariant();
        }
    }

    public SolidColorBrush MonogramBrush
    {
        get
        {
            int hash = Math.Abs((Title ?? "game").GetHashCode());
            return new SolidColorBrush(ParseHex(Palette[hash % Palette.Length]));
        }
    }

    [ObservableProperty]
    public partial bool IsExcluded { get; set; }

    /// <summary>APEX 板载配置档：0=保持原样，1–4=进入游戏时切到该档。</summary>
    [ObservableProperty]
    public partial int SelectedApexProfileSlot { get; set; }

    [ObservableProperty]
    public partial ImageSource? CoverImage { get; set; }

    public bool HasCover => CoverImage is not null;

    public bool HasNoCover => CoverImage is null;

    /// <summary>排除状态下卡片整体降低不透明度。</summary>
    public double CardOpacity => IsExcluded ? 0.55 : 1.0;

    public event Action<GameCardItem>? ExcludeChanged;

    public event Action<GameCardItem>? ProfileSlotChanged;

    /// <summary>初始化状态：不触发持久化回调（加载阶段用）。</summary>
    public void InitializeState(bool excluded, int profileSlot)
    {
        _suppressPersist = true;
        IsExcluded = excluded;
        SelectedApexProfileSlot = profileSlot;
        _suppressPersist = false;
    }

    /// <summary>确保封面已尝试加载（幂等；同一 URL 命中内存缓存时无网络开销）。</summary>
    public void EnsureCover()
    {
        if (CoverImage is not null || string.IsNullOrWhiteSpace(Game.IconUrl)) return;

        CoverImage = CoverCacheService.GetImage(Game.IconUrl, loaded => CoverImage = loaded);
    }

    partial void OnIsExcludedChanged(bool value)
    {
        OnPropertyChanged(nameof(CardOpacity));
        if (!_suppressPersist) ExcludeChanged?.Invoke(this);
    }

    partial void OnSelectedApexProfileSlotChanged(int value)
    {
        if (!_suppressPersist) ProfileSlotChanged?.Invoke(this);
    }

    partial void OnCoverImageChanged(ImageSource? value)
    {
        OnPropertyChanged(nameof(HasCover));
        OnPropertyChanged(nameof(HasNoCover));
    }

    private static Color ParseHex(string hex)
    {
        byte r = Convert.ToByte(hex.Substring(1, 2), 16);
        byte g = Convert.ToByte(hex.Substring(3, 2), 16);
        byte b = Convert.ToByte(hex.Substring(5, 2), 16);
        return Color.FromArgb(255, r, g, b);
    }
}
