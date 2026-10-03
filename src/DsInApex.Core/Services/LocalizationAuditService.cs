using System.Text;
using System.Text.RegularExpressions;
using DsInApex.Core.Localization;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 本地化覆盖审计（P8）。
///
/// <para>
/// <b>为什么要有这个服务：</b>P8 原计划里「8 个页面全量走查」和「中英文切换全页面验证」
/// 都是人肉项。但本项目最隐蔽的一类缺陷恰恰是人眼很难发现的 ——
/// <b>某个控件漏绑 <c>loc:Localize.*</c></b>：中文界面下它显示 XAML 写死的文本，
/// 整页看起来是通的，只是那一小块永远不翻译。
/// </para>
///
/// <para>
/// P6 已经踩过两次这种坑，且<b>两次编译期都毫无提示</b>：
/// <list type="bullet">
/// <item><c>Expander</c> 用 <c>loc:Localize.Key</c> → 继承自 <c>ContentControl</c>，
///       <c>ApplyAuto</c> 无 Expander 分支 → 整棵内容子树被换成一行字符串</item>
/// <item><c>MenuFlyoutSubItem</c> 漏分支 → 子菜单标题被塞进 ToolTip → 菜单里一个空白项</item>
/// </list>
/// 既然已经踩了两次，就应该变成可自动检测的检查项，而不是继续靠人眼。
/// </para>
///
/// <para>
/// <b>三条腿：</b>
/// <list type="number">
/// <item><b>字典腿</b>（始终可跑）：两种语言的键集合是否一致、占位符是否一致、双语是否同文</item>
/// <item><b>源码腿</b>（仅开发/源码树可跑）：扫 XAML 找写死文案 + <c>loc:Localize.*</c> 键是否存在。
///       便携包里没有 XAML 源码 → 自动降级为「跳过」并在日志里说明，不假装跑过</item>
/// <item><b>控件树腿</b>（运行时）：逐页读已注册 <c>loc</c> 元素的<b>实际属性值</b>，
///       在中/英下各读一遍，查「查不到键」与「两语言取值相同」</item>
/// </list>
/// </para>
///
/// <para>
/// ⚠️ 第 3 条腿是关键：查字典永远返回正确值（它查的就是字典），
/// 只有读控件真实属性才能证明<b>界面真的渲染对了</b>。理由与 P1 自检段完全一致。
/// </para>
/// </summary>
public static partial class LocalizationAuditService
{
    // ══════════════════ 编译期排除名单 ══════════════════

    /// <summary>
    /// 允许「两种语言下完全相同」的键。
    ///
    /// <para>
    /// 全部是<b>刻意不翻译</b>的：品牌名、技术型号、语言选择器自身的标签。
    /// 语言选择器那两条尤其要注意 —— 界面语言切到英文后，中文选项**仍然要显示「简体中文」**，
    /// 否则用户看到两个 English 一样的选项。这是正确行为，不是缺陷。
    /// </para>
    /// </summary>
    private static readonly HashSet<string> AllowSameTextBothLanguages = new(StringComparer.Ordinal)
    {
        "Loc_AppName",            // 品牌名 "Ds in Apex"：本来就是同一个词
        "Loc_LangZhCN",           // 语言选择器：中文项在英文界面下仍显示「简体中文」
        "Loc_LangEn",             // 语言选择器：英文项在中文界面下仍显示「English」
    };

    // ══════════════════ 腿 1 · 字典结构审计（始终可跑） ══════════════════

    /// <summary><c>{0}</c> 形式的占位符。</summary>
    [GeneratedRegex(@"\{(\d+)\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    /// <summary>取 <c>{0}</c> 形式的占位符索引集合。</summary>
    private static SortedSet<int> Placeholders(string template)
    {
        var found = new SortedSet<int>();
        foreach (Match match in PlaceholderRegex().Matches(template))
        {
            if (int.TryParse(match.Groups[1].Value, out int index))
            {
                found.Add(index);
            }
        }
        return found;
    }

    /// <summary>
    /// 审计两种语言字典的结构一致性。
    ///
    /// <para>
    /// 这是整个审计里<b>唯一在发布包里也能完整跑</b>的一条腿 ——
    /// 字典是编译进程序集的，不依赖任何外部文件。
    /// </para>
    /// </summary>
    public static (bool Passed, IReadOnlyList<LocalizationAuditFinding> Findings, IReadOnlyList<string> Notes)
        AuditDictionary()
    {
        var findings = new List<LocalizationAuditFinding>();
        var notes = new List<string>();

        IReadOnlyDictionary<string, string> zh = Strings.ZhCN;
        IReadOnlyDictionary<string, string> en = Strings.En;

        notes.Add($"字典条目        : zh-CN {zh.Count} 条 / en {en.Count} 条");

        // ── 键集合差异 ──
        var zhOnly = zh.Keys.Except(en.Keys, StringComparer.Ordinal)
                       .OrderBy(k => k, StringComparer.Ordinal).ToList();
        var enOnly = en.Keys.Except(zh.Keys, StringComparer.Ordinal)
                       .OrderBy(k => k, StringComparer.Ordinal).ToList();

        foreach (string key in zhOnly)
        {
            findings.Add(new LocalizationAuditFinding
            {
                Key = key,
                IssueKind = LocalizationAuditIssueKind.MissingInEn,
                Detail = $"键只存在于 zh-CN，英文界面下会显示键名本身（Get() 回落失败）",
            });
        }

        foreach (string key in enOnly)
        {
            findings.Add(new LocalizationAuditFinding
            {
                Key = key,
                IssueKind = LocalizationAuditIssueKind.MissingInZhCN,
                Detail = $"键只存在于 en，中文界面下会回落显示英文原文",
            });
        }

        // ── 共用键的值检查 ──
        int sameText = 0;
        int placeholderMismatch = 0;

        foreach (string key in zh.Keys.Intersect(en.Keys, StringComparer.Ordinal)
                                .OrderBy(k => k, StringComparer.Ordinal))
        {
            string zhValue = zh[key];
            string enValue = en[key];

            // 占位符一致性：Format() 遇数量不符会静默降级返回原文 → 界面露 {1} 毛刺
            SortedSet<int> zhSlots = Placeholders(zhValue);
            SortedSet<int> enSlots = Placeholders(enValue);

            if (!zhSlots.SetEquals(enSlots))
            {
                placeholderMismatch++;
                findings.Add(new LocalizationAuditFinding
                {
                    Key = key,
                    IssueKind = LocalizationAuditIssueKind.PlaceholderMismatch,
                    Detail =
                        $"占位符不一致：zh{{{string.Join(",", zhSlots)}}} / " +
                        $"en{{{string.Join(",", enSlots)}}} —— Format() 会静默降级返回模板原文",
                });
                continue;   // 占位符都不一致时，同文判定没有意义
            }

            // 双语同文：只在两边都没有占位符时才有意义
            // （有占位符的同文是巧合，如 "{0} 已就绪" vs "{0} is ready" 不会同文）
            if (zhSlots.Count == 0 &&
                string.Equals(zhValue, enValue, StringComparison.Ordinal) &&
                !AllowSameTextBothLanguages.Contains(key))
            {
                sameText++;
                findings.Add(new LocalizationAuditFinding
                {
                    Key = key,
                    IssueKind = LocalizationAuditIssueKind.SameTextInBothLanguages,
                    Detail = $"两种语言文案完全相同：\"{zhValue}\"（可能漏译，也可能是刻意保留的品牌/术语）",
                });
            }
        }

        bool balanced = zhOnly.Count == 0 && enOnly.Count == 0;

        notes.Add($"键集合一致性    : {(balanced ? "一致" : $"不一致（zh 多 {zhOnly.Count} / en 多 {enOnly.Count}）")}");
        notes.Add($"占位符不一致    : {placeholderMismatch} 条");
        notes.Add($"双语同文(可疑)  : {sameText} 条（已排除 {AllowSameTextBothLanguages.Count} 个刻意保留键）");

        // 判定：键集合一致 + 零占位符不一致
        bool passed = balanced && placeholderMismatch == 0;
        return (passed, findings, notes);
    }

    // ══════════════════ 腿 2 · XAML 源码扫描（仅源码树可跑） ══════════════════

    /// <summary>XAML 里 <c>loc:Localize.Xxx</c> 的绑定形态。</summary>
    [GeneratedRegex(@"loc:Localize\.(?<prop>Key|Header|ToolTip)\s*=\s*""(?<key>[^""]+)""",
                    RegexOptions.Compiled)]
    private static partial Regex LocalizeBindingRegex();

    /// <summary>XAML 里写死的可见文案（数据绑定与空串不算）。</summary>
    [GeneratedRegex(@"(?<prop>Text|Content|Header|Title|PlaceholderText)\s*=\s*""(?<value>[^""{}][^""]*)""",
                    RegexOptions.Compiled)]
    private static partial Regex HardcodedTextRegex();

    /// <summary>纯符号/数字/单位 —— 不是文案，漏绑检测要放过。</summary>
    [GeneratedRegex(@"^[\d\s\.\,\:\+\-\%×x/°\(\)#\*\|\[\]_·—–…→←↑↓]+$",
                    RegexOptions.Compiled)]
    private static partial Regex NotTextRegex();

    /// <summary>中文语言名 / 英文语言名 —— 语言选择器自身的标签，刻意不翻译。</summary>
    private static readonly string[] LanguageSelfLabels = ["简体中文", "English", "中文", "英文"];

    /// <summary>
    /// 扫描 XAML 源码：查 <c>loc:Localize.*</c> 的键是否存在 + 是否有写死文案疑似漏绑。
    ///
    /// <para>
    /// ⚠️ <b>便携包里没有 XAML 源码</b>（发布产物只有 <c>*.xbf</c>），
    /// 因此本腿在用户机器上会自动降级为「跳过」并在日志里写明原因 ——
    /// <b>不假装跑过</b>。发布包里的保障由腿 1（字典）与腿 3（控件树）承担，
    /// 而这两条恰好能覆盖真缺陷。
    /// </para>
    /// </summary>
    /// <param name="xamlDirectory">XAML 所在目录；不存在时返回「跳过」结果。</param>
    public static (bool Available, IReadOnlyList<LocalizationAuditFinding> Findings,
                    bool Passed, IReadOnlyList<string> Notes)
        AuditXamlSources(string xamlDirectory)
    {
        var findings = new List<LocalizationAuditFinding>();
        var notes = new List<string>();

        if (string.IsNullOrWhiteSpace(xamlDirectory) || !Directory.Exists(xamlDirectory))
        {
            notes.Add($"XAML 源码        : 不可用（{xamlDirectory}）→ 跳过源码腿");
            notes.Add("                  发布包只有 *.xbf，无源码是预期；字典腿与控件树腿不受影响");
            return (false, findings, true, notes);
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(xamlDirectory, "*.xaml", SearchOption.AllDirectories);
        }
        catch (Exception ex)
        {
            notes.Add($"XAML 源码        : 枚举失败（{ex.Message}）→ 跳过源码腿");
            return (false, findings, true, notes);
        }

        int boundCount = 0;
        int hardcodedCount = 0;
        int missingKeyCount = 0;

        foreach (string file in files)
        {
            string[] lines;
            try
            {
                lines = File.ReadAllLines(file, Encoding.UTF8);
            }
            catch
            {
                continue;
            }

            // 逐行扫：既拿到行号，又能把「绑定」与「写死」关联到同一元素上
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                // ── loc 绑定的键是否在两种语言里都存在 ──
                foreach (Match match in LocalizeBindingRegex().Matches(line))
                {
                    boundCount++;
                    string key = match.Groups["key"].Value;

                    bool inZh = Strings.ZhCN.ContainsKey(key);
                    bool inEn = Strings.En.ContainsKey(key);

                    if (!inZh && !inEn)
                    {
                        missingKeyCount++;
                        findings.Add(new LocalizationAuditFinding
                        {
                            Key = key,
                            IssueKind = LocalizationAuditIssueKind.MissingInBoth,
                            File = Path.GetFileName(file),
                            Line = i + 1,
                            Detail = $"loc 绑定的键在两种语言字典里都不存在 → 界面会直接显示 \"{key}\"",
                        });
                    }
                    else if (!inZh)
                    {
                        findings.Add(new LocalizationAuditFinding
                        {
                            Key = key,
                            IssueKind = LocalizationAuditIssueKind.MissingInZhCN,
                            File = Path.GetFileName(file),
                            Line = i + 1,
                            Detail = "loc 绑定的键只存在于 en → 中文界面回落显示英文",
                        });
                    }
                    else if (!inEn)
                    {
                        findings.Add(new LocalizationAuditFinding
                        {
                            Key = key,
                            IssueKind = LocalizationAuditIssueKind.MissingInEn,
                            File = Path.GetFileName(file),
                            Line = i + 1,
                            Detail = "loc 绑定的键只存在于 zh-CN → 英文界面显示键名本身",
                        });
                    }
                }

                // ── 同一行还写了别的可见文案？→ 疑似漏绑 ──
                if (line.Contains("loc:Localize.", StringComparison.Ordinal))
                {
                    continue;   // 这行已经绑了 loc，不算漏绑
                }

                foreach (Match match in HardcodedTextRegex().Matches(line))
                {
                    string value = match.Groups["value"].Value.Trim();
                    if (!LooksLikeText(value))
                    {
                        continue;
                    }

                    hardcodedCount++;
                    findings.Add(new LocalizationAuditFinding
                    {
                        Key = $"{match.Groups["prop"].Value}=\"{value}\"",
                        IssueKind = LocalizationAuditIssueKind.SameTextInBothLanguages,
                        File = Path.GetFileName(file),
                        Line = i + 1,
                        Detail = $"写死文案未绑 loc（{value}）—— 切英文时不会变",
                    });
                }
            }
        }

        notes.Add($"XAML 源码        : {files.Length} 个文件");
        notes.Add($"loc 绑定抽查     : {boundCount} 处，键缺失 {missingKeyCount} 处");
        notes.Add($"疑似漏绑(写死)   : {hardcodedCount} 处");

        // ⚠️ 源码腿只对「键缺失」判失败。
        // 写死文案一律只是可疑项：语言选择器的「简体中文 / English」、
        // 品牌名、技术型号都是合法的写死文案，判失败会逼人加例外名单，
        // 最后整个检查就没人看了 —— 那比漏报更糟。
        bool hasMissingKey = findings.Any(f =>
            f.IssueKind is LocalizationAuditIssueKind.MissingInBoth
                          or LocalizationAuditIssueKind.MissingInEn
                          or LocalizationAuditIssueKind.MissingInZhCN);

        return (true, findings, hasMissingKey, notes);
    }

    /// <summary>这个字符串像「用户会读的文案」吗？用于漏绑检测的白名单过滤。</summary>
    private static bool LooksLikeText(string value)
    {
        if (value.Length < 2)
        {
            return false;   // 单字符几乎都是分隔符/单位
        }

        if (NotTextRegex().IsMatch(value))
        {
            return false;   // 纯数字/符号
        }

        if (LanguageSelfLabels.Contains(value, StringComparer.Ordinal))
        {
            return false;   // 语言选择器自身的标签
        }

        // 路径 / 命令 / 文件名 —— 是数据不是文案
        if (value.Contains('\\') || value.Contains('/') ||
            value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
            value.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        // 至少含一个字母（含中日韩统一表意文字），否则是纯符号
        return value.Any(c => char.IsLetter(c));
    }

    // ══════════════════ 腿 3 · 控件树实测（运行时） ══════════════════

    /// <summary>
    /// 一条来自控件树的实测记录。
    /// </summary>
    /// <param name="Key">loc 绑定的键。</param>
    /// <param name="ControlType">控件类型名。</param>
    /// <param name="ZhText">中文下的实际属性值。</param>
    /// <param name="EnText">英文下的实际属性值。</param>
    public readonly record struct AppliedSample(
        string Key, string ControlType, string ZhText, string EnText);

    /// <summary>
    /// 判定一批控件树采样是否健康。
    ///
    /// <para>
    /// 判据只有两条，都是<b>可证伪</b>的，不含主观成分：
    /// <list type="number">
    /// <item>取值等于键名本身 → 字典里没有这个键（P8 之前没有任何检查能发现这件事）</item>
    /// <item>取值以 <c>"(</c> 开头且以 <c>")"</c> 结尾 → 命中了 <c>Localize.DumpApplied</c> 的哨兵值，
    ///       说明该控件类型不在 <c>ApplyAuto</c> 的支持列表里，文案被丢掉了</item>
    /// </list>
    /// </summary>
    public static (bool Passed, IReadOnlyList<string> Lines) VerifySamples(
        IReadOnlyList<AppliedSample> samples)
    {
        var lines = new List<string>();
        bool ok = true;

        if (samples.Count == 0)
        {
            lines.Add("  ✗ 控件树采样为 0 条 —— 说明没有任何 loc 绑定生效，机制本身没跑起来");
            return (false, lines);
        }

        int missingKey = 0;
        int unsupportedType = 0;
        int sameText = 0;

        foreach (AppliedSample sample in samples)
        {
            // 哨兵一：取值就是键名 → 字典缺键
            if (string.Equals(sample.ZhText, sample.Key, StringComparison.Ordinal))
            {
                missingKey++;
                lines.Add($"  ✗ [{sample.ControlType,-18}] {sample.Key} → 取值就是键名本身，字典里没有");
            }

            // 哨兵二：DumpApplied 的哨兵值 → 控件类型不被支持
            if (IsSentinel(sample.ZhText) || IsSentinel(sample.EnText))
            {
                unsupportedType++;
                lines.Add($"  ✗ [{sample.ControlType,-18}] {sample.Key} → {sample.ZhText} / {sample.EnText}" +
                          $"（该控件类型不在 ApplyAuto 支持列表里，文案被丢弃）");
            }
        }

        // 双语同文：只统计，不判失败（品牌名/术语合法）
        foreach (AppliedSample sample in samples)
        {
            if (!IsSentinel(sample.ZhText) && !IsSentinel(sample.EnText) &&
                string.Equals(sample.ZhText, sample.EnText, StringComparison.Ordinal) &&
                !AllowSameTextBothLanguages.Contains(sample.Key))
            {
                sameText++;
                lines.Add($"  － [{sample.ControlType,-18}] {sample.Key} → 双语同文 \"{sample.ZhText}\"" +
                          $"（可能漏译，也可能刻意保留）");
            }
        }

        lines.Add($"  采样条数        = {samples.Count}");
        lines.Add($"  字典缺键        = {missingKey} 条（真缺陷）");
        lines.Add($"  不支持的控件类型 = {unsupportedType} 条（真缺陷）");
        lines.Add($"  双语同文(可疑)  = {sameText} 条");

        ok = missingKey == 0 && unsupportedType == 0;
        return (ok, lines);
    }

    /// <summary>是否为 <c>Localize.DumpApplied</c> 的哨兵值（表示"取不到实际值"）。</summary>
    private static bool IsSentinel(string? text)
        => text is not null
           && (text.StartsWith('(') && text.EndsWith(')'));
}
