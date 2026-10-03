using System.Security.Cryptography;
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

    // ══════════════════════ 工具 ══════════════════════

    private static string Sha256(byte[] data)
        => Convert.ToHexString(SHA256.HashData(data));

    private static string Short(string hash)
        => hash.Length <= 16 ? hash : hash[..16] + "…";
}
