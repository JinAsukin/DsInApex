using DsInApex.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;

namespace DsInApex.App.Localization;

/// <summary>
/// XAML 本地化附加属性。
///
/// <para>
/// <b>为什么必须自建：</b>WinUI 3 <b>不支持 <c>{DynamicResource}</c></b>
/// （2026-10-03 Spike 以编译器级证据证实：<c>Unknown type 'DynamicResource'</c>；
/// 同一份 XAML 里 <c>{ThemeResource}</c> 却能通过）。
/// 而上游 WPF 版的整套语言切换完全建立在 DynamicResource 之上，
/// 因此在 WinUI 3 上必须换一套机制。
/// </para>
///
/// <para>
/// <b>机制：</b>附加属性 + 弱引用注册表 + 主动刷新。
/// 不依赖绑定引擎的任何特性，因而不受 WinUI 3 限制影响。
/// 实测刷新 16 个元素约 0.4ms，肉眼无感，无需重启界面。
/// </para>
///
/// <para>
/// <b>用法：</b>
/// <code>
/// &lt;TextBlock loc:Localize.Key="Loc_AppName" /&gt;
/// &lt;Button    loc:Localize.Key="Loc_BtnClose" /&gt;
/// &lt;Expander  loc:Localize.Header="Loc_SectionConfig" /&gt;   &lt;!-- 显式指定 Header --&gt;
/// &lt;Button    loc:Localize.ToolTip="Loc_BtnSync" /&gt;        &lt;!-- 显式指定 ToolTip --&gt;
/// </code>
/// </para>
/// </summary>
public static class Localize
{
    /// <summary>文案的落点策略。</summary>
    private enum Route
    {
        /// <summary>按控件类型智能路由（默认）。</summary>
        Auto,

        /// <summary>显式写入 HeaderedContentControl.Header（如 Expander）。</summary>
        Header,

        /// <summary>显式写入 ToolTipService.ToolTip。</summary>
        ToolTip,
    }

    private sealed class Entry
    {
        public required WeakReference<DependencyObject> Target { get; init; }
        public required string Key { get; init; }
        public required Route Route { get; init; }
    }

    private static readonly List<Entry> Registry = new(512);
    private static readonly object SyncRoot = new();
    private static bool _hooked;

    /// <summary>最近一次全量刷新的耗时（毫秒）。</summary>
    public static double LastRefreshMs { get; private set; }

    /// <summary>累计刷新次数。</summary>
    public static int RefreshCount { get; private set; }

    /// <summary>当前存活登记的注册项数量。</summary>
    public static int RegisteredCount
    {
        get { lock (SyncRoot) { return Registry.Count; } }
    }

    // ═══════════════ 附加属性定义 ═══════════════

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key", typeof(string), typeof(Localize),
            new PropertyMetadata(null, (d, e) => OnChanged(d, e, Route.Auto)));

    public static readonly DependencyProperty HeaderProperty =
        DependencyProperty.RegisterAttached(
            "Header", typeof(string), typeof(Localize),
            new PropertyMetadata(null, (d, e) => OnChanged(d, e, Route.Header)));

    public static readonly DependencyProperty ToolTipProperty =
        DependencyProperty.RegisterAttached(
            "ToolTip", typeof(string), typeof(Localize),
            new PropertyMetadata(null, (d, e) => OnChanged(d, e, Route.ToolTip)));

    public static void SetKey(DependencyObject element, string value)
        => element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element)
        => (string?)element.GetValue(KeyProperty);

    public static void SetHeader(DependencyObject element, string value)
        => element.SetValue(HeaderProperty, value);

    public static string? GetHeader(DependencyObject element)
        => (string?)element.GetValue(HeaderProperty);

    public static void SetToolTip(DependencyObject element, string value)
        => element.SetValue(ToolTipProperty, value);

    public static string? GetToolTip(DependencyObject element)
        => (string?)element.GetValue(ToolTipProperty);

    // ═══════════════ 刷新链路 ═══════════════

    /// <summary>
    /// 挂接语言服务的变更通知（幂等）。首次有元素注册时自动调用。
    /// </summary>
    private static void EnsureHooked()
    {
        if (_hooked) return;

        lock (SyncRoot)
        {
            if (_hooked) return;
            LocalizationService.Shared.LanguageChanged += (_, _) => RefreshAll();
            _hooked = true;
        }
    }

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e, Route route)
    {
        EnsureHooked();
        ReplaceEntry(d, (string?)e.NewValue, route);
        Apply(d, (string?)e.NewValue, route);
    }

    /// <summary>
    /// 语言切换时由语言服务事件驱动：把新语言文案写回每个已注册元素。
    /// </summary>
    public static void RefreshAll()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        lock (SyncRoot)
        {
            for (int i = Registry.Count - 1; i >= 0; i--)
            {
                Entry entry = Registry[i];

                if (!entry.Target.TryGetTarget(out DependencyObject? target))
                {
                    Registry.RemoveAt(i);   // 控件已回收，顺手清理
                    continue;
                }

                Apply(target, entry.Key, entry.Route);
            }
        }

        sw.Stop();
        LastRefreshMs = sw.Elapsed.TotalMilliseconds;
        RefreshCount++;
    }

    private static void ReplaceEntry(DependencyObject d, string? key, Route route)
    {
        lock (SyncRoot)
        {
            for (int i = 0; i < Registry.Count; i++)
            {
                if (Registry[i].Target.TryGetTarget(out DependencyObject? existing) &&
                    ReferenceEquals(existing, d))
                {
                    if (string.IsNullOrEmpty(key))
                    {
                        Registry.RemoveAt(i);
                    }
                    else
                    {
                        Registry[i] = new Entry
                        {
                            Target = new WeakReference<DependencyObject>(d),
                            Key = key,
                            Route = route,
                        };
                    }
                    return;
                }
            }

            if (!string.IsNullOrEmpty(key))
            {
                Registry.Add(new Entry
                {
                    Target = new WeakReference<DependencyObject>(d),
                    Key = key,
                    Route = route,
                });
            }
        }
    }

    /// <summary>
    /// 按策略/控件类型把文案写到正确的属性上。
    ///
    /// 顺序敏感：TextBox / TextBlock / RichTextBlock 都不在 ContentControl 继承链上，先判；
    /// Button / CheckBox / RadioButton / NavigationViewItem / ComboBoxItem 等统一走 Content。
    /// </summary>
    private static void Apply(DependencyObject d, string? key, Route route)
    {
        if (string.IsNullOrEmpty(key)) return;

        string text = LocalizationService.Shared.Get(key);

        // 显式策略优先
        if (route == Route.ToolTip)
        {
            ToolTipService.SetToolTip(d, text);
            return;
        }

        if (route == Route.Header)
        {
            // ⚠️ WinUI 3 内置控件里**没有 HeaderedContentControl**
            //（那是 CommunityToolkit 的），带 Header 的内置控件只有 Expander。
            if (d is Expander expander)
            {
                expander.Header = text;
            }
            else
            {
                // 目标不支持 Header，退化为智能路由，避免文案静默丢失
                ApplyAuto(d, text);
            }
            return;
        }

        ApplyAuto(d, text);
    }

    private static void ApplyAuto(DependencyObject d, string text)
    {
        switch (d)
        {
            case TextBlock tb:
                tb.Text = text;
                break;

            case TextBox txb:
                txb.PlaceholderText = text;
                break;

            case PasswordBox pb:
                pb.PlaceholderText = text;
                break;

            case RichTextBlock rtb:
                rtb.Blocks.Clear();
                rtb.Blocks.Add(new Paragraph { Inlines = { new Run { Text = text } } });
                break;

            // 覆盖 Button / ToggleButton / CheckBox / RadioButton /
            // NavigationViewItem / AppBarButton / ComboBoxItem / ToolTip 等
            case ContentControl cc:
                cc.Content = text;
                break;

            // 托盘右键菜单：MenuFlyoutItem 不是 ContentControl，文案走 Text
            case MenuFlyoutItem mfi:
                mfi.Text = text;
                break;

            case MenuFlyoutSeparator:
                break;   // 分隔线无文案

            default:
                // 兜底：塞进 ToolTip，至少不让文案静默消失
                ToolTipService.SetToolTip(d, text);
                break;
        }
    }

    // ═══════════════ 诊断（供探针/自检使用） ═══════════════

    /// <summary>
    /// 读取注册元素的【实际属性值】而非字典返回值。
    /// 这是验证本地化真的落到控件上、而不是只查了字典的关键手段。
    /// </summary>
    public static IReadOnlyList<string> DumpApplied()
    {
        var result = new List<string>();

        lock (SyncRoot)
        {
            foreach (Entry entry in Registry)
            {
                if (!entry.Target.TryGetTarget(out DependencyObject? target))
                {
                    result.Add($"[已回收] {entry.Key}");
                    continue;
                }

                string actual = target switch
                {
                    TextBlock tb => tb.Text,
                    TextBox txb => txb.PlaceholderText,
                    PasswordBox pb => pb.PlaceholderText,
                    // ⚠️ Expander 必须排在 ContentControl **之前** —— 它继承自 ContentControl，
                    // 放在后面会被判为不可达模式（CS8510）。
                    Expander exp => exp.Header?.ToString() ?? "(null)",
                    ContentControl cc => cc.Content?.ToString() ?? "(null)",
                    MenuFlyoutItem mfi => mfi.Text,
                    _ => "(不支持的类型)",
                };

                result.Add($"{target.GetType().Name,-20} {entry.Key,-32} => {actual}");
            }
        }

        return result;
    }
}
