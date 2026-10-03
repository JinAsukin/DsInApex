using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 「可执行文件学习」服务：把进程监控识别到的 (可执行文件 → 游戏) 关系沉淀为持久绑定。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/ExecutableLearningService.cs</c>（876 行）。
/// <b>并发模型、稳定性窗口、容量裁剪、导出格式全部照搬</b>；唯一变更是 JSON 读写由
/// <c>JavaScriptSerializer</c> 改为 <c>System.Text.Json</c>。
/// </para>
///
/// <para>
/// <b>设计要点（勿"简化"）：</b>
/// </para>
/// <list type="bullet">
/// <item><b>稳定性窗口（默认 30 秒）</b>：进程刚出现时不立即记绑定，而是挂一个延迟校验；
/// 30 秒后若该进程仍是「同一个进程 + 同一路径」才落库。避免把一闪而过的误报（如崩溃重启）
/// 写成永久绑定。</item>
/// <item><b>不可变快照 + 读无锁</b>：<c>snapshot</c> 每次变更都整体替换（写锁、读无锁）。
/// 检测热路径（<see cref="TryResolve"/>）因此不需要任何锁。</item>
/// <item><b>容量上限 2048</b>：超限按 <c>LastSeenUtc</c> 淘汰最旧的。</item>
/// <item><b>落盘原子性</b>：先写 <c>.tmp</c> 再 <c>File.Replace</c>，避免断电写坏缓存。</item>
/// </list>
///
/// <para>
/// <b>⚠️ 跨应用共享契约：</b>文件名 <c>learned_executables.json</c> 与 JSON schema
/// （<c>version:1</c> + <c>bindings[]</c> + 各字段键名）<b>不可改</b> ——
/// DIA 与官方版共用同一份缓存，改了会互相读不懂。
/// </para>
/// </summary>
public sealed class ExecutableLearningService : IDisposable
{
    private const int SchemaVersion = 1;
    private const int MaximumBindings = 2048;

    /// <summary>JSON 字符串转义策略：与上游 JavaScriptSerializer 一致，不转义非 ASCII / HTML 字符。</summary>
    private static readonly JsonSerializerOptions StringJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string storagePath;
    private readonly string logDirectory;
    private readonly TimeSpan stabilityDelay;
    private readonly object mutationLock = new();
    private readonly object pendingLock = new();
    private readonly object persistenceLock = new();

    private Dictionary<string, LearnedExecutableBinding> snapshot =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, PendingObservation> pendingObservations = [];
    private int initializationStarted;
    private int persistenceDirty;
    private volatile bool isDisposed;

    /// <summary>绑定集合发生「实质」变更（验证通过 / 删除）。</summary>
    public event Action? BindingsChanged;

    /// <summary>状态变更（含待验证项增减）。UI 应刷新计数。</summary>
    public event Action? StateChanged;

    public ExecutableLearningService()
        : this(DefaultStoragePath, TimeSpan.FromSeconds(30))
    {
    }

    public ExecutableLearningService(string storagePath, TimeSpan stabilityDelay)
    {
        if (string.IsNullOrWhiteSpace(storagePath))
        {
            throw new ArgumentException("必须提供存储路径。", nameof(storagePath));
        }
        if (stabilityDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(stabilityDelay));
        }

        this.storagePath = storagePath;
        string storageDirectory = Path.GetDirectoryName(Path.GetFullPath(storagePath)) ?? string.Empty;
        logDirectory = Path.Combine(storageDirectory, "logs");
        this.stabilityDelay = stabilityDelay;
    }

    private static string DefaultStoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ApexSenseBridge", "learned_executables.json");

    public int Count => Volatile.Read(ref snapshot).Count;

    public int PendingCount
    {
        get { lock (pendingLock) { return pendingObservations.Count; } }
    }

    /// <summary>异步从磁盘载入缓存（幂等，仅首次生效）。</summary>
    public void InitializeAsync()
    {
        if (Interlocked.Exchange(ref initializationStarted, 1) != 0) return;

        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                LoadFromDisk();
            }
            catch (Exception ex)
            {
                Log("learning load failed: " + ex.Message);
            }
        });
    }

    /// <summary>按已学路径解析出游戏（检测热路径）。</summary>
    public bool TryResolve(
        string? executablePath,
        CloudGameListService? gameListService,
        out SupportedGame? game)
    {
        game = null;
        if (string.IsNullOrWhiteSpace(executablePath) || gameListService is null) return false;

        string normalizedPath = NormalizeStoredPath(executablePath);
        if (string.IsNullOrWhiteSpace(normalizedPath)) return false;

        var current = Volatile.Read(ref snapshot);
        if (!current.TryGetValue(normalizedPath, out LearnedExecutableBinding? binding) || binding is null)
        {
            return false;
        }

        if (binding.SteamAppId > 0 &&
            gameListService.TryFindBySteamAppId(binding.SteamAppId, out game))
        {
            return true;
        }

        return gameListService.TryFindExactGame(binding.GameNormalized, out game);
    }

    /// <summary>取当前全部绑定（已克隆，按游戏名/文件名/路径排序）。</summary>
    public IReadOnlyList<LearnedExecutableBinding> GetBindings()
    {
        var current = Volatile.Read(ref snapshot);
        return current.Values
            .Select(x => x.Clone())
            .OrderBy(x => x.GameTitle, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Executable, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>开始观察一个进程；<paramref name="stabilityDelay"/> 后回调判定其是否仍稳定。</summary>
    public void BeginObservation(
        uint processId,
        string? executablePath,
        SupportedGame? game,
        string? detectionMethod,
        Func<uint, string, bool>? isStillActive)
    {
        if (isDisposed || processId == 0 || game is null || isStillActive is null) return;
        if (string.IsNullOrWhiteSpace(executablePath) || !Path.IsPathRooted(executablePath))
        {
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "learning skipped: PID {0} has no absolute executable path ('{1}')",
                processId,
                executablePath ?? string.Empty));
            return;
        }

        string normalizedPath = NormalizeStoredPath(executablePath);
        if (string.IsNullOrWhiteSpace(normalizedPath))
        {
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "learning skipped: PID {0} executable path could not be normalized",
                processId));
            return;
        }

        var observation = new PendingObservation
        {
            ProcessId = processId,
            ExecutablePath = normalizedPath,
            GameTitle = game.Title ?? string.Empty,
            GameNormalized = game.Normalized ?? string.Empty,
            SteamAppId = game.SteamAppIdVerified ? game.SteamAppId : 0,
            DetectionMethod = detectionMethod ?? string.Empty,
            IsStillActive = isStillActive,
        };

        lock (pendingLock)
        {
            if (isDisposed) return;

            if (pendingObservations.TryGetValue(processId, out PendingObservation? existing))
            {
                if (!existing.ValidationStarted &&
                    string.Equals(existing.ExecutablePath, normalizedPath, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existing.GameNormalized, observation.GameNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    // 重复的 WMI/poll/前景窗口观测不得无限重置稳定性窗口。
                    existing.IsStillActive = isStillActive;
                    return;
                }

                CancelPendingNoLock(existing);
                pendingObservations.Remove(processId);
            }

            observation.ValidationTimer = new Timer(
                ValidatePendingObservation,
                observation,
                Timeout.InfiniteTimeSpan,
                Timeout.InfiniteTimeSpan);
            pendingObservations[processId] = observation;
            observation.ValidationTimer.Change(stabilityDelay, Timeout.InfiniteTimeSpan);
        }

        Log(string.Format(
            CultureInfo.InvariantCulture,
            "learning pending: PID {0}, game '{1}', executable '{2}', method '{3}'",
            processId,
            observation.GameTitle,
            observation.ExecutablePath,
            observation.DetectionMethod));
        RaiseStateChanged();
    }

    /// <summary>取消观察（<paramref name="processId"/> 为 0 表示全部）。</summary>
    public void CancelObservation(uint processId, string? reason = null)
    {
        bool changed = false;
        int cancelledCount = 0;
        lock (pendingLock)
        {
            if (processId == 0)
            {
                cancelledCount = pendingObservations.Count;
                changed = cancelledCount > 0;
                CancelAllPendingNoLock();
            }
            else if (pendingObservations.TryGetValue(processId, out PendingObservation? observation))
            {
                pendingObservations.Remove(processId);
                CancelPendingNoLock(observation);
                cancelledCount = 1;
                changed = true;
            }
        }

        if (changed)
        {
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "learning cancelled: {0} pending observation(s), PID {1}, reason '{2}'",
                cancelledCount,
                processId == 0 ? "all" : processId.ToString(CultureInfo.InvariantCulture),
                string.IsNullOrWhiteSpace(reason) ? "not specified" : reason));
            RaiseStateChanged();
        }
    }

    /// <summary>删除若干绑定。返回实际删除数。</summary>
    public int DeleteBindings(IEnumerable<string>? executablePaths)
    {
        if (executablePaths is null) return 0;

        var requested = new HashSet<string>(
            executablePaths.Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);
        if (requested.Count == 0) return 0;

        bool pendingChanged = false;
        lock (pendingLock)
        {
            uint[] cancelledProcessIds = pendingObservations
                .Where(x => requested.Contains(x.Value.ExecutablePath))
                .Select(x => x.Key)
                .ToArray();
            foreach (uint processId in cancelledProcessIds)
            {
                PendingObservation observation = pendingObservations[processId];
                pendingObservations.Remove(processId);
                CancelPendingNoLock(observation);
                pendingChanged = true;
            }
        }

        int deleted = 0;
        lock (mutationLock)
        {
            if (isDisposed) return 0;
            var current = Volatile.Read(ref snapshot);
            var replacement = new Dictionary<string, LearnedExecutableBinding>(
                current, StringComparer.OrdinalIgnoreCase);

            foreach (string path in requested)
            {
                if (replacement.Remove(path)) deleted++;
            }

            if (deleted > 0)
            {
                Interlocked.Increment(ref persistenceDirty);
                Volatile.Write(ref snapshot, replacement);
            }
        }

        if (deleted > 0)
        {
            ThreadPool.QueueUserWorkItem(_ => PersistCurrentSnapshot());
            RaiseBindingsChanged();
        }
        else if (pendingChanged)
        {
            RaiseStateChanged();
        }
        return deleted;
    }

    /// <summary>把选中的绑定导出为「可并入官方库」的 JSON 片段。</summary>
    public bool ExportBindings(
        IEnumerable<LearnedExecutableBinding>? bindings,
        string? outputPath,
        out string? error)
    {
        error = null;
        if (bindings is null || string.IsNullOrWhiteSpace(outputPath))
        {
            error = "未选择任何可导出的学习记录。";
            return false;
        }

        try
        {
            if (string.Equals(
                Path.GetFullPath(outputPath),
                Path.GetFullPath(storagePath),
                StringComparison.OrdinalIgnoreCase))
            {
                error = "不能把本地学习缓存本身作为导出目标。";
                return false;
            }

            LearnedExecutableBinding[] selected = bindings
                .Where(x => x is not null && !string.IsNullOrWhiteSpace(x.Executable))
                .Select(x => x.Clone())
                .ToArray();
            if (selected.Length == 0)
            {
                error = "未选择任何可导出的学习记录。";
                return false;
            }

            string json = BuildExportJson(selected);
            File.WriteAllText(outputPath, json, new UTF8Encoding(false));
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private void ValidatePendingObservation(object? state)
    {
        if (state is not PendingObservation observation || isDisposed) return;

        lock (pendingLock)
        {
            if (!pendingObservations.TryGetValue(observation.ProcessId, out PendingObservation? current) ||
                !ReferenceEquals(current, observation) || observation.IsCancelled)
            {
                return;
            }

            observation.ValidationStarted = true;
            if (observation.ValidationTimer is not null)
            {
                observation.ValidationTimer.Dispose();
                observation.ValidationTimer = null;
            }
        }

        bool stable = false;
        try
        {
            stable = observation.IsStillActive(observation.ProcessId, observation.ExecutablePath);
        }
        catch (Exception ex)
        {
            Log("learning validation failed: " + ex.Message);
        }

        lock (pendingLock)
        {
            if (!pendingObservations.TryGetValue(observation.ProcessId, out PendingObservation? current) ||
                !ReferenceEquals(current, observation) || observation.IsCancelled)
            {
                return;
            }

            pendingObservations.Remove(observation.ProcessId);
        }

        if (!stable || isDisposed)
        {
            Log(string.Format(
                CultureInfo.InvariantCulture,
                "learning rejected: PID {0}, executable '{1}' was no longer the same running process after {2:F1} seconds",
                observation.ProcessId,
                observation.ExecutablePath,
                stabilityDelay.TotalSeconds));
            RaiseStateChanged();
            return;
        }

        UpsertValidatedBinding(observation);
    }

    private void UpsertValidatedBinding(PendingObservation observation)
    {
        string now = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        LearnedExecutableBinding validated;

        lock (mutationLock)
        {
            if (isDisposed) return;
            var current = Volatile.Read(ref snapshot);
            var replacement = new Dictionary<string, LearnedExecutableBinding>(
                current, StringComparer.OrdinalIgnoreCase);

            if (replacement.TryGetValue(observation.ExecutablePath, out LearnedExecutableBinding? existing) &&
                existing is not null)
            {
                validated = existing.Clone();
                validated.GameTitle = observation.GameTitle;
                validated.GameNormalized = observation.GameNormalized;
                validated.SteamAppId = observation.SteamAppId;
                if (!observation.DetectionMethod.StartsWith("learned exact path", StringComparison.OrdinalIgnoreCase))
                {
                    validated.DetectionMethod = observation.DetectionMethod;
                }
                validated.LastSeenUtc = now;
                validated.SuccessfulSessions = existing.SuccessfulSessions == int.MaxValue
                    ? int.MaxValue
                    : Math.Max(1, existing.SuccessfulSessions + 1);
            }
            else
            {
                validated = new LearnedExecutableBinding
                {
                    Path = observation.ExecutablePath,
                    Executable = Path.GetFileName(observation.ExecutablePath),
                    GameTitle = observation.GameTitle,
                    GameNormalized = observation.GameNormalized,
                    SteamAppId = observation.SteamAppId,
                    DetectionMethod = observation.DetectionMethod,
                    FirstSeenUtc = now,
                    LastSeenUtc = now,
                    SuccessfulSessions = 1,
                };
            }

            replacement[observation.ExecutablePath] = validated;
            TrimToCapacity(replacement);
            // 先标脏再发布快照：Dispose 与 mutationLock 同步，
            // 因此不会出现「已发布绑定但没标记需落盘」的窗口。
            Interlocked.Increment(ref persistenceDirty);
            Volatile.Write(ref snapshot, replacement);
        }

        Log(string.Format(
            CultureInfo.InvariantCulture,
            "learning validated: PID {0}, game '{1}', executable '{2}'",
            observation.ProcessId,
            observation.GameTitle,
            observation.ExecutablePath));
        ThreadPool.QueueUserWorkItem(_ => PersistCurrentSnapshot());
        RaiseBindingsChanged();
    }

    private void LoadFromDisk()
    {
        if (isDisposed) return;
        if (!File.Exists(storagePath)) return;

        Dictionary<string, LearnedExecutableBinding> loaded;
        try
        {
            string json = File.ReadAllText(storagePath, Encoding.UTF8);
            loaded = ParseLocalJson(json);
        }
        catch (Exception ex)
        {
            Log("learning cache ignored: " + ex.Message);
            return;
        }

        lock (mutationLock)
        {
            if (isDisposed) return;
            var live = Volatile.Read(ref snapshot);
            foreach (KeyValuePair<string, LearnedExecutableBinding> item in live)
            {
                loaded[item.Key] = item.Value;
            }
            TrimToCapacity(loaded);
            Volatile.Write(ref snapshot, loaded);
        }

        RaiseBindingsChanged();
    }

    private static Dictionary<string, LearnedExecutableBinding> ParseLocalJson(string json)
    {
        var result = new Dictionary<string, LearnedExecutableBinding>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json)) return result;

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || ReadInt(root, "version") != SchemaVersion)
        {
            throw new InvalidDataException("Unsupported learned executable cache version.");
        }

        if (!root.TryGetProperty("bindings", out JsonElement bindingsArray) ||
            bindingsArray.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (JsonElement item in bindingsArray.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;

            string path = NormalizeStoredPath(ReadString(item, "path"));
            string normalized = CloudGameListService.Normalize(ReadString(item, "gameNormalized"));
            int steamAppId = ReadInt(item, "steamAppId");
            string firstSeen = NormalizeTimestamp(ReadString(item, "firstSeenUtc"));
            string lastSeen = NormalizeTimestamp(ReadString(item, "lastSeenUtc"));

            if (string.IsNullOrWhiteSpace(path) || !Path.IsPathRooted(path) ||
                (string.IsNullOrWhiteSpace(normalized) && steamAppId <= 0) ||
                string.IsNullOrWhiteSpace(firstSeen) || string.IsNullOrWhiteSpace(lastSeen))
            {
                continue;
            }

            result[path] = new LearnedExecutableBinding
            {
                Path = path,
                Executable = Path.GetFileName(path),
                GameTitle = ReadString(item, "gameTitle"),
                GameNormalized = normalized,
                SteamAppId = steamAppId,
                DetectionMethod = ReadString(item, "detectionMethod"),
                FirstSeenUtc = firstSeen,
                LastSeenUtc = lastSeen,
                SuccessfulSessions = Math.Max(1, ReadInt(item, "successfulSessions")),
            };
        }
        return result;
    }

    private void PersistCurrentSnapshot()
    {
        lock (persistenceLock)
        {
            int dirtyGeneration;
            Dictionary<string, LearnedExecutableBinding> current;
            lock (mutationLock)
            {
                dirtyGeneration = Volatile.Read(ref persistenceDirty);
                if (dirtyGeneration == 0) return;
                current = Volatile.Read(ref snapshot);
            }

            string tempPath = storagePath + ".tmp";
            try
            {
                string json = BuildLocalJson(current.Values);
                string? directory = Path.GetDirectoryName(storagePath);
                if (!string.IsNullOrWhiteSpace(directory) && !Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                File.WriteAllText(tempPath, json, new UTF8Encoding(false));
                if (File.Exists(storagePath))
                {
                    File.Replace(tempPath, storagePath, null, true);
                }
                else
                {
                    File.Move(tempPath, storagePath);
                }
                Interlocked.CompareExchange(ref persistenceDirty, 0, dirtyGeneration);
            }
            catch (Exception ex)
            {
                Log("learning persistence failed: " + ex.Message);
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // 清理失败无所谓
                }
            }
        }
    }

    private static string BuildLocalJson(IEnumerable<LearnedExecutableBinding> bindings)
    {
        LearnedExecutableBinding[] ordered = bindings
            .Where(x => x is not null)
            .OrderBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": 1,\n  \"bindings\": [");

        for (int i = 0; i < ordered.Length; i++)
        {
            LearnedExecutableBinding item = ordered[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append("    {\n");
            AppendJsonProperty(sb, "path", item.Path, true);
            AppendJsonProperty(sb, "executable", item.Executable, true);
            AppendJsonProperty(sb, "gameTitle", item.GameTitle, true);
            AppendJsonProperty(sb, "gameNormalized", item.GameNormalized, true);
            AppendJsonNumber(sb, "steamAppId", item.SteamAppId, true);
            AppendJsonProperty(sb, "detectionMethod", item.DetectionMethod, true);
            AppendJsonProperty(sb, "firstSeenUtc", item.FirstSeenUtc, true);
            AppendJsonProperty(sb, "lastSeenUtc", item.LastSeenUtc, true);
            AppendJsonNumber(sb, "successfulSessions", item.SuccessfulSessions, false);
            sb.Append("    }");
        }

        if (ordered.Length > 0) sb.Append('\n');
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static string BuildExportJson(IEnumerable<LearnedExecutableBinding> bindings)
    {
        var groups = bindings
            .GroupBy(x => new ExportKey(x.SteamAppId, x.GameNormalized ?? string.Empty))
            .Select(g => new
            {
                Key = g.Key,
                Executables = g.Select(x => Path.GetFileName(x.Executable))
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(x => x, StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
            })
            .Where(x => x.Executables.Length > 0)
            .OrderBy(x => x.Key.SteamAppId)
            .ThenBy(x => x.Key.Normalized, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var sb = new StringBuilder();
        sb.Append("{\n  \"version\": 1,\n  \"games\": [");
        for (int i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            sb.Append(i == 0 ? "\n" : ",\n");
            sb.Append("    {\n");
            AppendJsonNumber(sb, "steamAppId", group.Key.SteamAppId, true);
            AppendJsonProperty(sb, "normalized", group.Key.Normalized, true);
            sb.Append("      \"executables\": [");
            for (int j = 0; j < group.Executables.Length; j++)
            {
                if (j > 0) sb.Append(", ");
                sb.Append(JsonSerializer.Serialize(group.Executables[j], StringJsonOptions));
            }
            sb.Append("]\n    }");
        }
        if (groups.Length > 0) sb.Append('\n');
        sb.Append("  ]\n}\n");
        return sb.ToString();
    }

    private static void AppendJsonProperty(StringBuilder sb, string name, string? value, bool comma)
    {
        sb.Append("      \"").Append(name).Append("\": ")
            .Append(JsonSerializer.Serialize(value ?? string.Empty, StringJsonOptions));
        sb.Append(comma ? ",\n" : "\n");
    }

    private static void AppendJsonNumber(StringBuilder sb, string name, int value, bool comma)
    {
        sb.Append("      \"").Append(name).Append("\": ")
            .Append(value.ToString(CultureInfo.InvariantCulture));
        sb.Append(comma ? ",\n" : "\n");
    }

    private static string ReadString(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out JsonElement value)) return string.Empty;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? string.Empty,
            JsonValueKind.Null => string.Empty,
            JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => string.Empty,
        };
    }

    private static int ReadInt(JsonElement item, string key)
    {
        if (!item.TryGetProperty(key, out JsonElement value)) return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.TryGetInt32(out int n) ? n : 0,
            JsonValueKind.String => int.TryParse(value.GetString(), NumberStyles.Integer,
                CultureInfo.InvariantCulture, out int parsed) ? parsed : 0,
            _ => 0,
        };
    }

    private static string NormalizeStoredPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;
        try
        {
            return Path.GetFullPath(path.Trim());
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string NormalizeTimestamp(string? value)
    {
        if (!DateTime.TryParse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out DateTime parsed))
        {
            return string.Empty;
        }
        return parsed.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture);
    }

    private static void TrimToCapacity(Dictionary<string, LearnedExecutableBinding> bindings)
    {
        if (bindings.Count <= MaximumBindings) return;

        int removeCount = bindings.Count - MaximumBindings;
        string[] oldest = bindings.Values
            .OrderBy(x => x.LastSeenUtc, StringComparer.Ordinal)
            .ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase)
            .Take(removeCount)
            .Select(x => x.Path)
            .ToArray();
        foreach (string path in oldest) bindings.Remove(path);
    }

    private static void CancelPendingNoLock(PendingObservation? observation)
    {
        if (observation is null) return;
        observation.IsCancelled = true;
        if (observation.ValidationTimer is not null)
        {
            observation.ValidationTimer.Dispose();
            observation.ValidationTimer = null;
        }
    }

    private void CancelAllPendingNoLock()
    {
        foreach (PendingObservation observation in pendingObservations.Values)
        {
            CancelPendingNoLock(observation);
        }
        pendingObservations.Clear();
    }

    private void RaiseBindingsChanged()
    {
        try { BindingsChanged?.Invoke(); }
        catch { /* 订阅者异常不得影响服务 */ }
        RaiseStateChanged();
    }

    private void RaiseStateChanged()
    {
        try { StateChanged?.Invoke(); }
        catch { /* 订阅者异常不得影响服务 */ }
    }

    private void Log(string message)
    {
        ThreadPool.QueueUserWorkItem(_ =>
            AppLog.WriteLine(logDirectory, "tray_detection.log", message));
    }

    public void Dispose()
    {
        lock (pendingLock)
        {
            if (isDisposed) return;
            isDisposed = true;
            CancelAllPendingNoLock();
        }

        // 与「刚通过最终取消检查、正准备落库」的校验线程同步。
        lock (mutationLock)
        {
        }

        // 已验证的绑定必须熬过一次立即退出：排队的线程池落盘只是优化。
        if (Volatile.Read(ref persistenceDirty) != 0)
        {
            PersistCurrentSnapshot();
        }
    }

    private sealed class PendingObservation
    {
        public uint ProcessId;
        public string ExecutablePath = string.Empty;
        public string GameTitle = string.Empty;
        public string GameNormalized = string.Empty;
        public int SteamAppId;
        public string DetectionMethod = string.Empty;
        public Func<uint, string, bool> IsStillActive = null!;
        public Timer? ValidationTimer;
        public bool ValidationStarted;
        public bool IsCancelled;
    }

    private sealed class ExportKey : IEquatable<ExportKey>
    {
        public readonly int SteamAppId;
        public readonly string Normalized;

        public ExportKey(int steamAppId, string normalized)
        {
            SteamAppId = steamAppId;
            Normalized = normalized ?? string.Empty;
        }

        public bool Equals(ExportKey? other)
            => other is not null && SteamAppId == other.SteamAppId &&
               string.Equals(Normalized, other.Normalized, StringComparison.OrdinalIgnoreCase);

        public override bool Equals(object? obj) => Equals(obj as ExportKey);

        public override int GetHashCode()
        {
            unchecked
            {
                return (SteamAppId * 397) ^ StringComparer.OrdinalIgnoreCase.GetHashCode(Normalized);
            }
        }
    }
}
