using System;
using System.IO;
using System.Windows;
using System.Windows.Markup;
using Playnite.SDK;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 插件本地化解析器。
    ///
    /// <para>
    /// Playnite 官方机制是「把扩展目录下 <c>Localization/*.xaml</c> 按当前语言合并进
    /// Application.Resources」（见 api.playnite.link/docs/tutorials/extensions/localizations.html）。
    /// 本类在构造期<b>再显式合并一次</b>，理由是确定性：
    /// 合并顺序能由我们控制（先 en_US 基底、后当前语言），不受宿主内部加载时机影响，
    /// 且字典文件缺失时仍能保证「回退到英文」而不是返回空串导致界面空白。
    /// </para>
    ///
    /// <para>
    /// 键前缀统一 <c>LOCDsInApex_</c>：Playnite 把所有扩展的字典合进同一张资源表，
    /// 键名冲突会互相覆盖，官方要求全局唯一且带 <c>LOC</c> 前缀。
    /// </para>
    /// </summary>
    internal static class Loc
    {
        /// <summary>基底语言：任何语言缺失的键都从这里回退。</summary>
        internal const string BaseLanguage = "en_US";

        private static readonly ILogger Logger = LogManager.GetLogger();

        private static ResourceDictionary baseDictionary;
        private static ResourceDictionary activeDictionary;

        /// <summary>实际生效的语言代码（回退后）。</summary>
        internal static string ActiveLanguage { get; private set; }

        /// <summary>当前语言字典的加载结果，供自检/诊断展示。</summary>
        internal static string LoadReport { get; private set; }

        /// <summary>
        /// 加载并合并字典。<paramref name="pluginFolder"/> = 插件所在目录（含 <c>Localization\</c>）。
        /// </summary>
        internal static void Initialize(string pluginFolder, string language)
        {
            ActiveLanguage = BaseLanguage;
            LoadReport = "未加载";

            string folder = Path.Combine(pluginFolder, "Localization");
            baseDictionary = LoadDictionary(Path.Combine(folder, BaseLanguage + ".xaml"));
            MergeIntoApplication(baseDictionary);

            string requested = NormalizeLanguage(language);
            if (!string.IsNullOrEmpty(requested) &&
                !requested.Equals(BaseLanguage, StringComparison.OrdinalIgnoreCase))
            {
                ResourceDictionary localized =
                    LoadDictionary(Path.Combine(folder, requested + ".xaml"));
                if (localized != null)
                {
                    activeDictionary = localized;
                    ActiveLanguage = requested;
                    MergeIntoApplication(localized);
                }
            }

            if (activeDictionary == null)
            {
                activeDictionary = baseDictionary;
            }

            LoadReport = string.Format(
                "基底 en_US={0} · 请求语言={1} · 生效语言={2} · 生效键数={3}",
                baseDictionary == null ? "缺失" : "已加载",
                string.IsNullOrEmpty(requested) ? "(空)" : requested,
                ActiveLanguage,
                activeDictionary == null ? 0 : activeDictionary.Count);
        }

        /// <summary>取本地化文本：当前语言 → 英文基底 → 键名本身（绝不返回 null）。</summary>
        internal static string Get(string key)
        {
            object value = Lookup(activeDictionary, key);
            if (value == null)
            {
                value = Lookup(baseDictionary, key);
            }
            return value as string ?? key;
        }

        /// <summary>取本地化文本并按 <c>{0}</c> 风格格式化（占位符在两种语言中必须一致）。</summary>
        internal static string Format(string key, params object[] args)
        {
            string template = Get(key);
            try
            {
                return string.Format(template, args);
            }
            catch (FormatException ex)
            {
                Logger.Warn(ex, "本地化占位符格式错误：" + key);
                return template;
            }
        }

        private static object Lookup(ResourceDictionary dictionary, string key)
        {
            if (dictionary == null || string.IsNullOrEmpty(key))
            {
                return null;
            }
            return dictionary.Contains(key) ? dictionary[key] : null;
        }

        private static ResourceDictionary LoadDictionary(string path)
        {
            try
            {
                if (!File.Exists(path))
                {
                    Logger.Warn("本地化文件不存在：" + path);
                    return null;
                }

                using (var stream = new StreamReader(path))
                {
                    var dictionary = XamlReader.Load(stream.BaseStream) as ResourceDictionary;
                    if (dictionary == null)
                    {
                        Logger.Warn("本地化文件不是 ResourceDictionary：" + path);
                    }
                    return dictionary;
                }
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "本地化文件解析失败：" + path);
                return null;
            }
        }

        private static void MergeIntoApplication(ResourceDictionary dictionary)
        {
            if (dictionary == null)
            {
                return;
            }

            try
            {
                Application application = Application.Current;
                if (application == null)
                {
                    // 无 Application 上下文（例如离体单测）——Get() 仍可用，只是 XAML 的
                    // DynamicResource 不会生效。
                    return;
                }
                application.Resources.MergedDictionaries.Add(dictionary);
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "本地化字典合并失败（XAML 侧将退回宿主自带字典）。");
            }
        }

        /// <summary>把 Playnite 的语言串规整成 <c>xx_YY.xaml</c> 的文件名部分。</summary>
        private static string NormalizeLanguage(string language)
        {
            if (string.IsNullOrWhiteSpace(language))
            {
                return string.Empty;
            }
            return language.Trim().Replace('-', '_');
        }
    }
}
