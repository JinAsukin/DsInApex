using System.Text.Json;
using System.Text.Json.Serialization;
using DsInApex.Core.Logging;

namespace DsInApex.Core.Models;

/// <summary>
/// 设置模型。
///
/// ⚠️ 两条兼容性红线（不可改）：
/// 1. **文件位置与上游完全一致**：<c>%LOCALAPPDATA%\ApexSenseBridge\tray_settings.json</c>
///    —— 用户可以无缝从官方版升级，设置不丢；
/// 2. **字段名保持原样**（JSON 键名不动），旧文件可直接反序列化。
///
/// 上游用 <c>System.Web.Script.Serialization.JavaScriptSerializer</c>（.NET Framework 专有），
/// DIA 迁移到 <c>System.Text.Json</c>。两者默认都是 PascalCase 键名，格式兼容。
/// </summary>
public sealed class TraySettings
{
    private const string LogFileName = "dsinapex_settings.log";

    // ═══════════════ 上游既有字段（键名不可改） ═══════════════

    public bool AutoDetectGames { get; set; } = true;

    public bool TriggerOnAdaptiveTriggers { get; set; } = true;

    public bool TriggerOnHapticFeedback { get; set; } = true;

    public bool EnableNotifications { get; set; } = true;

    public bool EnableRumble { get; set; } = true;

    public int HapticThresholdPercent { get; set; } = 12;

    public int InitializationTimeoutSeconds { get; set; } = 20;

    /// <summary>强制配置档；"none" 表示不强制。</summary>
    public string ForcedProfile { get; set; } = "none";

    /// <summary>
    /// 界面语言。取值：<c>auto</c> / <c>zh-CN</c> / <c>en</c>；
    /// 旧版可能是 <c>fr</c>，由 LocalizationService 归一化时降级为英文。
    /// </summary>
    public string Language { get; set; } = "auto";

    public List<string> ExcludedGames { get; set; } = [];

    /// <summary>游戏 → APEX 板载配置档（1–4）。</summary>
    public Dictionary<string, int> ApexProfileSlots { get; set; } = [];

    // ═══════════════ DIA 新增字段（旧文件缺失时取默认值，不影响反序列化） ═══════════════

    /// <summary>主题策略：<c>system</c> / <c>light</c> / <c>dark</c>（P1-7）。</summary>
    public string Theme { get; set; } = "system";

    /// <summary>
    /// 关闭窗口时隐藏到托盘而不是退出（P6）。
    ///
    /// <para>
    /// 默认 <c>true</c>：桥接需要在后台持续工作，点 X 就把虚拟 DualSense 一起带走
    /// 是上游 WPF 版从未有过的行为，不该在重构版里悄悄改掉。
    /// </para>
    /// </summary>
    public bool CloseWindowToTray { get; set; } = true;

    /// <summary>启动时静默检查更新（P6）。</summary>
    public bool CheckUpdatesOnStartup { get; set; } = true;

    // ⚠️ 刻意的设计决定：**开机自启状态不存这里**。
    //    注册表 Run 键才是唯一真源 —— 若再在 json 里存一份，用户手工删掉注册表项后
    //    设置页会显示"已开启"而实际没开，这种双真值是最难查的一类 bug。

    // ═══════════════ 持久化 ═══════════════

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>设置文件绝对路径（与上游同一位置）。</summary>
    [JsonIgnore]
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApexSenseBridge",
        "tray_settings.json");

    /// <summary>
    /// 载入设置。任何异常都回落到默认值 —— 设置损坏不该阻止程序启动。
    /// </summary>
    public static TraySettings Load()
    {
        try
        {
            string path = FilePath;
            if (!File.Exists(path))
            {
                AppLog.Info(LogFileName, "未找到设置文件，使用默认值");
                return new TraySettings();
            }

            string json = File.ReadAllText(path);
            TraySettings? loaded = JsonSerializer.Deserialize<TraySettings>(json, JsonOptions);
            if (loaded is null)
            {
                return new TraySettings();
            }

            // 集合字段兜底（旧文件可能显式为 null）
            loaded.ExcludedGames ??= [];
            loaded.ApexProfileSlots ??= [];

            AppLog.Info(LogFileName, $"设置已载入：language={loaded.Language} theme={loaded.Theme}");
            return loaded;
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"设置载入失败，回落默认值：{AppLog.Describe(ex)}");
            return new TraySettings();
        }
    }

    /// <summary>保存设置。失败只记日志，不抛出。</summary>
    public void Save()
    {
        try
        {
            string path = FilePath;
            string? dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"设置保存失败：{AppLog.Describe(ex)}");
        }
    }

    // ═══════════════ 业务辅助（迁移自上游，P3 游戏库会用） ═══════════════

    public bool IsGameExcluded(string normalizedOrTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedOrTitle) || ExcludedGames is null)
        {
            return false;
        }

        foreach (string item in ExcludedGames)
        {
            if (string.Equals(item, normalizedOrTitle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    public void SetGameExcluded(string normalizedOrTitle, bool excluded)
    {
        if (string.IsNullOrWhiteSpace(normalizedOrTitle)) return;
        ExcludedGames ??= [];

        for (int i = ExcludedGames.Count - 1; i >= 0; i--)
        {
            if (string.Equals(ExcludedGames[i], normalizedOrTitle, StringComparison.OrdinalIgnoreCase))
            {
                if (!excluded)
                {
                    ExcludedGames.RemoveAt(i);
                }
                else
                {
                    return;   // 已存在且仍要排除，无需变动
                }
            }
        }

        if (excluded)
        {
            ExcludedGames.Add(normalizedOrTitle);
        }
    }

    public int GetApexProfileSlot(string normalizedOrTitle)
    {
        if (string.IsNullOrWhiteSpace(normalizedOrTitle) || ApexProfileSlots is null)
        {
            return 0;
        }

        foreach (KeyValuePair<string, int> item in ApexProfileSlots)
        {
            if (string.Equals(item.Key, normalizedOrTitle, StringComparison.OrdinalIgnoreCase))
            {
                return item.Value is >= 1 and <= 4 ? item.Value : 0;
            }
        }
        return 0;
    }

    public void SetApexProfileSlot(string normalizedOrTitle, int slot)
    {
        if (string.IsNullOrWhiteSpace(normalizedOrTitle)) return;
        if (slot is < 0 or > 4)
        {
            throw new ArgumentOutOfRangeException(nameof(slot), "APEX 配置档必须在 0–4 之间。");
        }

        ApexProfileSlots ??= [];

        string? existingKey = null;
        foreach (string key in ApexProfileSlots.Keys)
        {
            if (string.Equals(key, normalizedOrTitle, StringComparison.OrdinalIgnoreCase))
            {
                existingKey = key;
                break;
            }
        }

        if (existingKey is not null)
        {
            ApexProfileSlots.Remove(existingKey);
        }

        if (slot != 0)
        {
            ApexProfileSlots[normalizedOrTitle.Trim()] = slot;
        }
    }
}
