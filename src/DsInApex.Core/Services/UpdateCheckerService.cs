using System.Diagnostics;
using System.Text.Json;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 更新检查（P6，迁移自上游 <c>UpdateCheckerService</c> 并做了三处必要改造）。
///
/// <para>
/// <b>改造 1 · 指向自有仓库。</b>
/// 上游硬编码 <c>ReynArts/ApexSenseBridge</c>；DIA 是衍生作品，
/// 检查上游版本等于引导用户把 DIA 覆盖成官方版。改为 <c>JinAsukin/DsInApex</c>。
/// </para>
///
/// <para>
/// <b>改造 2 · 两端点、先轻后重。</b>
/// 首选 jsDelivr 上的 <c>version.json</c>（与游戏库同一套通路，实测可达、无 API 限额、
/// 不需要 token）；拿不到才退到 <c>api.github.com</c> 的 latest release
/// （能顺带拿到发布说明与便携包直链）。
/// 两端点都失败时<b>安静地</b>报告失败，不弹窗 —— 一个衍生工具的启动流程
/// 不该因为 GitHub 抽风就打断用户。
/// </para>
///
/// <para>
/// <b>改造 3 · 不发安装器。</b>
/// 上游会下载并执行官方安装器。便携版没有安装器可跑，且 `UseShellExecute` 执行
/// 下载来的 exe 在无签名的衍生项目里是十足的坏味道。
/// 这里只负责把版本号与发布页地址交给 UI，剩下让用户自己来。
/// </para>
/// </summary>
public sealed class UpdateCheckerService
{
    private const string LogFileName = "dsinapex_update.log";

    /// <summary>DIA 自有仓库（公开），同时承载游戏库数据与版本信息。</summary>
    public const string RepoOwner = "JinAsukin";

    public const string RepoName = "DsInApex";

    /// <summary>发布页地址（浏览器打开的落点）。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases";

    /// <summary>首选端点：jsDelivr 上的版本清单。</summary>
    private const string VersionManifestUrl =
        $"https://cdn.jsdelivr.net/gh/{RepoOwner}/{RepoName}@main/version.json";

    /// <summary>次选端点：GitHub 最新发布。</summary>
    private const string LatestReleaseApiUrl =
        $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private const string UserAgent = "DsInApex/1.0";

    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>本机当前版本（读不到则回落 <c>0.0.0</c>）。</summary>
    public string CurrentVersion { get; }

    public UpdateCheckerService()
    {
        CurrentVersion = ReadCurrentVersion();
    }

    /// <summary>发现新版本时触发（静默检查路径用；UI 层接这个做托盘气泡）。</summary>
    public event Action<UpdateInfo>? UpdateAvailable;

    /// <summary>
    /// 检查更新。
    /// </summary>
    /// <param name="silent">
    /// true = 启动时的静默检查：失败不记警告、不打扰用户；
    /// false = 用户主动点击：失败要在日志里留明确原因，供界面提示。
    /// </param>
    public async Task<UpdateInfo> CheckAsync(bool silent, CancellationToken cancellationToken = default)
    {
        var baseResult = new UpdateInfo
        {
            CheckSucceeded = false,
            CurrentVersion = CurrentVersion,
            HasUpdate = false,
        };

        // ── 端点 1：version.json（轻量清单） ──
        UpdateInfo? fromManifest = await TryVersionManifestAsync(silent, cancellationToken).ConfigureAwait(false);
        if (fromManifest is not null)
        {
            RaiseIfUpdate(fromManifest);
            return fromManifest;
        }

        // ── 端点 2：GitHub latest release ──
        UpdateInfo? fromRelease = await TryLatestReleaseAsync(silent, cancellationToken).ConfigureAwait(false);
        if (fromRelease is not null)
        {
            RaiseIfUpdate(fromRelease);
            return fromRelease;
        }

        AppLog.Info(LogFileName, silent
            ? "更新检查：两个端点均不可达（静默模式，不打扰用户）"
            : "更新检查：两个端点均不可达");

        return baseResult with
        {
            Error = "两个更新端点均不可达（jsDelivr 清单 / GitHub 发布页）。",
        };
    }

    private void RaiseIfUpdate(UpdateInfo info)
    {
        if (!info.HasUpdate)
        {
            return;
        }

        try
        {
            UpdateAvailable?.Invoke(info);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"更新事件订阅方抛错：{AppLog.Describe(ex)}");
        }
    }

    // ────────────────────────── 端点 1 ──────────────────────────

    private async Task<UpdateInfo?> TryVersionManifestAsync(bool silent, CancellationToken cancellationToken)
    {
        string? json = await GetStringAsync(VersionManifestUrl, silent, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? version = root.TryGetProperty("version", out JsonElement v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(version))
            {
                return null;
            }

            string? releaseUrl = root.TryGetProperty("releaseUrl", out JsonElement u) ? u.GetString() : null;
            string? notes = root.TryGetProperty("notes", out JsonElement n) ? n.GetString() : null;
            string? asset = root.TryGetProperty("portableAsset", out JsonElement a) ? a.GetString() : null;

            string latest = version.Trim().TrimStart('v', 'V');
            AppLog.Info(LogFileName, $"版本清单：本机 {CurrentVersion} / 远端 {latest}");

            return new UpdateInfo
            {
                CheckSucceeded = true,
                CurrentVersion = CurrentVersion,
                LatestVersion = latest,
                HasUpdate = IsNewer(latest, CurrentVersion),
                ReleaseNotes = notes,
                ReleaseUrl = string.IsNullOrWhiteSpace(releaseUrl) ? ReleasesPageUrl : releaseUrl,
                PortableAssetUrl = asset,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"版本清单解析失败（继续尝试发布接口）：{AppLog.Describe(ex)}");
            return null;
        }
    }

    // ────────────────────────── 端点 2 ──────────────────────────

    private async Task<UpdateInfo?> TryLatestReleaseAsync(bool silent, CancellationToken cancellationToken)
    {
        string? json = await GetStringAsync(LatestReleaseApiUrl, silent, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            // 仓库尚无发布时，GitHub 返回 {"message":"Not Found"} —— 这是正常空结果，不是错误
            if (!root.TryGetProperty("tag_name", out JsonElement tagElement))
            {
                string? message = root.TryGetProperty("message", out JsonElement m) ? m.GetString() : null;
                AppLog.Info(LogFileName, $"发布接口无版本信息（{(string.IsNullOrWhiteSpace(message) ? "未发版" : message)}）");

                return new UpdateInfo
                {
                    CheckSucceeded = true,
                    CurrentVersion = CurrentVersion,
                    HasUpdate = false,
                    ReleaseUrl = ReleasesPageUrl,
                };
            }

            string latest = (tagElement.GetString() ?? string.Empty).Trim().TrimStart('v', 'V');
            string? htmlUrl = root.TryGetProperty("html_url", out JsonElement h) ? h.GetString() : null;
            string? body = root.TryGetProperty("body", out JsonElement b) ? b.GetString() : null;

            string? assetUrl = null;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string name = asset.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString() ?? string.Empty
                        : string.Empty;

                    // 便携包约定：以 .zip 结尾（发布资产命名 DsInApex-Portable-<ver>.zip）
                    if (name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    {
                        assetUrl = asset.TryGetProperty("browser_download_url", out JsonElement urlElement)
                            ? urlElement.GetString()
                            : null;
                        break;
                    }
                }
            }

            AppLog.Info(LogFileName, $"发布接口：本机 {CurrentVersion} / 远端 {(string.IsNullOrEmpty(latest) ? "(空)" : latest)}");

            return new UpdateInfo
            {
                CheckSucceeded = true,
                CurrentVersion = CurrentVersion,
                LatestVersion = string.IsNullOrEmpty(latest) ? null : latest,
                HasUpdate = !string.IsNullOrEmpty(latest) && IsNewer(latest, CurrentVersion),
                ReleaseNotes = body,
                ReleaseUrl = string.IsNullOrWhiteSpace(htmlUrl) ? ReleasesPageUrl : htmlUrl,
                PortableAssetUrl = assetUrl,
            };
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"发布接口解析失败：{AppLog.Describe(ex)}");
            return null;
        }
    }

    // ────────────────────────── 工具 ──────────────────────────

    private static async Task<string?> GetStringAsync(string url, bool silent, CancellationToken cancellationToken)
    {
        try
        {
            using var client = new HttpClient { Timeout = RequestTimeout };
            client.DefaultRequestHeaders.Add("User-Agent", UserAgent);

            using HttpResponseMessage response = await client
                .GetAsync(url, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                if (!silent)
                {
                    AppLog.Warn(LogFileName, $"端点返回 {(int)response.StatusCode}：{url}");
                }
                return null;
            }

            return await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            if (!silent)
            {
                AppLog.Warn(LogFileName, $"端点请求失败：{url} → {AppLog.Describe(ex)}");
            }
            return null;
        }
    }

    /// <summary>语义化版本比较（缺失位按 0 处理；非法串一律判「不更新」，避免误报）。</summary>
    public static bool IsNewer(string? latest, string? current)
    {
        if (string.IsNullOrWhiteSpace(latest) || string.IsNullOrWhiteSpace(current))
        {
            return false;
        }

        string[] latestParts = Normalize(latest).Split('.');
        string[] currentParts = Normalize(current).Split('.');
        int length = Math.Max(latestParts.Length, currentParts.Length);

        for (int i = 0; i < length; i++)
        {
            int l = i < latestParts.Length && int.TryParse(latestParts[i], out int lv) ? lv : 0;
            int c = i < currentParts.Length && int.TryParse(currentParts[i], out int cv) ? cv : 0;

            if (l > c) return true;
            if (l < c) return false;
        }

        return false;
    }

    /// <summary>剥掉预发布后缀（<c>0.7.0-beta.1</c> → <c>0.7.0</c>），只比主干数字。</summary>
    private static string Normalize(string version)
    {
        int dash = version.IndexOfAny(['-', '+']);
        return dash < 0 ? version : version[..dash];
    }

    private static string ReadCurrentVersion()
    {
        try
        {
            string? exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe) && File.Exists(exe))
            {
                // ⚠️ FileInfo 没有 VersionInfo（那是 PowerShell 对象的属性）→ 必须用 FileVersionInfo
                FileVersionInfo info = FileVersionInfo.GetVersionInfo(exe);
                string text = info.ProductVersion ?? info.FileVersion ?? string.Empty;
                text = text.Trim();
                if (!string.IsNullOrEmpty(text))
                {
                    return text.Split('+')[0];
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"读取本机版本失败：{AppLog.Describe(ex)}");
        }

        return "0.0.0";
    }
}
