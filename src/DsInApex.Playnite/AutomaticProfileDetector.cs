using System;
using System.IO;
using System.Text;
using Playnite.SDK.Models;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 自动配置档识别：先查已验证游戏库，再退回名称/安装目录的别名匹配。
    /// 逻辑与上游一致，仅把日志用的 reason 改成中性英文。
    /// </summary>
    internal static class AutomaticProfileDetector
    {
        internal static bool TryDetect(Game game, out BridgeProfileType profileType, out string reason)
        {
            profileType = BridgeProfileType.StandardDualSense;
            reason = null;
            if (game == null)
            {
                return false;
            }

            if (SupportedGameCatalog.TryResolve(game, out profileType, out reason))
            {
                return true;
            }

            if (TryDetectValue(game.Name, out profileType))
            {
                reason = "Playnite name \"" + game.Name + "\"";
                return true;
            }

            string installFolder = GetInstallFolderName(game.InstallDirectory);
            if (TryDetectValue(installFolder, out profileType))
            {
                reason = "install folder \"" + installFolder + "\"";
                return true;
            }

            return false;
        }

        internal static bool TryDetectValue(string value, out BridgeProfileType profileType)
        {
            profileType = BridgeProfileType.StandardDualSense;
            string normalized = Normalize(value);
            if (string.IsNullOrEmpty(normalized))
            {
                return false;
            }

            // 原声带 / 设定集不算游戏本体
            if (normalized.Contains("soundtrack") || normalized.Contains("artbook"))
            {
                return false;
            }

            // 最具体的别名放前面；标点、空格与撇号变体已被 Normalize 去掉
            if (normalized.Contains("milesmorales"))
            {
                profileType = BridgeProfileType.MilesMorales;
                return true;
            }
            if (normalized.Contains("spiderman2"))
            {
                profileType = BridgeProfileType.SpiderMan2;
                return true;
            }
            if (normalized.Contains("ghostoftsushima"))
            {
                profileType = BridgeProfileType.GhostOfTsushima;
                return true;
            }
            if (normalized == "warframe" || normalized.StartsWith("warframe", StringComparison.Ordinal))
            {
                profileType = BridgeProfileType.Warframe;
                return true;
            }

            return false;
        }

        private static string Normalize(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return string.Empty;
            }

            var result = new StringBuilder(value.Length);
            foreach (char character in value)
            {
                if (char.IsLetterOrDigit(character))
                {
                    result.Append(char.ToLowerInvariant(character));
                }
            }
            return result.ToString();
        }

        private static string GetInstallFolderName(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return string.Empty;
            }

            try
            {
                return Path.GetFileName(installDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
