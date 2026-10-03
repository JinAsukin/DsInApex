using System.Globalization;

namespace DsInApex.Core.Localization;

/// <summary>语言服务契约（便于 DI 与单元测试替身）。</summary>
public interface ILocalizationService
{
    /// <summary>当前生效语言（已归一化，不会是 auto / fr）。</summary>
    string CurrentLanguage { get; }

    /// <summary>语言变更通知，参数为新语言标识。</summary>
    event EventHandler<string>? LanguageChanged;

    /// <summary>初始化：preferred 为 auto 时按系统语言判定。</summary>
    void Initialize(string? preferred);

    /// <summary>切换语言并触发通知。</summary>
    void SetLanguage(string lang);

    /// <summary>取文案，缺键时回落英文、再回落键名本身。</summary>
    string Get(string key);

    /// <summary>取带占位符的文案并格式化，格式错误时降级返回原文。</summary>
    string Format(string key, params object?[] args);

    /// <summary>把 auto 解析为具体语言标识。</summary>
    string Resolve(string? preferred);

    /// <summary>按系统 UI 语言判定（zh* → zh-CN，其余 → en）。</summary>
    string DetectSystemLanguage();
}

/// <summary>
/// 语言服务实现。
///
/// ⚠️ 与上游的关键差异：上游用 <c>Application.Current.Resources</c> + <c>{DynamicResource}</c>
/// 把文案推给界面，而 **WinUI 3 不支持 DynamicResource**（Spike 已编译器级证实）。
/// 因此本服务只负责「语言状态 + 字典查询」，实际刷新由 App 层的附加属性机制
/// （<c>Localize.RefreshAll()</c>）订阅 <see cref="LanguageChanged"/> 完成。
///
/// ⚠️ 关于法语：上游提供 en + fr 双语，**DIA 只提供 zh-CN + en**。
/// 旧设置里的 <c>"fr"</c> 会统一降级为英文 —— 这样语言选择器的选中态
/// 与实际呈现的语言始终一致，不会出现「没选中任何一项」的诡异状态。
/// <see cref="Langs.Fr"/> 常量保留仅用于识别上游遗留值。
/// </summary>
public sealed class LocalizationService : ILocalizationService
{
    private static LocalizationService? _shared;

    /// <summary>
    /// 供 XAML 附加属性使用的共享实例。
    /// XAML 附加属性没有 DI 上下文，只能通过静态入口取语言服务；
    /// App 启动时应把容器中的实例赋给这里，保证两边状态一致。
    /// </summary>
    public static LocalizationService Shared
    {
        get => _shared ??= new LocalizationService();
        set => _shared = value;
    }

    private string _current = Langs.ZhCN;

    public string CurrentLanguage => _current;

    public event EventHandler<string>? LanguageChanged;

    public void Initialize(string? preferred) => Apply(Resolve(preferred));

    public void SetLanguage(string lang) => Apply(lang);

    private void Apply(string lang)
    {
        _current = Normalize(lang);

        // 无论语言是否变化都通知一次，保证首次进入时界面文案已落地
        LanguageChanged?.Invoke(this, _current);
    }

    public string Resolve(string? preferred)
    {
        if (string.IsNullOrWhiteSpace(preferred) ||
            string.Equals(preferred, Langs.Auto, StringComparison.OrdinalIgnoreCase))
        {
            return DetectSystemLanguage();
        }
        return preferred;
    }

    public string DetectSystemLanguage()
    {
        try
        {
            CultureInfo culture = CultureInfo.CurrentUICulture;
            string twoLetter = culture.TwoLetterISOLanguageName;

            if (twoLetter.Equals("zh", StringComparison.OrdinalIgnoreCase))
            {
                return Langs.ZhCN;
            }

            // 上游有法语，DIA 不提供法语界面 —— 法语系统一并降级到英文
        }
        catch
        {
            // 区域性探测失败时回落英文
        }

        return Langs.En;
    }

    /// <summary>
    /// 归一化：
    /// <c>zh / zh-Hans / zh-CN / zh-TW …</c> → <c>zh-CN</c>；
    /// 其余（含上游遗留的 <c>fr</c> 与未知值）→ <c>en</c>。
    /// </summary>
    private static string Normalize(string? lang)
    {
        if (string.IsNullOrWhiteSpace(lang)) return Langs.ZhCN;

        if (lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase)) return Langs.ZhCN;

        // 上游遗留的 "fr" 走这里：DIA 不提供法语界面，降级英文
        return Langs.En;
    }

    public string Get(string key)
    {
        if (string.IsNullOrEmpty(key)) return string.Empty;

        IReadOnlyDictionary<string, string> dict =
            _current == Langs.En ? Strings.En : Strings.ZhCN;

        if (dict.TryGetValue(key, out string? value))
        {
            return value;
        }

        if (Strings.En.TryGetValue(key, out string? fallback))
        {
            return fallback;
        }

        // 缺键时返回键名本身 —— 让漏配在界面上一眼可见，而不是显示空白
        return key;
    }

    public string Format(string key, params object?[] args)
    {
        string template = Get(key);
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, args);
        }
        catch (FormatException)
        {
            // 占位符与参数数量不符时降级为原文，绝不抛异常打断界面
            return template;
        }
    }
}
