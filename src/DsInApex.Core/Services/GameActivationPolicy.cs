using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 判定「某游戏是否应当触发桥接」的策略。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/GameActivationPolicy.cs</c>（31 行），逻辑照搬。
/// </para>
///
/// <para>
/// 决策链：<b>先排除、后能力</b>。
/// 排除名单会同时比对 归一化名 / 标题 / 可执行标题 / 目录名 / 文件名 / Steam AppID ——
/// 只要命中任意一个就不激活。<b>不要"优化"掉这些维度</b>：用户排除游戏时用的是他看到的那个名字，
/// 而进程监控拿到的是另一套标识，多维度比对是唯一能对齐两者的办法。
/// </para>
/// </summary>
public static class GameActivationPolicy
{
    public static bool ShouldActivate(
        SupportedGame? game,
        TraySettings? settings,
        string? executableTitle,
        string? folderName,
        string? fileName)
    {
        if (game is null || settings is null) return false;

        if (settings.IsGameExcluded(game.Normalized) ||
            settings.IsGameExcluded(game.Title) ||
            settings.IsGameExcluded(executableTitle ?? string.Empty) ||
            settings.IsGameExcluded(folderName ?? string.Empty) ||
            settings.IsGameExcluded(fileName ?? string.Empty) ||
            (game.SteamAppIdVerified && game.SteamAppId > 0 &&
             settings.IsGameExcluded(game.SteamAppId.ToString())))
        {
            return false;
        }

        return (settings.TriggerOnAdaptiveTriggers && game.AdaptiveTriggers) ||
               (settings.TriggerOnHapticFeedback && game.HapticFeedback);
    }
}
