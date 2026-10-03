using System.Globalization;

namespace DsInApex.Spike.Localization;

/// <summary>
/// 本地化管理器（Spike 版）。
///
/// 与上游的差异：
/// - 上游用 Application.Current.Resources + {DynamicResource} 实现刷新 —— WinUI 3 不支持该机制；
/// - 本实现改为「事件 + 附加属性注册表」主动刷新，不依赖任何绑定引擎特性；
/// - 语言集从 en/fr 扩展为 zh-CN/en（fr 保留识别但 Spike 不提供字典）。
/// </summary>
public static class LocalizationManager
{
    private static string _current = Langs.ZhCN;

    public static string CurrentLanguage => _current;

    /// <summary>供后续接入托盘菜单、通知等非 XAML 场景。</summary>
    public static event Action? LanguageChanged;

    /// <summary>
    /// 初始化：<paramref name="preferred"/> 为 "auto" 时按系统语言自动判定。
    /// 保持与上游 tray_settings.json 的 Language 字段兼容。
    /// </summary>
    public static void Initialize(string? preferred)
    {
        SetLanguage(Resolve(preferred), notify: false);
    }

    public static void SetLanguage(string lang)
    {
        SetLanguage(lang, notify: true);
    }

    private static void SetLanguage(string lang, bool notify)
    {
        string normalized = Normalize(lang);
        bool changed = !string.Equals(normalized, _current, StringComparison.OrdinalIgnoreCase);
        _current = normalized;

        // 无论语言是否变化都要刷新一次，保证首次进入时文案已落地
        Localize.RefreshAll();

        if (notify || changed)
        {
            LanguageChanged?.Invoke();
        }
    }

    public static string Resolve(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred) ||
            string.Equals(preferred, Langs.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return DetectSystemLanguage();
        }
        return preferred;
    }

    public static string DetectSystemLanguage()
    {
        try
        {
            // 中文环境判定：zh / zh-Hans / zh-CN 全部归入 zh-CN
            var culture = CultureInfo.CurrentUICulture;
            if (culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                return Langs.ZhCN;
            }
            if (culture.TwoLetterISOLanguageName.Equals("fr", StringComparison.OrdinalIgnoreCase))
            {
                return Langs.Fr;
            }
        }
        catch
        {
            // 忽略区域性探测失败，回落到英文
        }
        return Langs.En;
    }

    private static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return Langs.ZhCN;

        if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Langs.ZhCN;
        if (lang.StartsWith("fr", StringComparison.OrdinalIgnoreCase)) return Langs.Fr;
        return Langs.En;
    }

    /// <summary>
    /// 取值。zh-CN 字典缺键时回落英文，英文也缺时返回键名本身（便于肉眼发现漏配）。
    /// </summary>
    public static string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        var primary = _current switch
        {
            Langs.Fr => Strings.En,          // fr 字典 Spike 未提供，回落英文
            Langs.En => Strings.En,
            _ => Strings.ZhCN,
        };

        if (primary.TryGetValue(key, out var value))
        {
            return value;
        }
        if (Strings.En.TryGetValue(key, out var fallback))
        {
            return fallback;
        }
        return key;
    }

    public static string Format(string key, params object?[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(template, args);
        }
        catch (FormatException)
        {
            return template;   // 占位符与参数数量不符时降级为原文，不抛异常
        }
    }
}
