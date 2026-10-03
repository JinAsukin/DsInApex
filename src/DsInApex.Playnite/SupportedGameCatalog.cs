using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Playnite.SDK.Data;
using Playnite.SDK.Models;

namespace DsInApex.Playnite
{
    /// <summary>
    /// 已验证游戏库（215 条，来自仓库根 <c>data/supported_games.json</c>，
    /// 与主程序 <c>CloudGameListService</c> 同源）。
    ///
    /// <para>
    /// <b>P9 改造点：</b>上游用 <c>JavaScriptSerializer</c> 手工拆
    /// <c>Dictionary&lt;string, object&gt;</c>（拖进 <c>System.Web.Extensions</c> 依赖）；
    /// 这里改用 Playnite 自带的 <see cref="Serialization.FromJson{T}"/> 反序列化成强类型。
    /// 数据格式没变（<c>title</c> / <c>profile</c> / <c>steamAppId</c> / <c>steamAppIdVerified</c>），
    /// 所以与主程序读同一份 JSON 依然成立。
    /// </para>
    /// </summary>
    internal static class SupportedGameCatalog
    {
        /// <summary>嵌入资源名 —— 必须与 csproj 里 <c>LogicalName</c> 逐字一致。</summary>
        internal const string CatalogResourceName = "DsInApex.Playnite.supported_games.json";

        private static readonly object SyncRoot = new object();
        private static Dictionary<string, Entry> byName;
        private static Dictionary<int, Entry> bySteamAppId;

        /// <summary>最后一次加载的说明（条目数 / 异常），供诊断展示。</summary>
        internal static string LoadReport { get; private set; }

        internal static bool TryResolve(Game game, out BridgeProfileType profileType, out string reason)
        {
            profileType = BridgeProfileType.StandardDualSense;
            reason = null;
            if (game == null)
            {
                return false;
            }

            EnsureLoaded();

            Entry entry;
            string normalizedName = Normalize(game.Name);
            if (!string.IsNullOrEmpty(normalizedName) && byName.TryGetValue(normalizedName, out entry))
            {
                profileType = ParseProfile(entry.Profile);
                reason = "verified catalogue (\"" + entry.Title + "\")";
                return true;
            }

            int steamAppId;
            if (game.Source != null &&
                string.Equals(game.Source.Name, "Steam", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(game.GameId) &&
                int.TryParse(game.GameId, out steamAppId) &&
                bySteamAppId.TryGetValue(steamAppId, out entry))
            {
                profileType = ParseProfile(entry.Profile);
                reason = "verified Steam AppID " + steamAppId;
                return true;
            }

            string folder = GetInstallFolderName(game.InstallDirectory);
            if (!string.IsNullOrEmpty(folder) && byName.TryGetValue(folder, out entry))
            {
                profileType = ParseProfile(entry.Profile);
                reason = "recognized folder (\"" + entry.Title + "\")";
                return true;
            }

            return false;
        }

        private static void EnsureLoaded()
        {
            if (byName != null)
            {
                return;
            }

            lock (SyncRoot)
            {
                if (byName != null)
                {
                    return;
                }

                var names = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
                var steamIds = new Dictionary<int, Entry>();
                string report;

                try
                {
                    Assembly assembly = Assembly.GetExecutingAssembly();
                    using (Stream stream = assembly.GetManifestResourceStream(CatalogResourceName))
                    {
                        if (stream == null)
                        {
                            report = "嵌入资源缺失：" + CatalogResourceName;
                        }
                        else
                        {
                            string json;
                            using (var reader = new StreamReader(stream, Encoding.UTF8))
                            {
                                json = reader.ReadToEnd();
                            }

                            CatalogRoot root = Serialization.FromJson<CatalogRoot>(json);
                            var games = root == null ? null : root.Games;
                            if (games != null)
                            {
                                foreach (Entry entry in games)
                                {
                                    if (entry == null || string.IsNullOrWhiteSpace(entry.Title))
                                    {
                                        continue;
                                    }
                                    names[Normalize(entry.Title)] = entry;
                                    if (entry.SteamAppIdVerified && entry.SteamAppId > 0)
                                    {
                                        steamIds[entry.SteamAppId] = entry;
                                    }
                                }
                            }
                            report = string.Format("已加载 {0} 条（Steam 已验证 {1} 条）", names.Count, steamIds.Count);
                        }
                    }
                }
                catch (Exception ex)
                {
                    // 小体量的内置名称识别仍然可用，不至于因为一份坏 JSON 就整体失效
                    report = "加载失败：" + ex.Message;
                }

                byName = names;
                bySteamAppId = steamIds;
                LoadReport = report;
            }
        }

        private static BridgeProfileType ParseProfile(string profile)
        {
            switch ((profile ?? string.Empty).Trim().ToLowerInvariant())
            {
                case "spider-man-2": return BridgeProfileType.SpiderMan2;
                case "miles-morales": return BridgeProfileType.MilesMorales;
                case "ghost-of-tsushima": return BridgeProfileType.GhostOfTsushima;
                case "warframe": return BridgeProfileType.Warframe;
                default: return BridgeProfileType.StandardDualSense;
            }
        }

        private static string GetInstallFolderName(string installDirectory)
        {
            if (string.IsNullOrWhiteSpace(installDirectory))
            {
                return string.Empty;
            }

            try
            {
                return Normalize(Path.GetFileName(installDirectory.TrimEnd(
                    Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)));
            }
            catch
            {
                return string.Empty;
            }
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

        private sealed class CatalogRoot
        {
            [SerializationPropertyName("totalGames")]
            public int TotalGames { get; set; }

            [SerializationPropertyName("games")]
            public List<Entry> Games { get; set; }
        }

        private sealed class Entry
        {
            [SerializationPropertyName("title")]
            public string Title { get; set; }

            [SerializationPropertyName("profile")]
            public string Profile { get; set; }

            [SerializationPropertyName("steamAppId")]
            public int SteamAppId { get; set; }

            [SerializationPropertyName("steamAppIdVerified")]
            public bool SteamAppIdVerified { get; set; }
        }
    }
}
