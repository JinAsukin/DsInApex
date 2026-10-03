using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 官方支持游戏库：加载 + 索引 + 云同步 + 匹配。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/CloudGameListService.cs</c>（441 行）。
/// <b>索引语义、匹配评分、歧义处理全部照搬</b>；唯一变更是 JSON 解析由
/// <c>System.Web.Script.Serialization.JavaScriptSerializer</c>（.NET Framework 专有）
/// 改为 <c>System.Text.Json</c>（.NET 10）。
/// </para>
///
/// <para>
/// <b>数据来源（三档，优先级从高到低）：</b>
/// </para>
/// <list type="number">
/// <item>本地缓存 <c>%LOCALAPPDATA%\ApexSenseBridge\cache\supported_games.json</c>；</item>
/// <item>云同步（GitHub raw / jsDelivr，成功即回写缓存）；</item>
/// <item>程序内嵌资源 <c>Data/supported_games.json</c>（215 条，离线兜底）。</item>
/// </list>
///
/// <para>
/// <b>⚠️ 匹配评分里的 <c>minimumFragmentLength = 8</c> 是刻意设计</b>：
/// "Control" "Stray" "Haste" 这类短名是进程名/产品名的常见子串，模糊匹配会误伤。
/// 它们仍可经「精确标题 / Steam AppID / 精确可执行名」命中，只是不足以靠模糊匹配拉起全局桥接。
/// <b>不要"放宽"这个阈值</b>，否则会开始误识别无关进程。
/// </para>
/// </summary>
public sealed class CloudGameListService
{
    /// <summary>嵌入资源名 = <c>&lt;RootNamespace&gt;.&lt;LogicalName&gt;</c>，逻辑名在 csproj 中指定。</summary>
    private const string EmbeddedResourceName = "DsInApex.Core.Data.supported_games.json";

    private readonly object syncRoot = new();
    private readonly Dictionary<string, SupportedGame> gamesByNormalizedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, SupportedGame> gamesBySteamAppId = [];
    private readonly HashSet<int> ambiguousSteamAppIds = [];
    private readonly List<SupportedGame> allGames = [];
    private volatile Dictionary<string, SupportedGame> gamesByExecutableName =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>游戏库内容发生变更（云同步成功后触发，UI 应重新加载列表）。</summary>
    public event Action? GamesUpdated;

    public DateTime? LastUpdated { get; private set; }

    public int TotalGamesLoaded
    {
        get { lock (syncRoot) { return allGames.Count; } }
    }

    private static string LocalCachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApexSenseBridge", "cache", "supported_games.json");

    /// <summary>同步初始化：优先本地缓存，缺失则回落嵌入资源。启动时调用一次。</summary>
    public void Initialize()
    {
        try
        {
            string cachePath = LocalCachePath;
            if (File.Exists(cachePath))
            {
                string json = File.ReadAllText(cachePath, Encoding.UTF8);
                if (ParseAndLoadJson(json))
                {
                    LastUpdated = File.GetLastWriteTimeUtc(cachePath);
                    return;
                }
            }
        }
        catch
        {
            // 缓存损坏/不可读 → 落到嵌入资源
        }

        LoadEmbeddedDatabase();
    }

    /// <summary>从云端拉取最新游戏库。任一 endpoint 成功即返回 true 并回写缓存。</summary>
    public async Task<bool> FetchLatestFromCloudAsync()
    {
        // 数据源 = DIA 自有仓库（2026-10-03 从上游迁出，不再依赖上游仓库）：
        //   https://github.com/JinAsukin/DsInApex  →  data/supported_games.json
        //
        // ⚠️ 顺序有讲究：**jsDelivr 放在前面**。
        // 2026-10-03 实测：raw.githubusercontent.com 在国内**直连不可达**（curl 返回 000），
        // 而 cdn.jsdelivr.net 直连 200。把不可达的那个放最前，每次同步都要白等 10 秒超时。
        //
        // 两个地址都拿不到时会回落到本地缓存 / 嵌入资源（215 款），不影响可用性。
        string[] endpoints =
        [
            "https://cdn.jsdelivr.net/gh/JinAsukin/DsInApex@main/data/supported_games.json",
            "https://raw.githubusercontent.com/JinAsukin/DsInApex/main/data/supported_games.json",
        ];

        foreach (string url in endpoints)
        {
            try
            {
                using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                client.DefaultRequestHeaders.Add("User-Agent", "DsInApex/1.0");

                HttpResponseMessage response = await client.GetAsync(url);
                if (!response.IsSuccessStatusCode) continue;

                string json = await response.Content.ReadAsStringAsync();
                if (ParseAndLoadJson(json))
                {
                    LastUpdated = DateTime.UtcNow;
                    try
                    {
                        string cachePath = LocalCachePath;
                        string? dir = Path.GetDirectoryName(cachePath);
                        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);
                        File.WriteAllText(cachePath, json, Encoding.UTF8);
                    }
                    catch
                    {
                        // 回写失败不影响本次加载
                    }

                    GamesUpdated?.Invoke();
                    return true;
                }
            }
            catch
            {
                // 换下一个 endpoint
            }
        }
        return false;
    }

    /// <summary>模糊匹配：精确归一化名优先，否则按最长子串评分（含歧义判定）。</summary>
    public bool TryFindGame(string? candidateName, out SupportedGame? game)
    {
        game = null;
        if (string.IsNullOrWhiteSpace(candidateName)) return false;

        string normalized = Normalize(candidateName);
        if (string.IsNullOrEmpty(normalized)) return false;

        lock (syncRoot)
        {
            if (gamesByNormalizedName.TryGetValue(normalized, out game))
            {
                return true;
            }

            SupportedGame? bestMatch = null;
            int bestScore = 0;
            bool isAmbiguous = false;

            foreach (KeyValuePair<string, SupportedGame> kvp in gamesByNormalizedName)
            {
                int score = GetMatchScore(normalized, kvp.Key);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestMatch = kvp.Value;
                    isAmbiguous = false;
                }
                else if (score > 0 && score == bestScore && !ReferenceEquals(bestMatch, kvp.Value))
                {
                    isAmbiguous = true;
                }
            }

            if (bestMatch is not null && !isAmbiguous)
            {
                game = bestMatch;
                return true;
            }
        }
        return false;
    }

    /// <summary>精确匹配（仅归一化名）。</summary>
    public bool TryFindExactGame(string? candidateName, out SupportedGame? game)
    {
        game = null;
        string normalized = Normalize(candidateName ?? string.Empty);
        if (string.IsNullOrEmpty(normalized)) return false;

        lock (syncRoot)
        {
            return gamesByNormalizedName.TryGetValue(normalized, out game);
        }
    }

    /// <summary>按已核验的 Steam AppID 匹配。</summary>
    public bool TryFindBySteamAppId(int steamAppId, out SupportedGame? game)
    {
        game = null;
        if (steamAppId <= 0) return false;

        lock (syncRoot)
        {
            return gamesBySteamAppId.TryGetValue(steamAppId, out game);
        }
    }

    /// <summary>按可执行文件名精确匹配（检测热路径，无锁无 I/O）。</summary>
    public bool TryFindByExecutable(string? executablePathOrName, out SupportedGame? game)
    {
        game = null;
        string executableName = GetExecutableName(executablePathOrName);
        if (string.IsNullOrEmpty(executableName)) return false;

        // 字典在发布前已完整构建、其后不再变更，故读 volatile 快照无需加锁。
        var snapshot = gamesByExecutableName;
        return snapshot.TryGetValue(executableName, out game);
    }

    private static int GetMatchScore(string candidate, string gameName)
    {
        // 见类注释：短目录名是进程名常见子串，阈值低于 8 会误伤。
        const int minimumFragmentLength = 8;

        if (gameName.Length >= minimumFragmentLength && candidate.Contains(gameName))
        {
            return 1000 + gameName.Length;
        }

        if (candidate.Length >= 8 &&
            candidate.Length * 2 >= gameName.Length &&
            gameName.Contains(candidate))
        {
            return 500 + candidate.Length;
        }

        return 0;
    }

    public IReadOnlyList<SupportedGame> GetAllGames()
    {
        lock (syncRoot)
        {
            return allGames.ToArray();
        }
    }

    private bool ParseAndLoadJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return false;

        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return false;
            if (!root.TryGetProperty("games", out JsonElement gamesArray) ||
                gamesArray.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            var parsedGames = new List<SupportedGame>();
            var parsedByNormalizedName = new Dictionary<string, SupportedGame>(StringComparer.OrdinalIgnoreCase);
            var parsedBySteamAppId = new Dictionary<int, SupportedGame>();
            var parsedAmbiguousSteamAppIds = new HashSet<int>();
            var parsedByExecutableName = new Dictionary<string, SupportedGame>(StringComparer.OrdinalIgnoreCase);
            var ambiguousExecutableNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (JsonElement item in gamesArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object) continue;

                var g = new SupportedGame
                {
                    Title = GetString(item, "title"),
                    AdaptiveTriggers = GetBool(item, "adaptiveTriggers"),
                    HapticFeedback = GetBool(item, "hapticFeedback"),
                    Profile = GetStringOrDefault(item, "profile", "standard"),
                    IconUrl = GetString(item, "iconUrl"),
                    SteamAppId = GetInt(item, "steamAppId"),
                    SteamAppIdVerified = GetBool(item, "steamAppIdVerified"),
                };

                string providedNormalized = Normalize(GetString(item, "normalized"));
                g.Executables = g.SteamAppIdVerified ? ParseExecutables(item) : [];

                string titleNormalized = Normalize(g.Title);
                g.Normalized = string.Equals(providedNormalized, titleNormalized, StringComparison.Ordinal)
                    ? providedNormalized
                    : titleNormalized;

                if (string.IsNullOrWhiteSpace(g.Normalized)) continue;

                parsedByNormalizedName[g.Normalized] = g;

                if (g.SteamAppIdVerified && g.SteamAppId > 0 &&
                    !parsedAmbiguousSteamAppIds.Contains(g.SteamAppId))
                {
                    if (parsedBySteamAppId.ContainsKey(g.SteamAppId))
                    {
                        parsedBySteamAppId.Remove(g.SteamAppId);
                        parsedAmbiguousSteamAppIds.Add(g.SteamAppId);
                    }
                    else
                    {
                        parsedBySteamAppId[g.SteamAppId] = g;
                    }
                }

                IndexExecutables(g, parsedByExecutableName, ambiguousExecutableNames);
                parsedGames.Add(g);
            }

            parsedGames.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));

            lock (syncRoot)
            {
                gamesByNormalizedName.Clear();
                foreach (KeyValuePair<string, SupportedGame> entry in parsedByNormalizedName)
                    gamesByNormalizedName.Add(entry.Key, entry.Value);

                gamesBySteamAppId.Clear();
                foreach (KeyValuePair<int, SupportedGame> entry in parsedBySteamAppId)
                    gamesBySteamAppId.Add(entry.Key, entry.Value);

                ambiguousSteamAppIds.Clear();
                foreach (int appId in parsedAmbiguousSteamAppIds)
                    ambiguousSteamAppIds.Add(appId);

                allGames.Clear();
                allGames.AddRange(parsedGames);

                // 全部条目与冲突都校验完毕后才发布。
                gamesByExecutableName = parsedByExecutableName;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string[] ParseExecutables(JsonElement item)
    {
        if (!item.TryGetProperty("executables", out JsonElement executables) ||
            executables.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonElement value in executables.EnumerateArray())
        {
            if (value.ValueKind != JsonValueKind.String) continue;
            string executableName = GetExecutableName(value.GetString());
            if (!string.IsNullOrEmpty(executableName)) result.Add(executableName);
        }
        return result.OrderBy(value => value, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void IndexExecutables(
        SupportedGame game,
        IDictionary<string, SupportedGame> index,
        ISet<string> ambiguousNames)
    {
        foreach (string executableName in game.Executables)
        {
            if (ambiguousNames.Contains(executableName)) continue;

            if (index.TryGetValue(executableName, out SupportedGame? existing) &&
                !ReferenceEquals(existing, game))
            {
                index.Remove(executableName);
                ambiguousNames.Add(executableName);
            }
            else
            {
                index[executableName] = game;
            }
        }
    }

    private static string GetExecutableName(string? executablePathOrName)
    {
        if (string.IsNullOrWhiteSpace(executablePathOrName)) return string.Empty;

        try
        {
            string normalizedPath = executablePathOrName.Trim().Replace('/', '\\');
            string executableName = Path.GetFileName(normalizedPath);
            if (string.IsNullOrWhiteSpace(executableName) ||
                !executableName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                executableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return string.Empty;
            }
            return executableName;
        }
        catch
        {
            return string.Empty;
        }
    }

    private void LoadEmbeddedDatabase()
    {
        try
        {
            Assembly assembly = Assembly.GetExecutingAssembly();
            using Stream? stream = assembly.GetManifestResourceStream(EmbeddedResourceName);
            if (stream is null) return;

            using var reader = new StreamReader(stream, Encoding.UTF8);
            ParseAndLoadJson(reader.ReadToEnd());
        }
        catch
        {
            // 嵌入资源缺失不应让启动失败
        }
    }

    /// <summary>归一化：仅保留字母与数字并转小写。用于标题/进程名对齐。</summary>
    public static string Normalize(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var sb = new StringBuilder(input.Length);
        foreach (char ch in input)
        {
            if (char.IsLetterOrDigit(ch))
            {
                sb.Append(char.ToLowerInvariant(ch));
            }
        }
        return sb.ToString();
    }

    // ─────────────── JSON 读取辅助（宽容解析，与上游 JavaScriptSerializer 行为对齐） ───────────────

    private static string GetString(JsonElement item, string name)
        => item.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string GetStringOrDefault(JsonElement item, string name, string fallback)
    {
        string value = GetString(item, name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    private static bool GetBool(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return false;

        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => bool.TryParse(value.GetString(), out bool parsed) && parsed,
            JsonValueKind.Number => value.TryGetDouble(out double d) && d != 0,
            _ => false,
        };
    }

    private static int GetInt(JsonElement item, string name)
    {
        if (!item.TryGetProperty(name, out JsonElement value)) return 0;

        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out int n) ? n : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), out int parsed) ? parsed : 0,
            _ => 0,
        };
    }
}
