using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Playnite.SDK;
using Playnite.SDK.Data;

namespace DsInApex.Playnite
{
    /// <summary>GitHub 最新发布（只取需要的字段）。</summary>
    internal class GitHubReleaseInfo
    {
        [SerializationPropertyName("tag_name")]
        public string TagName { get; set; }

        [SerializationPropertyName("html_url")]
        public string HtmlUrl { get; set; }

        [SerializationPropertyName("body")]
        public string Body { get; set; }

        [SerializationPropertyName("assets")]
        public System.Collections.Generic.List<GitHubReleaseAsset> Assets { get; set; }
    }

    internal class GitHubReleaseAsset
    {
        [SerializationPropertyName("name")]
        public string Name { get; set; }

        [SerializationPropertyName("browser_download_url")]
        public string BrowserDownloadUrl { get; set; }
    }

    /// <summary>部署目录里 <c>version.json</c> 的清单结构（与主程序同源）。</summary>
    internal class DeployedVersionManifest
    {
        [SerializationPropertyName("version")]
        public string Version { get; set; }

        [SerializationPropertyName("releaseUrl")]
        public string ReleaseUrl { get; set; }

        [SerializationPropertyName("portableAsset")]
        public string PortableAsset { get; set; }

        [SerializationPropertyName("notes")]
        public string Notes { get; set; }
    }

    /// <summary>一次更新检查的结果。</summary>
    internal class UpdateCheckResult
    {
        internal bool IsUpdateAvailable { get; set; }
        internal string CurrentVersion { get; set; }
        internal string RemoteVersion { get; set; }
        internal string TagName { get; set; }
        internal string ReleaseNotes { get; set; }
        internal string ReleaseUrl { get; set; }
        internal string PortableAssetUrl { get; set; }
        internal string ErrorMessage { get; set; }
    }

    /// <summary>
    /// 更新检查（P9 重写）。
    ///
    /// <para><b>相对上游的三处必要改造：</b></para>
    /// <list type="number">
    /// <item><b>指向自有仓库。</b>上游硬编码 <c>ReynArts/ApexSenseBridge</c>，
    /// 且只认 <c>ApexSenseBridge-Setup.exe</c> 安装器资产 —— Ds in Apex 是衍生作品，
    /// 检查上游版本等于引导用户把 DIA 覆盖成官方版。改为 <c>JinAsukin/DsInApex</c>。</item>
    /// <item><b>两端点、先轻后重。</b>首选 jsDelivr 上的 <c>version.json</c>
    /// （与主程序 UpdateCheckerService 同一套通路，实测可达、无 API 限额）；
    /// 拿不到才退到 <c>api.github.com</c> 的 latest release。</item>
    /// <item><b>不发安装器。</b>便携版没有安装器可跑，上游那套
    /// <c>ReleaseSecurity</c>（校验 Inno 安装包的 Authenticode 签名与
    /// <c>ReynArts/ApexSenseBridge/releases/download/</c> 前缀）在 DIA 语境下<b>整体作废</b>，
    /// 已随迁移删除。这里只把版本号与发布页地址交给 UI，剩下让用户自己来。</item>
    /// </list>
    ///
    /// <para><b>本机版本从哪来：</b>读部署根目录的 <c>version.json</c>
    /// （便携包随包分发的那份），拿不到再退回插件程序集版本。这样「本机版本」
    /// 指的是 <b>DIA 的版本</b>，而不是插件自己的版本 —— 与主程序同源。</para>
    /// </summary>
    internal static class UpdateManager
    {
        private static readonly ILogger Logger = LogManager.GetLogger();

        /// <summary>DIA 自有仓库（公开），同时承载游戏库数据与版本信息。</summary>
        internal const string RepoOwner = "JinAsukin";
        internal const string RepoName = "DsInApex";

        /// <summary>发布页地址（浏览器打开的落点）。</summary>
        internal static string ReleasesPageUrl
        {
            get { return "https://github.com/" + RepoOwner + "/" + RepoName + "/releases"; }
        }

        private const string VersionManifestUrl =
            "https://cdn.jsdelivr.net/gh/" + RepoOwner + "/" + RepoName + "@main/version.json";

        private const string LatestReleaseApiUrl =
            "https://api.github.com/repos/" + RepoOwner + "/" + RepoName + "/releases/latest";

        private const string UserAgent = "DsInApex-Playnite/1.0";

        private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

        /// <summary>插件自身程序集版本（读部署清单失败时的兜底）。</summary>
        internal static string PluginVersion
        {
            get
            {
                try
                {
                    Version version = typeof(UpdateManager).Assembly.GetName().Version;
                    return version == null ? "0.0.0" : version.ToString(3);
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }

        /// <summary>读部署目录 <c>version.json</c> 里的 <c>version</c> 字段。</summary>
        internal static string ReadDeployedVersion(string deployRoot)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(deployRoot))
                {
                    return null;
                }

                string path = Path.Combine(deployRoot, "version.json");
                if (!File.Exists(path))
                {
                    return null;
                }

                DeployedVersionManifest manifest =
                    Serialization.FromJson<DeployedVersionManifest>(File.ReadAllText(path));
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                {
                    return null;
                }

                return manifest.Version.Trim().TrimStart('v', 'V');
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "读取部署目录 version.json 失败。");
                return null;
            }
        }

        /// <summary>
        /// 检查更新。<paramref name="currentVersion"/> 传空时用插件程序集版本兜底。
        /// 两个端点都不可达时返回带 <c>ErrorMessage</c> 的结果（不抛异常）。
        /// </summary>
        internal static async Task<UpdateCheckResult> CheckForUpdateAsync(string currentVersion)
        {
            string current = string.IsNullOrWhiteSpace(currentVersion)
                ? PluginVersion
                : currentVersion.Trim().TrimStart('v', 'V');

            UpdateCheckResult result = await TryManifestAsync(current).ConfigureAwait(false);
            if (result != null)
            {
                return result;
            }

            result = await TryLatestReleaseAsync(current).ConfigureAwait(false);
            if (result != null)
            {
                return result;
            }

            Logger.Info("更新检查：两个端点均不可达。");
            return new UpdateCheckResult
            {
                CurrentVersion = current,
                IsUpdateAvailable = false,
                ErrorMessage = Loc.Get("LOCDsInApex_ErrBothEndpointsUnreachable")
            };
        }

        /// <summary>语义化版本比较（缺失位按 0 处理；非法串一律判「不更新」，避免误报）。</summary>
        internal static bool IsNewer(string latest, string current)
        {
            if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
            {
                return false;
            }

            string[] latestParts = NormalizeVersion(latest).Split('.');
            string[] currentParts = NormalizeVersion(current).Split('.');
            int length = Math.Max(latestParts.Length, currentParts.Length);

            for (int i = 0; i < length; i++)
            {
                int l = ParsePart(latestParts, i);
                int c = ParsePart(currentParts, i);

                if (l > c) return true;
                if (l < c) return false;
            }

            return false;
        }

        /// <summary>取版本串第 <paramref name="index"/> 段；缺失或非数字一律当 0。</summary>
        private static int ParsePart(string[] parts, int index)
        {
            int value;
            if (index < parts.Length && int.TryParse(parts[index], out value))
            {
                return value;
            }
            return 0;
        }

        /// <summary>在系统浏览器里打开发布页。</summary>
        internal static void OpenReleasePage(string url)
        {
            string target = string.IsNullOrWhiteSpace(url) ? ReleasesPageUrl : url;
            try
            {
                Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "打开发布页失败：" + target);
            }
        }

        // ────────────────────────── 端点 1：version.json ──────────────────────────

        private static async Task<UpdateCheckResult> TryManifestAsync(string current)
        {
            string json = await GetStringAsync(VersionManifestUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                DeployedVersionManifest manifest = Serialization.FromJson<DeployedVersionManifest>(json);
                if (manifest == null || string.IsNullOrWhiteSpace(manifest.Version))
                {
                    return null;
                }

                string latest = manifest.Version.Trim().TrimStart('v', 'V');
                Logger.Info(string.Format("版本清单：本机 {0} / 远端 {1}", current, latest));

                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    RemoteVersion = latest,
                    TagName = "v" + latest,
                    IsUpdateAvailable = IsNewer(latest, current),
                    ReleaseNotes = manifest.Notes,
                    ReleaseUrl = string.IsNullOrWhiteSpace(manifest.ReleaseUrl)
                        ? ReleasesPageUrl
                        : manifest.ReleaseUrl,
                    PortableAssetUrl = manifest.PortableAsset
                };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "版本清单解析失败（继续尝试发布接口）。");
                return null;
            }
        }

        // ────────────────────────── 端点 2：GitHub latest release ──────────────────────────

        private static async Task<UpdateCheckResult> TryLatestReleaseAsync(string current)
        {
            string json = await GetStringAsync(LatestReleaseApiUrl).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return null;
            }

            try
            {
                GitHubReleaseInfo release = Serialization.FromJson<GitHubReleaseInfo>(json);

                // 仓库尚无发布时 GitHub 返回 {"message":"Not Found"} —— 正常空结果，不是错误
                if (release == null || string.IsNullOrWhiteSpace(release.TagName))
                {
                    Logger.Info("发布接口无版本信息（可能尚未发版）。");
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        IsUpdateAvailable = false,
                        ReleaseUrl = ReleasesPageUrl
                    };
                }

                string latest = ParseVersionFromTag(release.TagName);
                if (latest == null)
                {
                    return new UpdateCheckResult
                    {
                        CurrentVersion = current,
                        IsUpdateAvailable = false,
                        ErrorMessage = Loc.Format("LOCDsInApex_ErrBadTag", release.TagName)
                    };
                }

                string portableAsset = null;
                if (release.Assets != null)
                {
                    foreach (GitHubReleaseAsset asset in release.Assets)
                    {
                        // 便携包约定：资产名以 .zip 结尾（DsInApex-Portable-<ver>.zip）
                        if (!string.IsNullOrEmpty(asset.Name) &&
                            asset.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                        {
                            portableAsset = asset.BrowserDownloadUrl;
                            break;
                        }
                    }
                }

                Logger.Info(string.Format("发布接口：本机 {0} / 远端 {1}", current, latest));

                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    RemoteVersion = latest,
                    TagName = release.TagName,
                    IsUpdateAvailable = IsNewer(latest, current),
                    ReleaseNotes = release.Body,
                    ReleaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl) ? ReleasesPageUrl : release.HtmlUrl,
                    PortableAssetUrl = portableAsset
                };
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "发布接口解析失败。");
                return null;
            }
        }

        // ────────────────────────── 工具 ──────────────────────────

        private static async Task<string> GetStringAsync(string url)
        {
            try
            {
                using (var client = new HttpClient { Timeout = RequestTimeout })
                {
                    client.DefaultRequestHeaders.UserAgent.Add(
                        new ProductInfoHeaderValue("DsInApex-Playnite", PluginVersion));

                    using (HttpResponseMessage response = await client.GetAsync(url).ConfigureAwait(false))
                    {
                        if (!response.IsSuccessStatusCode)
                        {
                            Logger.Warn(string.Format("更新端点返回 {0}：{1}", (int)response.StatusCode, url));
                            return null;
                        }

                        return await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "更新端点请求失败：" + url);
                return null;
            }
        }

        /// <summary>从 tag 里剥出版本号（<c>v0.7.0</c> → <c>0.7.0</c>）。</summary>
        private static string ParseVersionFromTag(string tag)
        {
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            Match match = Regex.Match(tag.Trim(), @"(\d+(?:\.\d+)*)");
            return match.Success ? match.Groups[1].Value : null;
        }

        /// <summary>剥掉预发布后缀（<c>0.7.0-beta.1</c> → <c>0.7.0</c>），只比主干数字。</summary>
        private static string NormalizeVersion(string version)
        {
            int cut = version.IndexOfAny(new[] { '-', '+' });
            return cut < 0 ? version : version.Substring(0, cut);
        }
    }
}
