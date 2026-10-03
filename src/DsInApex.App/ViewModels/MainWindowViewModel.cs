using CommunityToolkit.Mvvm.ComponentModel;
using DsInApex.App.Models;
using DsInApex.App.Views;
using DsInApex.Core.Localization;
using DsInApex.Core.Models;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 主窗口 ViewModel：导航项、语言切换、主题切换。
/// </summary>
public partial class MainWindowViewModel : ObservableObject
{
    private readonly ILocalizationService _loc;
    private readonly TraySettings _settings;

    public MainWindowViewModel(ILocalizationService loc, TraySettings settings)
    {
        _loc = loc;
        _settings = settings;
    }

    /// <summary>
    /// 八个一级导航页。P1 阶段全部为占位页，P2–P5 逐步填充真实内容。
    /// 图标为 Segoe Fluent Icons 字形。
    /// </summary>
    public IReadOnlyList<NavigationItem> NavigationItems { get; } =
    [
        new("dashboard",   "Loc_NavDashboard",    "\uE80F", typeof(DashboardPage)),
        new("gamelib",     "Loc_NavGameLibrary",  "\uE7FC", typeof(GameLibraryPage)),
        new("learned",     "Loc_NavLearned",      "\uE82D", typeof(LearnedPage)),
        new("drivers",     "Loc_NavDrivers",      "\uE90F", typeof(DriversPage)),
        new("hardware",    "Loc_NavHardwareTest", "\uE7C1", typeof(HardwareTestPage)),
        new("diagnostics", "Loc_NavDiagnostics",  "\uE9D9", typeof(DiagnosticsPage)),
        new("settings",    "Loc_NavSettings",     "\uE713", typeof(SettingsPage)),
        new("about",       "Loc_NavAbout",        "\uE946", typeof(AboutPage)),
    ];

    /// <summary>当前选中的导航项。</summary>
    [ObservableProperty]
    public partial NavigationItem? SelectedItem { get; set; }

    // ═══════════════ 语言 ═══════════════

    /* 说明：语言选择器绑这两个布尔而不是直接绑字符串，
       因为 RadioButton.IsChecked 是 bool，且需要双向同步选中态。 */

    public bool IsChineseSelected
    {
        get => _loc.CurrentLanguage == Langs.ZhCN;
        set { if (value) SwitchLanguage(Langs.ZhCN); }
    }

    public bool IsEnglishSelected
    {
        get => _loc.CurrentLanguage == Langs.En;
        set { if (value) SwitchLanguage(Langs.En); }
    }

    /// <summary>当前语言标识（供界面显示）。</summary>
    public string CurrentLanguage => _loc.CurrentLanguage;

    private void SwitchLanguage(string lang)
    {
        if (string.Equals(lang, _loc.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _loc.SetLanguage(lang);

        // 持久化到 tray_settings.json（与上游同一文件，旧版可无缝升级）
        _settings.Language = lang;
        _settings.Save();

        NotifyLanguageChanged();
    }

    /// <summary>
    /// 供外部（托盘菜单等）切换语言后同步界面选中态。
    /// </summary>
    public void NotifyLanguageChanged()
    {
        OnPropertyChanged(nameof(IsChineseSelected));
        OnPropertyChanged(nameof(IsEnglishSelected));
        OnPropertyChanged(nameof(CurrentLanguage));
    }

    // ═══════════════ 主题 ═══════════════

    public bool IsSystemThemeSelected
    {
        get => _settings.Theme == "system";
        set { if (value) SwitchTheme("system"); }
    }

    public bool IsLightThemeSelected
    {
        get => _settings.Theme == "light";
        set { if (value) SwitchTheme("light"); }
    }

    public bool IsDarkThemeSelected
    {
        get => _settings.Theme == "dark";
        set { if (value) SwitchTheme("dark"); }
    }

    /// <summary>主题切换时触发，由窗口层实际应用（VM 不碰 UI）。</summary>
    public event EventHandler<string>? ThemeChangeRequested;

    private void SwitchTheme(string theme)
    {
        if (string.Equals(theme, _settings.Theme, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        _settings.Theme = theme;
        _settings.Save();

        ThemeChangeRequested?.Invoke(this, theme);

        OnPropertyChanged(nameof(IsSystemThemeSelected));
        OnPropertyChanged(nameof(IsLightThemeSelected));
        OnPropertyChanged(nameof(IsDarkThemeSelected));
    }

    /// <summary>启动时按已保存的设置应用一次主题。</summary>
    public void ApplySavedTheme() => ThemeChangeRequested?.Invoke(this, _settings.Theme);
}
