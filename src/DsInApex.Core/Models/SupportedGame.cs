namespace DsInApex.Core.Models;

/// <summary>
/// 官方支持列表中的一款游戏（来自 <c>supported_games.json</c>）。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Models/SupportedGame.cs</c>（31 行），字段与语义照搬。
/// 该模型是<b>引擎/官方数据契约</b>的一部分：JSON 键名（camelCase）不可改。
/// </para>
/// </summary>
public sealed class SupportedGame
{
    public string Title { get; set; } = string.Empty;

    /// <summary>归一化名（仅小写字母数字，见 <c>CloudGameListService.Normalize</c>）。</summary>
    public string Normalized { get; set; } = string.Empty;

    /// <summary>是否支持自适应扳机。</summary>
    public bool AdaptiveTriggers { get; set; }

    /// <summary>是否支持触觉反馈。</summary>
    public bool HapticFeedback { get; set; }

    /// <summary>推荐触摸板配置档（如 <c>standard</c> / <c>spider-man-2</c> …）。</summary>
    public string Profile { get; set; } = "standard";

    /// <summary>封面图 URL（Steam 商店图）。</summary>
    public string IconUrl { get; set; } = string.Empty;

    public int SteamAppId { get; set; }

    /// <summary>Steam AppID 是否已人工核验（未核验则不做可执行文件索引）。</summary>
    public bool SteamAppIdVerified { get; set; }

    /// <summary>
    /// 已知可执行文件名（仅小写比较安全的裸文件名）。
    /// 仅在 <see cref="SteamAppIdVerified"/> 为 true 时由数据源提供。
    /// </summary>
    public string[] Executables { get; set; } = [];

    public override string ToString() => $"{Title} (Profile: {Profile})";
}
