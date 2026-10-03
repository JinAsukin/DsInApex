using DsInApex.Core.Models;

namespace DsInApex.Core.Services;

/// <summary>
/// 跟踪「当前正在运行的游戏进程集合」与其生命周期。
///
/// <para>
/// 迁移自上游 <c>ApexSenseBridgeTray/Services/GameProcessSessionTracker.cs</c>（135 行），逻辑照搬。
/// </para>
///
/// <para>
/// <b>它解决的核心问题：</b>很多游戏是「启动器 + 本体 + 子进程」多进程结构。
/// 同一款游戏可能先起一个 PID、再拉起另一个 PID。若只认第一个 PID，游戏切进程时桥接会被误停。
/// 因此本类维护<b>一个游戏的一个 PID 集合</b>，并在集合清空后给一个
/// <c>exitGracePeriod</c> 宽限期（默认 2 秒），期间若有同款游戏的新 PID 出现就直接「接管」，
/// 只有宽限期内没等到替换进程，才判定游戏真的退出了。
/// </para>
/// </summary>
internal sealed class GameProcessSessionTracker
{
    private readonly Dictionary<uint, string> processes = [];

    public SupportedGame? ActiveGame { get; private set; }
    public string? LastKnownPath { get; private set; }
    public DateTime? ExitDeadlineUtc { get; private set; }

    /// <summary>当前是否有被跟踪的游戏会话（无论进程是否还在）。</summary>
    public bool HasSession => ActiveGame is not null;

    public bool HasRunningProcesses => processes.Count > 0;

    /// <summary>进程已全部退出，但仍在等待「同款游戏换进程」的宽限期内。</summary>
    public bool IsAwaitingReplacement => HasSession && processes.Count == 0 && ExitDeadlineUtc.HasValue;

    public int ProcessCount => processes.Count;

    /// <summary>开始跟踪一款新游戏（清空既有进程集合）。</summary>
    public void Start(SupportedGame game, uint processId, string? executablePath)
    {
        ArgumentNullException.ThrowIfNull(game);
        if (processId == 0) throw new ArgumentOutOfRangeException(nameof(processId));

        processes.Clear();
        ActiveGame = game;
        LastKnownPath = executablePath;
        ExitDeadlineUtc = null;
        processes[processId] = executablePath ?? string.Empty;
    }

    /// <summary>把同一款游戏的新 PID 并入当前会话（宽限期恢复 / 多进程）。</summary>
    public bool TryAttach(SupportedGame game, uint processId, string? executablePath)
    {
        if (!IsSameGame(game) || processId == 0) return false;

        processes[processId] = executablePath ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(executablePath))
        {
            LastKnownPath = executablePath;
        }
        ExitDeadlineUtc = null;
        return true;
    }

    /// <summary>移除一个已退出的 PID；若集合清空则开始宽限期倒计时。</summary>
    public bool Remove(uint processId, DateTime nowUtc, TimeSpan exitGracePeriod)
    {
        if (!processes.Remove(processId)) return false;

        if (processes.Count == 0 && HasSession && !ExitDeadlineUtc.HasValue)
        {
            ExitDeadlineUtc = nowUtc.Add(exitGracePeriod);
        }
        return true;
    }

    public bool Contains(uint processId)
        => processId != 0 && processes.ContainsKey(processId);

    /// <summary>检查某 PID 是否对应同一路径（防止 PID 复用导致误判）。</summary>
    public bool Contains(uint processId, string? executablePath)
    {
        if (processId == 0 || !processes.TryGetValue(processId, out string? trackedPath)) return false;

        return string.Equals(
            trackedPath ?? string.Empty,
            executablePath ?? string.Empty,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 判断给定游戏是否与当前活动游戏「是同一款」。
    /// 优先用已核验的 Steam AppID；否则回落到归一化名。
    /// </summary>
    public bool IsSameGame(SupportedGame? game)
    {
        if (ActiveGame is null || game is null) return false;

        if (ActiveGame.SteamAppIdVerified && ActiveGame.SteamAppId > 0 &&
            game.SteamAppIdVerified && game.SteamAppId > 0)
        {
            return ActiveGame.SteamAppId == game.SteamAppId;
        }

        string activeIdentity = GetNormalizedIdentity(ActiveGame);
        string candidateIdentity = GetNormalizedIdentity(game);
        return !string.IsNullOrWhiteSpace(activeIdentity) &&
               string.Equals(activeIdentity, candidateIdentity, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>宽限期是否已到（该收尾停止桥接了）。</summary>
    public bool ShouldStop(DateTime nowUtc)
        => IsAwaitingReplacement && nowUtc >= ExitDeadlineUtc!.Value;

    public uint[] GetProcessIds()
    {
        var result = new uint[processes.Count];
        processes.Keys.CopyTo(result, 0);
        return result;
    }

    public void Clear()
    {
        processes.Clear();
        ActiveGame = null;
        LastKnownPath = null;
        ExitDeadlineUtc = null;
    }

    private static string GetNormalizedIdentity(SupportedGame? game)
    {
        if (game is null) return string.Empty;
        string identity = !string.IsNullOrWhiteSpace(game.Normalized) ? game.Normalized : game.Title;
        return CloudGameListService.Normalize(identity ?? string.Empty);
    }
}
