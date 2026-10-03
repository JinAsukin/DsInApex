using System.Security.Cryptography;
using System.Text;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DsInApex.App.Diagnostics;

/// <summary>
/// 应用自检。
///
/// 触发方式：设置环境变量 <c>DIA_SELFTEST=1</c> 后启动应用。
///
/// <para>
/// <b>设计原则：只读不写业务状态。</b>
/// 自检会在界面上留下痕迹（遍历导航、临时切语言），也会做一次设置文件的写入往返测试，
/// 但凡是动过磁盘的地方都必须<b>原样恢复</b>并留下核对记录。
/// 唯一的例外是本地化日志文件与设置日志文件（追加写入，属正常日志行为）。
/// </para>
///
/// <para>
/// 覆盖范围随阶段扩展：
///  P1 —— 导航壳可用（8 页面路由）、中英热切换生效（读控件真实文案）。
///  P2 —— 页面内容落地（页面内文案非空且与语言一致）、设置读写往返一致。
///  P8 —— 在此基础上扩展真实硬件检查（手柄在位、桥接会话、自适应扳机）。
/// </para>
/// </summary>
public static class SelfTest
{
    /// <summary>环境变量名。</summary>
    public const string EnvVarName = "DIA_SELFTEST";

    private const string LogFileName = "dsinapex_selftest.log";

    /// <summary>可视化树遍历的最大深度（防御性上限，避免异常结构导致栈溢出）。</summary>
    private const int MaxTreeDepth = 24;

    /// <summary>本次启动是否请求了自检。</summary>
    public static bool IsRequested =>
        string.Equals(Environment.GetEnvironmentVariable(EnvVarName), "1", StringComparison.Ordinal);

    public static void Log(string message) => AppLog.Info(LogFileName, message);

    public static void LogHeader(string title)
        => AppLog.Info(LogFileName, $"===== {title} =====");

    // ══════════════════════ 页面内容检查（P2） ══════════════════════

    /// <summary>
    /// 收集可视化树里的全部非空 <see cref="TextBlock.Text"/>。
    ///
    /// <para>
    /// 为什么要读控件实际值：本地化机制失效时，字典查询依然会返回正确文案
    /// （因为它查的就是字典），但界面上可能一个字都没渲染。
    /// 只有读控件真实属性值，才能证明"文案真的落到 UI 上了"。
    /// </para>
    /// </summary>
    public static List<string> CollectTexts(DependencyObject? root)
    {
        var sink = new List<string>();
        if (root is not null)
        {
            Walk(root, sink, 0);
        }
        return sink;

        static void Walk(DependencyObject node, List<string> sink, int depth)
        {
            if (depth > MaxTreeDepth || sink.Count >= 64) return;

            int count;
            try
            {
                count = VisualTreeHelper.GetChildrenCount(node);
            }
            catch
            {
                return;   // 某些节点（如 ContentPresenter 未实例化）会抛，跳过即可
            }

            for (int i = 0; i < count; i++)
            {
                DependencyObject child;
                try
                {
                    child = VisualTreeHelper.GetChild(node, i);
                }
                catch
                {
                    continue;
                }

                if (child is TextBlock tb && !string.IsNullOrWhiteSpace(tb.Text))
                {
                    sink.Add(tb.Text);
                }

                Walk(child, sink, depth + 1);
            }
        }
    }

    // ══════════════════════ 设置读写往返检查（P2） ══════════════════════

    /// <summary>
    /// 设置文件读写往返测试。
    ///
    /// <para>
    /// <b>安全性约定（重要）：</b>本方法会真的写入
    /// <c>%LOCALAPPDATA%\ApexSenseBridge\tray_settings.json</c>，
    /// 因此在开始前备份原始字节、结束后无条件恢复，并用 SHA-256 核对恢复是否精确。
    /// 原文件不存在的场合，结束时会删除而不是留下一个默认值文件。
    /// </para>
    ///
    /// <para>
    /// 返回逐行结果，供调用方写入自检日志。返回值第二项表示整体是否通过。
    /// </para>
    /// </summary>
    public static (bool Passed, IReadOnlyList<string> Lines) RunSettingsRoundTrip()
    {
        var lines = new List<string>();
        string path = TraySettings.FilePath;

        bool existedBefore = File.Exists(path);
        byte[]? backup = null;

        try
        {
            if (existedBefore)
            {
                backup = File.ReadAllBytes(path);
            }
        }
        catch (Exception ex)
        {
            lines.Add($"  ✗ 无法备份原设置文件：{AppLog.Describe(ex)}");
            return (false, lines);
        }

        string beforeHash = backup is null ? "(文件不存在)" : Sha256(backup);
        lines.Add($"  设置文件        : {path}");
        lines.Add($"  测试前状态      : {(existedBefore ? "存在" : "不存在")}  hash={Short(beforeHash)}");

        bool passed;
        try
        {
            // ── 写入：用不易与真实值混淆的探针值 ──
            TraySettings probe = TraySettings.Load();

            int originalThreshold = probe.HapticThresholdPercent;
            string originalTheme = probe.Theme;

            int probeThreshold = originalThreshold == 77 ? 78 : 77;
            string probeTheme = originalTheme == "light" ? "dark" : "light";
            const string probeGame = "__DsInApexSelfTest__";

            probe.HapticThresholdPercent = probeThreshold;
            probe.Theme = probeTheme;
            probe.SetGameExcluded(probeGame, true);
            probe.Save();

            // ── 读回：全部字段逐一核对 ──
            TraySettings reloaded = TraySettings.Load();

            bool okThreshold = reloaded.HapticThresholdPercent == probeThreshold;
            bool okTheme = string.Equals(reloaded.Theme, probeTheme, StringComparison.Ordinal);
            bool okExcluded = reloaded.IsGameExcluded(probeGame);

            lines.Add($"  写入 hapticThreshold : {originalThreshold} → {probeThreshold}");
            lines.Add($"  回读 hapticThreshold : {reloaded.HapticThresholdPercent,-6} {(okThreshold ? "✓" : "✗ 不一致")}");
            lines.Add($"  写入 theme           : {originalTheme} → {probeTheme}");
            lines.Add($"  回读 theme           : {reloaded.Theme,-6} {(okTheme ? "✓" : "✗ 不一致")}");
            lines.Add($"  集合字段 ExcludedGames 往返 : {(okExcluded ? "✓ 命中探针项" : "✗ 探针项丢失")}");

            bool okSlot = ProbeProfileSlot(reloaded, lines);

            passed = okThreshold && okTheme && okExcluded && okSlot;
        }
        catch (Exception ex)
        {
            lines.Add($"  ✗ 往返测试异常：{AppLog.Describe(ex)}");
            passed = false;
        }
        finally
        {
            // ── 恢复原状：必须无条件执行 ──
            try
            {
                if (backup is not null)
                {
                    File.WriteAllBytes(path, backup);
                }
                else if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (Exception ex)
            {
                lines.Add($"  ✗✗ 恢复原设置文件失败，请手工检查：{AppLog.Describe(ex)}");
                passed = false;
            }
        }

        // ── 核对恢复是否精确（哈希比对，比"看起来对"可靠） ──
        try
        {
            bool restoredExactly;
            if (backup is not null)
            {
                restoredExactly = File.Exists(path) && Sha256(File.ReadAllBytes(path)) == beforeHash;
            }
            else
            {
                restoredExactly = !File.Exists(path);
            }

            lines.Add($"  测试后状态      : {(backup is not null ? "已还原" : "已删除")}  hash={(backup is not null ? Short(Sha256(File.ReadAllBytes(path))) : "(文件不存在)")}");
            lines.Add($"  原样恢复        : {(restoredExactly ? "✓ 是" : "✗ 否（哈希不一致，请手工检查）")}");

            if (!restoredExactly) passed = false;
        }
        catch (Exception ex)
        {
            lines.Add($"  ✗ 恢复核对异常：{AppLog.Describe(ex)}");
            passed = false;
        }

        lines.Add($"设置往返结果    : {(passed ? "通过" : "失败")}");
        return (passed, lines);
    }

    /// <summary>顺带验证 APEX 配置档字段（Dictionary&lt;string,int&gt;，旧文件常见缺失）。</summary>
    private static bool ProbeProfileSlot(TraySettings settings, List<string> lines)
    {
        const string probeGame = "__DsInApexSelfTest__";
        try
        {
            settings.SetApexProfileSlot(probeGame, 3);
            settings.Save();

            TraySettings reloaded = TraySettings.Load();
            int slot = reloaded.GetApexProfileSlot(probeGame);
            bool ok = slot == 3;

            lines.Add($"  字典字段 ApexProfileSlots 往返 : {(ok ? "✓ 值为 3" : $"✗ 值为 {slot}")}");

            // 清理探针项（写入后马上移除，避免污染随后的 ExcludedGames 判定）
            reloaded.SetApexProfileSlot(probeGame, 0);
            reloaded.Save();
            return ok;
        }
        catch (Exception ex)
        {
            lines.Add($"  ✗ 配置档字段测试异常：{AppLog.Describe(ex)}");
            return false;
        }
    }

    // ══════════════════════ 引擎桥接日志校验（P2 真实会话冒烟） ══════════════════════

    /// <summary>
    /// 真实会话冒烟开关。**必须与 <c>DIA_SELFTEST=1</c> 同时设置**，
    /// 且只在手柄已在 DInput 模式接入时才该开启 —— 它会真实接管手柄。
    /// </summary>
    public const string SessionSmokeEnvVarName = "DIA_SESSION_SMOKE";

    public static bool SessionSmokeRequested =>
        string.Equals(Environment.GetEnvironmentVariable(SessionSmokeEnvVarName), "1", StringComparison.Ordinal);

    // ══════════════════════ 诊断链路冒烟（P5） ══════════════════════

    /// <summary>
    /// 诊断链路冒烟开关。**必须与 <c>DIA_SELFTEST=1</c> 同时设置**。
    ///
    /// <para>
    /// 自检主体只跑只读子集（list / diagnose / xinput-status），
    /// 开启本开关后会额外把整条链路走完：
    /// <c>input-status --json</c>（1 秒采样）、<c>virtual-ds --json</c>（1 秒）、
    /// 以及一次<b>完整诊断收集</b>（跳过 XInput 采样）到临时目录，
    /// 用来证明 7 步流程与 ZIP 产出真的可用。
    /// </para>
    ///
    /// <para>
    /// 全部是只读命令 + 临时文件写入，<b>不会驱动手柄</b>。
    /// （<c>virtual-ds</c> 会短暂创建一个虚拟 DualSense，命令结束即清理。）
    /// </para>
    /// </summary>
    public const string DiagnosticsSmokeEnvVarName = "DIA_DIAGNOSTICS_SMOKE";

    public static bool DiagnosticsSmokeRequested =>
        string.Equals(Environment.GetEnvironmentVariable(DiagnosticsSmokeEnvVarName), "1", StringComparison.Ordinal);

    /// <summary>引擎桥接日志文件名（与上游一致，不随品牌改名）。</summary>
    private const string BridgeLogFileName = "tray_bridge.log";

    public static string BridgeLogPath => Path.Combine(AppLog.DefaultDirectory, BridgeLogFileName);

    /// <summary>取桥接日志当前字节长度，作为「本次会话」的读取起点。</summary>
    public static long CurrentBridgeLogLength()
    {
        try
        {
            var info = new FileInfo(BridgeLogPath);
            return info.Exists ? info.Length : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 校验「本次会话」新增的桥接日志是否满足 <b>P8 端到端验收基准</b>
    /// （该基准取自官方 v0.6.3 在 APEX 4 上的实跑记录，见项目记忆）：
    /// <list type="bullet">
    /// <item><c>audio_haptics_active_percent</c> 有非零值</item>
    /// <item><c>audio_haptics_timeouts=0</c></item>
    /// <item><c>last_active_lt_ds_type</c> / <c>last_active_rt_ds_type</c> 有非零类型</item>
    /// <item><c>last_active_lt_apex</c> / <c>last_active_rt_apex</c> 有实际参数</item>
    /// <item><c>apex_original_restored=yes</c>（手柄被还原 —— 最关键的一条）</item>
    /// </list>
    /// </summary>
    public static (bool Passed, IReadOnlyList<string> Lines) VerifyBridgeLogSince(long offset)
    {
        var lines = new List<string>();

        string text;
        try
        {
            if (!File.Exists(BridgeLogPath))
            {
                lines.Add($"  ✗ 桥接日志不存在：{BridgeLogPath}");
                return (false, lines);
            }

            // 引擎进程可能仍在写这个文件 → 必须以 FileShare.ReadWrite 打开
            using var fs = new FileStream(BridgeLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < offset)
            {
                offset = 0;   // 日志被轮转/清空过，退回全量读取
            }
            fs.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception ex)
        {
            lines.Add($"  ✗ 读取桥接日志失败：{AppLog.Describe(ex)}");
            return (false, lines);
        }

        lines.Add($"  日志文件        : {BridgeLogPath}");
        lines.Add($"  本次新增        : {text.Length} 字符");

        if (text.Length == 0)
        {
            lines.Add("  ✗ 本次会话没有产生任何桥接日志 —— 说明引擎根本没跑起来");
            return (false, lines);
        }

        bool ok = true;

        // ── 必判项：引擎健康指标。**不需要任何游戏输入，必须全部满足** ──
        lines.Add("  ── 必判项（引擎健康） ──");

        ok &= CheckMetric(lines, text, "apex_routing",
            v => string.Equals(v, "adaptive-triggers", StringComparison.OrdinalIgnoreCase),
            "自适应扳机路由已启用");

        ok &= CheckMetric(lines, text, "input_reports_lost",
            v => string.Equals(v, "0", StringComparison.Ordinal), "输入报告零丢失");

        ok &= CheckMetric(lines, text, "virtual_input_neutralized",
            v => string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase), "虚拟输入已中立化");

        ok &= CheckMetric(lines, text, "audio_haptics_timeouts",
            v => string.Equals(v, "0", StringComparison.Ordinal), "音频触觉超时 = 0");

        // ★ 最关键的验收指标：手柄必须被还原回原始状态，否则用户的手柄会卡在虚拟态
        ok &= CheckMetric(lines, text, "apex_original_restored",
            v => string.Equals(v, "yes", StringComparison.OrdinalIgnoreCase), "手柄已还原原状 ★");

        // ── 条件判项：需要真实输入才有数据，没有时如实标注「跳过」而非判失败 ──
        //
        // ⚠️ 关键认知（已核对引擎源码 RumbleBridge.cpp:34）：
        // `FeedbackKind::AudioHaptics` 来自**游戏发往虚拟 DualSense 的音频/触觉输出报告**，
        // 不是系统扬声器的回环捕获。因此没有游戏时 audio_haptics_processed 恒为 0，
        // 在扬声器上放音乐也不会改变它 —— 这是设计使然，不是故障。
        // 同理 `last_active_*_ds_type` 需要游戏下发力反馈指令。
        lines.Add("  ── 条件判项（需真实游戏输入） ──");

        string? processedRaw = LastValue(text, "audio_haptics_processed");
        bool hasAudioInput = processedRaw is not null
                             && double.TryParse(processedRaw, out double processed)
                             && processed > 0;

        if (hasAudioInput)
        {
            ok &= CheckMetric(lines, text, "audio_haptics_active_percent",
                v => double.TryParse(v, out double d) && d > 0, "音频触觉活跃占比 > 0");
        }
        else
        {
            lines.Add($"  － 音频触觉活跃占比    = 跳过（本次无游戏音频触觉输入，processed={processedRaw ?? "缺失"}）");
        }

        string? ltTypeRaw = LastValue(text, "last_active_lt_ds_type");
        string? rtTypeRaw = LastValue(text, "last_active_rt_ds_type");
        bool hasTriggerInput =
            (ltTypeRaw is not null && double.TryParse(ltTypeRaw, out double lt) && lt != 0) ||
            (rtTypeRaw is not null && double.TryParse(rtTypeRaw, out double rt) && rt != 0);

        if (hasTriggerInput)
        {
            ok &= CheckMetric(lines, text, "last_active_lt_ds_type",
                v => double.TryParse(v, out double d) && d != 0, "左扳机 DS 类型非零");
            ok &= CheckMetric(lines, text, "last_active_rt_ds_type",
                v => double.TryParse(v, out double d) && d != 0, "右扳机 DS 类型非零");
            ok &= CheckMetric(lines, text, "last_active_lt_apex",
                HasNonZeroComponent, "左扳机 APEX 参数非全零");
            ok &= CheckMetric(lines, text, "last_active_rt_apex",
                HasNonZeroComponent, "右扳机 APEX 参数非全零");
        }
        else
        {
            lines.Add($"  － 扳机力反馈活跃指标  = 跳过（本次无游戏力反馈输入，lt={ltTypeRaw ?? "缺失"} rt={rtTypeRaw ?? "缺失"}）");
            lines.Add("     完整 P8 基准需在【真实游戏 + 音频输出到 DualSense】环境下复测。");
        }

        return (ok, lines);
    }

    /// <summary>取日志文本中最后一个含 <paramref name="key"/> 的行的 <c>=</c> 之后的值。</summary>
    private static string? LastValue(string text, string key)
    {
        string? value = null;
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.Trim();
            int idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;

            int eq = line.IndexOf('=', idx);
            if (eq < 0) continue;

            value = line[(eq + 1)..].Trim();
        }
        return value;
    }

    /// <summary>在日志文本中找最后一个含 <paramref name="key"/> 的行，取 <c>=</c> 之后的值做判定。</summary>
    private static bool CheckMetric(
        List<string> lines, string text, string key, Func<string, bool> predicate, string label)
    {
        string? value = LastValue(text, key);
        bool hit = value is not null && predicate(value);
        lines.Add($"  {(hit ? "✓" : "✗")} {label,-22} = {value ?? "(日志中未找到该指标)"}");
        return hit;
    }

    /// <summary>数值串是否含任一非零分量（用于 <c>a,b,c,d,e,f</c> 形式的 APEX 参数）。</summary>
    private static bool HasNonZeroComponent(string value)
    {
        foreach (string part in value.Split(','))
        {
            if (double.TryParse(part.Trim(), out double d) && d != 0)
            {
                return true;
            }
        }
        return false;
    }

    // ══════════════════════ 工具 ══════════════════════

    private static string Sha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data));

    private static string Short(string hash)
        => hash.Length <= 16 ? hash : hash[..16] + "…";
}
