namespace DsInApex.Spike.Localization;

/// <summary>
/// 语言标识常量。与上游 tray_settings.json 的 Language 字段语义保持一致，
/// 新增 zh-CN（上游只有 en / fr / auto）。
/// </summary>
public static class Langs
{
    public const string Auto = "auto";
    public const string ZhCN = "zh-CN";
    public const string En = "en";
    public const string Fr = "fr";
}

/// <summary>
/// 文案字典。Spike 阶段只放代表性键位，用于验证热切换机制；
/// 全量 121 键的中英对照在 P1 落地。
/// </summary>
public static class Strings
{
    public static readonly IReadOnlyDictionary<string, string> ZhCN =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // ── 纯文本 ──
            ["Loc_AppName"]              = "Ds in Apex",
            ["Loc_AppSubtitle"]          = "DualSense \u2192 飞智 APEX 4 / APEX 5 桥接",
            ["Loc_Close"]                = "关闭",
            ["Loc_BtnClose"]             = "关闭",
            ["Loc_Hide"]                 = "隐藏",
            ["Loc_Cancel"]               = "取消",
            ["Loc_Warning"]              = "警告",

            // ── 托盘 ──
            ["Loc_TrayOpen"]             = "打开界面...",
            ["Loc_TrayLanguage"]         = "语言",
            ["Loc_TrayExit"]             = "退出",
            ["Loc_TrayStatusStandby"]    = "Ds in Apex：待机",

            // ── 状态卡 ──
            ["Loc_StatusBadgeStandby"]   = "● 待机",
            ["Loc_StatusBadgeActive"]    = "● 桥接已激活",
            ["Loc_NoActiveGame"]         = "无活动游戏",
            ["Loc_WaitingHint"]          = "等待兼容游戏启动...",
            ["Loc_BtnExcludeCurrent"]    = "排除此游戏",

            // ── 设置 ──
            ["Loc_EnableDetection"]      = "启用检测",
            ["Loc_EnableDetectionHint"]  = "兼容游戏启动时自动激活桥接",
            ["Loc_Language"]             = "语言",
            ["Loc_LanguageHint"]         = "界面显示语言",

            // ── 游戏库 ──
            ["Loc_SearchPlaceholder"]    = "搜索游戏...",
            ["Loc_TabAll"]               = "全部",
            ["Loc_TabTriggers"]          = "自适应扳机",
            ["Loc_TabHaptics"]           = "触觉反馈",
            ["Loc_TabExcluded"]          = "已排除",

            // ── 带参数格式化（验证 string.Format 路径） ──
            ["Loc_MsgExcluded"]          = "\u201c{0}\u201d 已从自动检测中排除。",
            ["Loc_NotificationGameProfile"] = "{0}\n配置档：{1}",
            ["Loc_GamesDisplayedPlural"] = "已显示 {0} 款游戏",
            ["Loc_UpdateUpToDate"]       = "Ds in Apex 已是最新版本（v{0}）。",
        };

    public static readonly IReadOnlyDictionary<string, string> En =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Loc_AppName"]              = "Ds in Apex",
            ["Loc_AppSubtitle"]          = "DualSense \u2192 Flydigi APEX 4 / APEX 5 Bridge",
            ["Loc_Close"]                = "Close",
            ["Loc_BtnClose"]             = "Close",
            ["Loc_Hide"]                 = "Hide",
            ["Loc_Cancel"]               = "Cancel",
            ["Loc_Warning"]              = "Warning",

            ["Loc_TrayOpen"]             = "Open Interface...",
            ["Loc_TrayLanguage"]         = "Language",
            ["Loc_TrayExit"]             = "Exit",
            ["Loc_TrayStatusStandby"]    = "Ds in Apex: Standby",

            ["Loc_StatusBadgeStandby"]   = "\u25cf Standby",
            ["Loc_StatusBadgeActive"]    = "\u25cf Bridge active",
            ["Loc_NoActiveGame"]         = "No active game",
            ["Loc_WaitingHint"]          = "Waiting for a compatible game...",
            ["Loc_BtnExcludeCurrent"]    = "Exclude this game",

            ["Loc_EnableDetection"]      = "Enable detection",
            ["Loc_EnableDetectionHint"]  = "Activates the bridge when a compatible game is launched",
            ["Loc_Language"]             = "Language",
            ["Loc_LanguageHint"]         = "User interface display language",

            ["Loc_SearchPlaceholder"]    = "Search a game...",
            ["Loc_TabAll"]               = "All",
            ["Loc_TabTriggers"]          = "Triggers",
            ["Loc_TabHaptics"]           = "Haptics",
            ["Loc_TabExcluded"]          = "Excluded",

            ["Loc_MsgExcluded"]          = "\u201c{0}\u201d has been excluded from automatic detection.",
            ["Loc_NotificationGameProfile"] = "{0}\nProfile: {1}",
            ["Loc_GamesDisplayedPlural"] = "{0} games displayed",
            ["Loc_UpdateUpToDate"]       = "Ds in Apex is up to date (version v{0}).",
        };
}
