using System.Diagnostics;
using System.Globalization;
using System.Management;
using DsInApex.Core.Interop;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 游戏进程监控：检测游戏启动/退出，并据此起停桥接会话。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/ProcessMonitorService.cs</c>（1029 行），
/// <b>检测链路与并发模型照搬</b>。
/// </para>
///
/// <para>
/// <b>三重检测（互为兜底，缺一不可）：</b>
/// </para>
/// <list type="number">
/// <item><b>WMI 事件</b> <c>Win32_ProcessStartTrace</c> / <c>ProcessStopTrace</c> ——
/// 进程一启动就感知，延迟最低。但需要相应权限，可能不可用。</item>
/// <item><b>250ms 轮询</b> —— WMI 不可用时的兜底，遍历全部进程做候选匹配。</item>
/// <item><b>前景窗口</b>（1 秒一次）—— 捕捉「已在运行、但刚被切到前台」的游戏。</item>
/// </list>
///
/// <para>
/// <b>核心状态机：</b>识别到游戏 → 起会话 → 跟踪其全部 PID →
/// PID 全部退出后进入 2 秒宽限期 → 宽限期内同款游戏换进程则「接管」，
/// 否则停会话。<b>不要动这个宽限期逻辑</b>：很多游戏重启/换图的瞬间 PID 会变，
/// 没有宽限期会导致桥接反复起停，手柄在游戏里闪断。
/// </para>
///
/// <para>
/// <b>「强制手动桥接」模式（<c>ForcedProfile != "none"</c>）：</b>
/// 此时用户已手动开了桥接，监控只做「被动学习」（认出游戏、沉淀绑定），
/// <b>绝不接管已有的会话</b>。
/// </para>
/// </summary>
public sealed class ProcessMonitorService : IDisposable
{
    private static readonly TimeSpan ProcessExitGracePeriod = TimeSpan.FromSeconds(2);

    private readonly CloudGameListService gameListService;
    private readonly EngineSessionManager sessionManager;
    private readonly ExecutableLearningService learningService;
    private readonly TraySettings settings;
    private readonly Timer pollTimer;
    private readonly Dictionary<uint, DateTime> retryCooldowns = [];
    private readonly Dictionary<uint, long> evaluatedProcesses = [];
    private readonly object sessionStateLock = new();
    private readonly GameProcessSessionTracker processSession = new();

    private ManagementEventWatcher? startWatcher;
    private ManagementEventWatcher? stopWatcher;

    private bool isDisposed;
    private bool isStoppingDetectedSession;
    private int isPolling;
    private DateTime nextProcessSweepUtc;
    private DateTime nextForegroundCheckUtc;

    /// <summary>识别到游戏并成功起会话（参数：游戏、可执行文件路径）。</summary>
    public event Action<SupportedGame, string>? GameDetected;

    /// <summary>游戏退出、会话已停（参数：最后已知可执行文件路径）。</summary>
    public event Action<string>? GameExited;

    public ProcessMonitorService(
        CloudGameListService gameListService,
        EngineSessionManager sessionManager,
        ExecutableLearningService learningService,
        TraySettings settings)
    {
        this.gameListService = gameListService;
        this.sessionManager = sessionManager;
        this.learningService = learningService;
        this.settings = settings;

        InitializeWmiWatchers();

        pollTimer = new Timer(OnPollTick, null, 100, 250);
    }

    private void InitializeWmiWatchers()
    {
        try
        {
            startWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStartTrace"));
            startWatcher.EventArrived += OnProcessStartedWmi;
            startWatcher.Start();
        }
        catch (Exception ex)
        {
            LogEvent("WMI StartWatcher unavailable: " + ex.Message);
            try { startWatcher?.Dispose(); } catch { /* 忽略 */ }
            startWatcher = null;
        }

        try
        {
            stopWatcher = new ManagementEventWatcher(new WqlEventQuery("SELECT * FROM Win32_ProcessStopTrace"));
            stopWatcher.EventArrived += OnProcessStoppedWmi;
            stopWatcher.Start();
        }
        catch (Exception ex)
        {
            LogEvent("WMI StopWatcher unavailable: " + ex.Message);
            try { stopWatcher?.Dispose(); } catch { /* 忽略 */ }
            stopWatcher = null;
        }
    }

    private void OnProcessStartedWmi(object sender, EventArrivedEventArgs e)
    {
        if (isDisposed) return;
        long detectionStartedAt = Stopwatch.GetTimestamp();
        DateTime? processEventUtc = null;

        try
        {
            processEventUtc = ReadWmiEventCreatedUtc(e);
            string? processName = e.NewEvent.Properties["ProcessName"].Value as string;
            object? pidObj = e.NewEvent.Properties["ProcessID"].Value;
            if (string.IsNullOrWhiteSpace(processName) || pidObj is null) return;

            uint pid = Convert.ToUInt32(pidObj);
            if (pid == 0 || IsTrackedProcess(pid)) return;
            if (IsSystemOrIgnoredProcess(processName)) return;

            if (!settings.AutoDetectGames) return;
            bool passiveLearning = IsManualBridgeMode();

            CheckCandidateProcess(pid, processName, null, detectionStartedAt, processEventUtc, "WMI", passiveLearning);
        }
        catch (Exception ex)
        {
            LogEvent("[OnProcessStartedWmi] " + ex.Message);
        }
    }

    private void OnProcessStoppedWmi(object sender, EventArrivedEventArgs e)
    {
        if (isDisposed) return;

        try
        {
            object? pidObj = e.NewEvent.Properties["ProcessID"].Value;
            if (pidObj is null) return;

            uint pid = Convert.ToUInt32(pidObj);
            if (pid != 0)
            {
                learningService.CancelObservation(pid, "process stopped (WMI)");
                HandleProcessStopped(pid, "Process stopped (WMI)");
            }
        }
        catch (Exception ex)
        {
            LogEvent("[OnProcessStoppedWmi] " + ex.Message);
        }
    }

    /// <summary>强制立即重扫一次（设置变更后调用）。</summary>
    public void ForceCheck()
    {
        lock (evaluatedProcesses)
        {
            evaluatedProcesses.Clear();
        }
        nextProcessSweepUtc = DateTime.MinValue;
        ThreadPool.QueueUserWorkItem(_ => OnPollTick(null));
    }

    private void OnPollTick(object? state)
    {
        if (isDisposed) return;
        if (Interlocked.CompareExchange(ref isPolling, 1, 0) != 0) return;

        try
        {
            PruneExitedTrackedProcesses();

            if (!settings.AutoDetectGames)
            {
                ResetTrackedSessionOnly();
                ResetPendingLearning();
                return;
            }

            bool passiveLearning = IsManualBridgeMode();
            if (passiveLearning)
            {
                // 强制桥接已占用引擎会话：继续识别游戏，但绝不尝试起停它。
                ResetTrackedSessionOnly();
            }

            if (!passiveLearning && HasTrackedSession() && !sessionManager.IsSessionActive)
            {
                ResetTrackedSessionOnly();
            }

            if ((passiveLearning || !HasTrackedSession() || IsAwaitingReplacement()) &&
                DateTime.UtcNow >= nextProcessSweepUtc)
            {
                nextProcessSweepUtc = DateTime.UtcNow.AddMilliseconds(250);
                ScanRunningProcesses(passiveLearning);
            }

            if (!passiveLearning && TryStopExpiredSession())
            {
                return;
            }

            if (DateTime.UtcNow < nextForegroundCheckUtc) return;
            nextForegroundCheckUtc = DateTime.UtcNow.AddSeconds(1);

            IntPtr hwnd = NativeMethods.GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return;

            string? exePath = NativeMethods.GetActiveProcessPath(hwnd, out uint pid);
            if (string.IsNullOrWhiteSpace(exePath) || pid == 0) return;
            if (IsTrackedProcess(pid)) return;

            string fileName = Path.GetFileName(exePath);
            if (IsSystemOrIgnoredProcess(fileName)) return;

            CheckCandidateProcess(pid, fileName, exePath, 0, null, "foreground", passiveLearning);
        }
        catch (Exception ex)
        {
            LogEvent("[OnPollTick] " + ex.Message);
        }
        finally
        {
            Interlocked.Exchange(ref isPolling, 0);
        }
    }

    private bool ScanRunningProcesses(bool passiveLearning = false)
    {
        uint[] processIds = NativeMethods.GetProcessIds();
        if (processIds.Length == 0) return false;

        var runningPids = new HashSet<uint>();
        foreach (uint pid in processIds)
        {
            if (pid == 0) continue;
            runningPids.Add(pid);
            if (IsTrackedProcess(pid)) continue;

            lock (evaluatedProcesses)
            {
                if (evaluatedProcesses.ContainsKey(pid)) continue;
                evaluatedProcesses[pid] = 0;
            }

            try
            {
                using var process = Process.GetProcessById((int)pid);
                string fileName = process.ProcessName + ".exe";
                if (IsSystemOrIgnoredProcess(fileName)) continue;

                long startedAt;
                try
                {
                    startedAt = process.StartTime.ToUniversalTime().Ticks;
                }
                catch
                {
                    startedAt = 0;
                }
                lock (evaluatedProcesses)
                {
                    evaluatedProcesses[pid] = startedAt;
                }

                string? exePath = null;
                try
                {
                    // PROCESS_QUERY_LIMITED_INFORMATION 对多数提升权限的游戏也够用，
                    // 而 Process.MainModule 会被拒绝。
                    exePath = NativeMethods.GetProcessPath(pid);
                    if (string.IsNullOrWhiteSpace(exePath) && process.MainModule is not null)
                        exePath = process.MainModule.FileName;
                    if (!string.IsNullOrWhiteSpace(exePath))
                        fileName = Path.GetFileName(exePath);
                }
                catch
                {
                    // 取不到路径时退回进程名，继续尝试
                }

                if (IsSystemOrIgnoredProcess(fileName)) continue;

                CheckCandidateProcess(pid, fileName, exePath, 0, null, "poll", passiveLearning);
                if (IsTrackedProcess(pid)) return true;
            }
            catch (ArgumentException)
            {
                lock (evaluatedProcesses)
                {
                    evaluatedProcesses.Remove(pid);
                }
            }
            catch (Exception ex)
            {
                LogEvent("[ScanRunningProcesses] Candidate failed: " + ex.Message);
            }
        }

        lock (evaluatedProcesses)
        {
            var stoppedPids = new List<uint>();
            foreach (uint pid in evaluatedProcesses.Keys)
            {
                if (!runningPids.Contains(pid)) stoppedPids.Add(pid);
            }
            foreach (uint pid in stoppedPids)
            {
                evaluatedProcesses.Remove(pid);
                learningService.CancelObservation(pid, "process no longer enumerated");
            }
        }
        return false;
    }

    private void CheckCandidateProcess(
        uint pid,
        string fileName,
        string? fullPath = null,
        long detectionStartedAt = 0,
        DateTime? processEventUtc = null,
        string detectionSource = "poll",
        bool passiveLearning = false)
    {
        if (detectionStartedAt == 0) detectionStartedAt = Stopwatch.GetTimestamp();
        if (IsTrackedProcess(pid) || IsSystemOrIgnoredProcess(fileName)) return;

        if (!passiveLearning)
        {
            lock (retryCooldowns)
            {
                if (retryCooldowns.TryGetValue(pid, out DateTime cooldown))
                {
                    if (DateTime.UtcNow < cooldown) return;
                    retryCooldowns.Remove(pid);
                }
            }
        }

        string? exePath = fullPath;
        if (string.IsNullOrWhiteSpace(exePath))
        {
            try
            {
                exePath = NativeMethods.GetProcessPath(pid);
                if (string.IsNullOrWhiteSpace(exePath))
                {
                    using var proc = Process.GetProcessById((int)pid);
                    exePath = proc.MainModule?.FileName ?? fileName;
                }
            }
            catch
            {
                exePath = fileName;
            }
        }

        if (IsSystemOrIgnoredProcess(exePath)) return;

        string exeTitle = Path.GetFileNameWithoutExtension(exePath);
        string folderName = GetParentFolderName(exePath);

        if (TryResolveGame(exePath, exeTitle, folderName, fileName, out SupportedGame? matchedGame, out string? matchedBy) &&
            matchedGame is not null)
        {
            if (GameActivationPolicy.ShouldActivate(matchedGame, settings, exeTitle, folderName, fileName))
            {
                if (passiveLearning)
                {
                    HandlePassiveLearningDetected(matchedGame, pid, exePath, matchedBy, detectionSource);
                }
                else
                {
                    HandleGameDetected(matchedGame, pid, exePath, matchedBy, detectionStartedAt, processEventUtc, detectionSource);
                }
            }
        }
    }

    private void HandlePassiveLearningDetected(
        SupportedGame game,
        uint pid,
        string exePath,
        string? matchedBy,
        string? detectionSource)
    {
        if (game is null) return;

        BeginExecutableObservation(pid, exePath, game, matchedBy);

        LogDetection(string.Format(
            CultureInfo.InvariantCulture,
            "Passively matched PID {0} to '{1}' using {2}. Path: {3}. Source: {4}. " +
            "The forced bridge session remains untouched.",
            pid, game.Title, matchedBy ?? "unknown evidence", exePath, detectionSource ?? "unknown"));
    }

    private bool TryResolveGame(
        string exePath,
        string exeTitle,
        string folderName,
        string fileName,
        out SupportedGame? game,
        out string? matchedBy)
    {
        game = null;
        matchedBy = null;

        if (learningService.TryResolve(exePath, gameListService, out game) && game is not null)
        {
            matchedBy = "learned exact path '" + exePath + "'";
            return true;
        }

        if (gameListService.TryFindByExecutable(exePath, out game) && game is not null)
        {
            matchedBy = "database exact executable '" + Path.GetFileName(exePath) + "'";
            return true;
        }

        var candidates = new List<KeyValuePair<string, string>>();
        AddExecutableMetadataCandidates(exePath, candidates);
        AddCandidate(candidates, "folder name", folderName);
        AddCandidate(candidates, "executable name", exeTitle);
        AddCandidate(candidates, "file name", fileName);

        foreach (KeyValuePair<string, string> candidate in candidates)
        {
            if (gameListService.TryFindExactGame(candidate.Value, out game) && game is not null)
            {
                matchedBy = FormatMatchEvidence(candidate);
                return true;
            }
        }

        foreach (KeyValuePair<string, string> candidate in candidates)
        {
            if (gameListService.TryFindGame(candidate.Value, out game) && game is not null)
            {
                matchedBy = FormatMatchEvidence(candidate);
                return true;
            }
        }

        return false;
    }

    private static void AddExecutableMetadataCandidates(
        string exePath,
        ICollection<KeyValuePair<string, string>> candidates)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath)) return;

        try
        {
            FileVersionInfo version = FileVersionInfo.GetVersionInfo(exePath);
            AddCandidate(candidates, "product name", version.ProductName);
            AddCandidate(candidates, "file description", version.FileDescription);
        }
        catch
        {
            // 读不到版本信息不影响其它候选
        }
    }

    private static void AddCandidate(
        ICollection<KeyValuePair<string, string>> candidates,
        string source,
        string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return;

        foreach (KeyValuePair<string, string> candidate in candidates)
        {
            if (string.Equals(candidate.Value, value, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
        }

        candidates.Add(new KeyValuePair<string, string>(source, value.Trim()));
    }

    private static string FormatMatchEvidence(KeyValuePair<string, string> candidate)
        => candidate.Key + " '" + candidate.Value + "'";

    private void HandleGameDetected(
        SupportedGame game,
        uint pid,
        string exePath,
        string? matchedBy,
        long detectionStartedAt,
        DateTime? processEventUtc,
        string? detectionSource)
    {
        try
        {
            bool attachedToExistingSession = false;
            bool resumedDuringGrace = false;
            double candidateToStartMs = 0;
            double startSessionToReadyMs = 0;
            double candidateToReadyMs = 0;

            lock (sessionStateLock)
            {
                if (isStoppingDetectedSession) return;

                if (processSession.HasSession && !sessionManager.IsSessionActive)
                {
                    processSession.Clear();
                }

                if (processSession.HasSession)
                {
                    if (!processSession.IsSameGame(game))
                    {
                        LogDetection(string.Format(
                            "Ignored PID {0} for '{1}' while '{2}' is active.",
                            pid, game.Title, processSession.ActiveGame?.Title ?? "?"));
                        return;
                    }

                    resumedDuringGrace = processSession.IsAwaitingReplacement;
                    attachedToExistingSession = processSession.TryAttach(game, pid, exePath);
                }
                else
                {
                    long startSessionCalledAt = Stopwatch.GetTimestamp();
                    int apexProfileSlot = settings.GetApexProfileSlot(game.Normalized);
                    // 学习关注的是「游戏进程是否稳定」，不应等待控制器初始化
                    //（后者本就可能失败或耗时到超时）。
                    BeginExecutableObservation(pid, exePath, game, matchedBy);
                    if (!sessionManager.StartSession(game.Title, game.Profile, settings, apexProfileSlot, out string? error))
                    {
                        lock (retryCooldowns)
                        {
                            retryCooldowns[pid] = DateTime.UtcNow.AddSeconds(10);
                        }
                        lock (evaluatedProcesses)
                        {
                            evaluatedProcesses.Remove(pid);
                        }
                        return;
                    }

                    long readyAt = Stopwatch.GetTimestamp();
                    candidateToStartMs = ElapsedMilliseconds(detectionStartedAt, startSessionCalledAt);
                    startSessionToReadyMs = ElapsedMilliseconds(startSessionCalledAt, readyAt);
                    candidateToReadyMs = ElapsedMilliseconds(detectionStartedAt, readyAt);

                    processSession.Start(game, pid, exePath);
                }
            }

            BeginExecutableObservation(pid, exePath, game, matchedBy);

            if (attachedToExistingSession)
            {
                LogDetection(string.Format(
                    "Attached PID {0} to active game '{1}' using {2}. Path: {3}. Grace recovery: {4}",
                    pid, game.Title, matchedBy ?? "unknown evidence", exePath,
                    resumedDuringGrace ? "yes" : "no"));
                return;
            }

            double processEventToReadyMs = processEventUtc.HasValue
                ? Math.Max(0, (DateTime.UtcNow - processEventUtc.Value).TotalMilliseconds)
                : -1;
            LogDetection(string.Format(
                CultureInfo.InvariantCulture,
                "Matched PID {0} to '{1}' using {2}. Path: {3}. Source: {4}. " +
                "Candidate-to-StartSession: {5:F3} ms. StartSession-to-Ready: {6:F3} ms. " +
                "Candidate-to-Ready: {7:F3} ms. Process-event-to-Ready: {8}",
                pid, game.Title, matchedBy ?? "unknown evidence", exePath, detectionSource ?? "unknown",
                candidateToStartMs, startSessionToReadyMs, candidateToReadyMs,
                processEventToReadyMs >= 0
                    ? processEventToReadyMs.ToString("F3", CultureInfo.InvariantCulture) + " ms"
                    : "n/a"));

            GameDetected?.Invoke(game, exePath);
        }
        catch (Exception ex)
        {
            LogEvent("[HandleGameDetected] " + ex.Message);
        }
    }

    private void HandleProcessStopped(uint processId, string reason)
    {
        try
        {
            bool awaitingReplacement;
            lock (sessionStateLock)
            {
                bool removed = processSession.Remove(processId, DateTime.UtcNow, ProcessExitGracePeriod);
                awaitingReplacement = processSession.IsAwaitingReplacement;
                if (!removed) return;
            }

            learningService.CancelObservation(processId, reason);

            LogDetection(string.Format(
                "Detached PID {0}: {1}. Waiting for same-game replacement: {2}",
                processId, reason, awaitingReplacement ? "yes" : "no"));
        }
        catch (Exception ex)
        {
            LogEvent("[HandleProcessStopped] " + ex.Message);
        }
    }

    private bool TryStopExpiredSession()
    {
        string? oldPath;
        lock (sessionStateLock)
        {
            if (isStoppingDetectedSession || !processSession.ShouldStop(DateTime.UtcNow))
            {
                return false;
            }

            isStoppingDetectedSession = true;
            oldPath = processSession.LastKnownPath;
            processSession.Clear();
        }

        try
        {
            sessionManager.StopSession("No game process remained after the PID replacement grace period");

            if (!string.IsNullOrWhiteSpace(oldPath))
            {
                GameExited?.Invoke(oldPath);
            }
            return true;
        }
        finally
        {
            lock (sessionStateLock)
            {
                isStoppingDetectedSession = false;
                nextProcessSweepUtc = DateTime.MinValue;
            }
            lock (evaluatedProcesses)
            {
                evaluatedProcesses.Clear();
            }
            if (!isDisposed) ForceCheck();
        }
    }

    private void BeginExecutableObservation(
        uint processId,
        string executablePath,
        SupportedGame game,
        string? detectionMethod)
    {
        long observedStartTicks = TryGetProcessStartTimeTicks(processId);
        learningService.BeginObservation(
            processId,
            executablePath,
            game,
            detectionMethod,
            (pid, path) => IsExecutableObservationActive(pid, path, observedStartTicks, game));
    }

    private bool IsExecutableObservationActive(
        uint processId,
        string executablePath,
        long observedStartTicks,
        SupportedGame game)
    {
        if (isDisposed || !settings.AutoDetectGames) return false;

        long currentStartTicks = TryGetProcessStartTimeTicks(processId);
        if (observedStartTicks > 0 && currentStartTicks > 0 && observedStartTicks != currentStartTicks)
        {
            return false;
        }

        string? activePath = NativeMethods.GetProcessPath(processId);
        if (!string.IsNullOrWhiteSpace(activePath))
        {
            if (!string.Equals(activePath, executablePath, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return IsStillEligibleForLearning(game, executablePath);
        }

        try
        {
            using var process = Process.GetProcessById((int)processId);
            if (process.HasExited ||
                !string.Equals(
                    process.ProcessName,
                    Path.GetFileNameWithoutExtension(executablePath),
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return IsStillEligibleForLearning(game, executablePath);
        }
        catch
        {
            return false;
        }
    }

    private bool IsStillEligibleForLearning(SupportedGame game, string executablePath)
    {
        string fileName = Path.GetFileName(executablePath);
        return GameActivationPolicy.ShouldActivate(
            game, settings,
            Path.GetFileNameWithoutExtension(executablePath),
            GetParentFolderName(executablePath),
            fileName);
    }

    private static long TryGetProcessStartTimeTicks(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }

    private bool IsManualBridgeMode()
        => !string.IsNullOrWhiteSpace(settings.ForcedProfile) &&
           !string.Equals(settings.ForcedProfile, "none", StringComparison.OrdinalIgnoreCase);

    private void PruneExitedTrackedProcesses()
    {
        uint[] trackedProcessIds;
        lock (sessionStateLock)
        {
            trackedProcessIds = processSession.GetProcessIds();
        }

        foreach (uint processId in trackedProcessIds)
        {
            bool hasExited = false;
            string reason = "Process terminated";
            try
            {
                using var process = Process.GetProcessById((int)processId);
                hasExited = process.HasExited;
            }
            catch (ArgumentException)
            {
                hasExited = true;
                reason = "Process exited";
            }
            catch (Exception)
            {
                hasExited = true;
                reason = "Process inaccessible";
            }

            if (hasExited)
            {
                HandleProcessStopped(processId, reason);
            }
        }
    }

    private bool IsTrackedProcess(uint processId)
    {
        lock (sessionStateLock)
        {
            return processSession.Contains(processId);
        }
    }

    private bool HasTrackedSession()
    {
        lock (sessionStateLock)
        {
            return processSession.HasSession;
        }
    }

    private bool IsAwaitingReplacement()
    {
        lock (sessionStateLock)
        {
            return processSession.IsAwaitingReplacement;
        }
    }

    private void ResetTrackedSessionOnly()
    {
        bool hadSession;
        lock (sessionStateLock)
        {
            hadSession = processSession.HasSession;
            processSession.Clear();
        }

        if (!hadSession) return;

        lock (evaluatedProcesses)
        {
            evaluatedProcesses.Clear();
        }
        nextProcessSweepUtc = DateTime.MinValue;
    }

    private void ResetPendingLearning()
    {
        learningService.CancelObservation(0, "automatic detection disabled");
        lock (evaluatedProcesses)
        {
            evaluatedProcesses.Clear();
        }
        nextProcessSweepUtc = DateTime.MinValue;
    }

    private static DateTime? ReadWmiEventCreatedUtc(EventArrivedEventArgs eventArgs)
    {
        if (eventArgs?.NewEvent is null) return null;

        try
        {
            PropertyData? property = eventArgs.NewEvent.Properties["TIME_CREATED"];
            if (property?.Value is null) return null;
            return DateTime.FromFileTimeUtc(Convert.ToInt64(property.Value));
        }
        catch
        {
            return null;
        }
    }

    private static double ElapsedMilliseconds(long startedAt, long endedAt)
    {
        if (startedAt <= 0 || endedAt < startedAt) return 0;
        return (endedAt - startedAt) * 1000.0 / Stopwatch.Frequency;
    }

    private static bool IsSystemOrIgnoredProcess(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return true;
        if (PlatformClientProcessFilter.IsExcluded(fileName)) return true;

        string lower = fileName.ToLowerInvariant();
        return lower is "system.exe"
            or "registry.exe"
            or "secure system.exe"
            or "memory compression.exe"
            or "explorer.exe"
            or "taskmgr.exe"
            or "apexsensebridge.exe"
            or "apexsensebridgetray.exe"
            or "apexsensebridgecontrol.exe"
            or "dsinapex.exe"
            or "applicationframehost.exe"
            or "shellexperiencehost.exe"
            or "systemsettings.exe"
            or "searchhost.exe"
            or "startmenuexperiencehost.exe"
            or "lockapp.exe"
            or "devenv.exe"
            or "cmd.exe"
            or "powershell.exe"
            or "pwsh.exe"
            or "conhost.exe"
            or "windowsterminal.exe";
    }

    private static string GetParentFolderName(string filePath)
    {
        try
        {
            string? dir = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(dir)) return string.Empty;
            return Path.GetFileName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void LogEvent(string msg) => AppLog.WriteLine("tray_crash.log", msg);

    private static void LogDetection(string message) => AppLog.WriteLine("tray_detection.log", message);

    public void Dispose()
    {
        if (isDisposed) return;
        isDisposed = true;

        try
        {
            if (startWatcher is not null)
            {
                startWatcher.Stop();
                startWatcher.Dispose();
            }
        }
        catch { /* 忽略 */ }

        try
        {
            if (stopWatcher is not null)
            {
                stopWatcher.Stop();
                stopWatcher.Dispose();
            }
        }
        catch { /* 忽略 */ }

        pollTimer.Dispose();

        bool hadTrackedSession;
        lock (sessionStateLock)
        {
            hadTrackedSession = processSession.HasSession;
            processSession.Clear();
        }

        learningService.CancelObservation(0, "tray app closing");

        if (hadTrackedSession)
        {
            try
            {
                sessionManager.StopSession("Tray app closing");
            }
            catch { /* 尽力而为 */ }
        }
    }
}
