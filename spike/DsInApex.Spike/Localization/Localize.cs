using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.Spike.Localization;

/// <summary>
/// 候选方案 1 · 自定义附加属性。
///
/// 用法：<c>&lt;TextBlock loc:Localize.Key="Loc_AppName" /&gt;</c>
///
/// 设计要点：
/// 1. 单一附加属性 + 按控件类型智能路由，避免为每种目标属性各造一个附加属性；
/// 2. 弱引用注册表，语言切换时遍历刷新，不阻止控件回收；
/// 3. 不依赖绑定引擎的任何特性 —— 这是它能绕开 WinUI 3 无 DynamicResource 的关键。
/// </summary>
public static class Localize
{
    private sealed class Entry
    {
        public required WeakReference<DependencyObject> Target { get; init; }
        public required string Key { get; init; }
    }

    private static readonly List<Entry> Registry = new(256);

    /// <summary>最近一次全量刷新的耗时（毫秒），供 Spike 面板显示。</summary>
    public static double LastRefreshMs { get; private set; }

    /// <summary>累计刷新次数。</summary>
    public static int RefreshCount { get; private set; }

    public static readonly DependencyProperty KeyProperty =
        DependencyProperty.RegisterAttached(
            "Key",
            typeof(string),
            typeof(Localize),
            new PropertyMetadata(null, OnKeyChanged));

    public static void SetKey(DependencyObject element, string value)
        => element.SetValue(KeyProperty, value);

    public static string? GetKey(DependencyObject element)
        => (string?)element.GetValue(KeyProperty);

    private static void OnKeyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // XAML 加载期会触发；只登记与首次应用，不做额外动作。
        ReplaceEntry(d, (string?)e.NewValue);
        Apply(d, (string?)e.NewValue);
    }

    /// <summary>语言切换时调用：遍历注册表，把新语言的文案写回每个元素。</summary>
    public static void RefreshAll()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();

        for (int i = Registry.Count - 1; i >= 0; i--)
        {
            var entry = Registry[i];
            if (!entry.Target.TryGetTarget(out var target))
            {
                Registry.RemoveAt(i);   // 控件已被回收，顺手清理
                continue;
            }

            // 元素可能同时挂了多个 Key（复用场景），以当前 KeyProperty 为准
            Apply(target, GetKey(target) ?? entry.Key);
        }

        sw.Stop();
        LastRefreshMs = sw.Elapsed.TotalMilliseconds;
        RefreshCount++;
    }

    /// <summary>当前存活登记的注册项数量（诊断用）。</summary>
    public static int RegisteredCount => Registry.Count;

    /// <summary>
    /// 诊断用：读取注册元素【实际属性值】，而不是字典返回值。
    /// 这是证明附加属性真的把文案写进了控件、而非只查了字典的关键证据。
    /// </summary>
    public static IReadOnlyList<string> DumpApplied()
    {
        var result = new List<string>(Registry.Count);

        foreach (var entry in Registry)
        {
            if (!entry.Target.TryGetTarget(out var target))
            {
                result.Add($"[已回收]      {entry.Key}");
                continue;
            }

            string actual = target switch
            {
                TextBlock tb => tb.Text,
                TextBox txb => txb.PlaceholderText,
                PasswordBox pb => pb.PlaceholderText,
                ContentControl cc => cc.Content?.ToString() ?? "(null)",
                MenuFlyoutItem mfi => mfi.Text,
                _ => "(不支持的类型)"
            };

            result.Add($"{target.GetType().Name,-18} {entry.Key,-30} => {actual}");
        }

        return result;
    }

    private static void ReplaceEntry(DependencyObject d, string? key)
    {
        for (int i = 0; i < Registry.Count; i++)
        {
            if (Registry[i].Target.TryGetTarget(out var existing) && ReferenceEquals(existing, d))
            {
                if (string.IsNullOrEmpty(key))
                {
                    Registry.RemoveAt(i);
                }
                else
                {
                    Registry[i] = new Entry { Target = new WeakReference<DependencyObject>(d), Key = key };
                }
                return;
            }
        }

        if (!string.IsNullOrEmpty(key))
        {
            Registry.Add(new Entry
            {
                Target = new WeakReference<DependencyObject>(d),
                Key = key
            });
        }
    }

    /// <summary>
    /// 按控件类型把文案写到正确的属性上。
    /// 顺序敏感：TextBox / TextBlock 不在 ContentControl 继承链上，先判;
    /// Button / CheckBox / RadioButton / NavigationViewItem 等统一走 ContentControl.Content。
    /// </summary>
    private static void Apply(DependencyObject d, string? key)
    {
        if (string.IsNullOrEmpty(key)) return;

        string text = LocalizationManager.Get(key);

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
                rtb.Blocks.Add(new Microsoft.UI.Xaml.Documents.Paragraph
                {
                    Inlines = { new Microsoft.UI.Xaml.Documents.Run { Text = text } }
                });
                break;

            // 覆盖 Button / ToggleButton / CheckBox / RadioButton /
            // NavigationViewItem / AppBarButton / ComboBoxItem / ToolTip 等
            case ContentControl cc:
                cc.Content = text;
                break;

            // 托盘右键菜单：MenuFlyoutItem 不是 ContentControl，文案走 Text 属性。
            // 放在 ContentControl 之后 —— ToggleMenuFlyoutItem 继承自 MenuFlyoutItem，
            // 一并覆盖；但两者与 ContentControl 无继承关系，顺序其实不影响。
            case MenuFlyoutItem mfi:
                mfi.Text = text;
                break;

            case MenuFlyoutSeparator:
                break;   // 分隔线无文案，显式忽略

            default:
                // 兜底：把文案塞进 ToolTip，至少不会静默丢失
                ToolTipService.SetToolTip(d, text);
                break;
        }
    }
}
