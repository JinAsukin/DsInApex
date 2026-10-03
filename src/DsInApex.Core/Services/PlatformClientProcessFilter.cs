namespace DsInApex.Core.Services;

/// <summary>
/// 启动器/平台客户端进程黑名单。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/PlatformClientProcessFilter.cs</c>（97 行），<b>名单照搬</b>。
/// </para>
///
/// <para>
/// <b>为什么必须有这份名单：</b>Steam / Epic / Battle.net 等启动器进程常年驻留且名字里带游戏关键词，
/// 若不排除，会被进程监控误判成「游戏已启动」而拉起桥接会话 —— 用户明明没在玩游戏，手柄却被接管。
/// 名单里的每一项都是踩坑换来的，<b>不要凭直觉删减</b>。
/// </para>
/// </summary>
internal static class PlatformClientProcessFilter
{
    private static readonly HashSet<string> ExcludedExecutables =
        new(StringComparer.OrdinalIgnoreCase)
        {
            // Steam
            "steam.exe",
            "steamservice.exe",
            "steamwebhelper.exe",
            "steamerrorreporter.exe",
            "steamerrorreporter64.exe",
            "steam_monitor.exe",
            "gameoverlayui.exe",

            // Battle.net
            "battle.net.exe",
            "battle.net launcher.exe",
            "battle.net helper.exe",
            "agent.exe",
            "blizzardbrowser.exe",
            "blizzarderror.exe",

            // Epic Games
            "epicgameslauncher.exe",
            "epicgamesupdater.exe",
            "epicwebhelper.exe",
            "eosoverlayrenderer-win32-shipping.exe",
            "eosoverlayrenderer-win64-shipping.exe",

            // EA / Origin
            "eadesktop.exe",
            "ealauncher.exe",
            "eabackgroundservice.exe",
            "ealocalhostsvc.exe",
            "eacefsubprocess.exe",
            "link2ea.exe",
            "origin.exe",
            "originwebhelperservice.exe",

            // Ubisoft Connect
            "ubisoftconnect.exe",
            "ubisoftconnectwebcore.exe",
            "ubisoftextension.exe",
            "ubisoftgamelauncher.exe",
            "uplay.exe",
            "uplaywebcore.exe",
            "upc.exe",

            // GOG Galaxy
            "galaxyclient.exe",
            "galaxyclient helper.exe",
            "galaxycommunication.exe",

            // Rockstar Games Launcher
            "rockstargameslauncher.exe",
            "launcherpatcher.exe",
            "socialclubhelper.exe",

            // Xbox app / Microsoft Gaming Services
            "xboxpcapp.exe",
            "gamingservices.exe",
            "gamingservicesnet.exe",

            // Other library clients
            "riotclientservices.exe",
            "riotclientux.exe",
            "riotclientuxrender.exe",
            "amazon games.exe",
            "itch.exe",
            "playnite.desktopapp.exe",
            "playnite.fullscreenapp.exe",
        };

    /// <summary>给定文件名或路径是否属于平台客户端（黑名单命中）。</summary>
    public static bool IsExcluded(string? fileNameOrPath)
    {
        if (string.IsNullOrWhiteSpace(fileNameOrPath)) return false;

        string fileName;
        try
        {
            fileName = Path.GetFileName(fileNameOrPath.Trim());
        }
        catch
        {
            fileName = fileNameOrPath.Trim();
        }

        return !string.IsNullOrWhiteSpace(fileName) && ExcludedExecutables.Contains(fileName);
    }
}
