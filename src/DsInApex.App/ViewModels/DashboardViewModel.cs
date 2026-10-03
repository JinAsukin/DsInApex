using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 仪表盘 ViewModel：桥接状态徽章、当前游戏、扳机/触觉指示灯、快捷操作。
///
/// <para>
/// <b>线程模型（重要）：</b><see cref="EngineSessionManager"/> 的会话事件由
/// <b>发起启动的那个线程</b>触发。启动动作被放在 <c>Task.Run</c> 里执行
/// （<c>bridge-triggers</c> 的就绪等待是阻塞式的，放在 UI 线程上会冻结界面最多 20 秒），
/// 因此事件回调落在后台线程 —— 所有界面属性更新都必须经 <see cref="OnUi"/> 回到 UI 线程。
/// </para>
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_app.log";

    private readonly EngineSessionManager _session;
    private readonly TraySettings _settings;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;

    public DashboardViewModel(EngineSessionManager session, TraySettings settings, ILocalizationService loc)
    {
        _session = session;
        _settings = settings;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _session.SessionStarted += OnSessionStarted;
        _session.SessionStopped += OnSessionStopped;
        _session.SessionError += OnSessionError;
        _session.LogMessage += OnLogMessage;
        _loc.LanguageChanged += OnLanguageChanged;

        RescanEngine();
        Refresh();
    }

    // ═══════════════ 会话状态 ═══════════════

    [ObservableProperty]
    public partial bool IsBridgeActive { get; set; }

    /// <summary>待机态（用于徽章与空状态的可见性切换）。</summary>
    public bool IsBridgeStandby => !IsBridgeActive;

    /// <summary>会话阶段文案（空闲 / 就绪 / 失败）。</summary>
    [ObservableProperty]
    public partial string SessionStateText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CurrentGameText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProfileText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ProcessIdText { get; set; } = "-";

    /// <summary>启动 / 停止动作进行中（按钮禁用，防重复点击）。</summary>
    [ObservableProperty]
    public partial bool Busy { get; set; }

    public bool CanStartBridge => EngineFound && !IsBridgeActive && !Busy;

    public bool CanStopBridge => IsBridgeActive && !Busy;

    // ═══════════════ 引擎与指示灯 ═══════════════

    [ObservableProperty]
    public partial bool EngineFound { get; set; }

    [ObservableProperty]
    public partial string EnginePathText { get; set; } = string.Empty;

    /// <summary>指示灯：发动机（自适应扳机）是否参与。</summary>
    public bool TriggersEnabled => _settings.TriggerOnAdaptiveTriggers;
    public bool TriggersDisabled => !TriggersEnabled;

    /// <summary>指示灯：触觉反馈是否参与。</summary>
    public bool HapticsEnabled => _settings.TriggerOnHapticFeedback;
    public bool HapticsDisabled => !HapticsEnabled;

    /// <summary>最近一条会话/引擎消息（诊断用，让用户看到引擎到底说了什么）。</summary>
    [ObservableProperty]
    public partial string LastMessage { get; set; } = string.Empty;

    // ═══════════════ 属性变化联动 ═══════════════

    partial void OnIsBridgeActiveChanged(bool value)
    {
        OnPropertyChanged(nameof(IsBridgeStandby));
        OnPropertyChanged(nameof(CanStartBridge));
        OnPropertyChanged(nameof(CanStopBridge));
    }

    partial void OnEngineFoundChanged(bool value) => OnPropertyChanged(nameof(CanStartBridge));

    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanStartBridge));
        OnPropertyChanged(nameof(CanStopBridge));
    }

    // ═══════════════ 快捷操作 ═══════════════

    /// <summary>
    /// 手动启动桥接（等价于上游「强制持续激活」的即时效果）。
    /// 游戏标题取本地化的「强制手动桥接」，与上游行为一致。
    /// </summary>
    [RelayCommand]
    private async Task StartBridgeAsync()
    {
        if (!CanStartBridge)
        {
            return;
        }

        Busy = true;
        string gameTitle = _loc.Get("Loc_ManualBridgeGameTitle");

        try
        {
            // 就绪等待是阻塞的，必须离开 UI 线程
            (bool ok, string? err) = await Task.Run(() =>
            {
                bool started = _session.StartSession(gameTitle, "standard", _settings, 0, out string? error);
                return (started, error);
            }).ConfigureAwait(true);

            if (!ok)
            {
                LastMessage = err ?? _loc.Get("Loc_ErrBridgeStartFailedShort");
            }
        }
        catch (Exception ex)
        {
            LastMessage = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"启动桥接异常：{AppLog.Describe(ex)}");
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    [RelayCommand]
    private async Task StopBridgeAsync()
    {
        if (!CanStopBridge)
        {
            return;
        }

        Busy = true;
        try
        {
            await Task.Run(() => _session.StopSession("manual")).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            LastMessage = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"停止桥接异常：{AppLog.Describe(ex)}");
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    /// <summary>重新定位引擎（安装 / 拷贝引擎后无需重启应用）。</summary>
    [RelayCommand]
    private void RescanEngine()
    {
        string path = EngineLocator.ResolveEngineWithTrace(out IReadOnlyList<string> trace);
        EngineFound = !string.IsNullOrWhiteSpace(path);

        if (EngineFound)
        {
            EnginePathText = path;
        }
        else
        {
            EnginePathText = _loc.Get("Loc_EngineMissing");
        }

        foreach (string line in trace)
        {
            AppLog.Info(LogFileName, "引擎定位 " + line);
        }

        Refresh();
    }

    // ═══════════════ 事件 ═══════════════

    private void OnSessionStarted(string gameTitle, string profileName)
        => OnUi(() =>
        {
            LastMessage = $"{gameTitle} / {profileName}";
            Refresh();
        });

    private void OnSessionStopped(string reason)
        => OnUi(() =>
        {
            LastMessage = reason;
            Refresh();
        });

    private void OnSessionError(string error)
        => OnUi(() =>
        {
            LastMessage = error;
            Refresh();
        });

    private void OnLogMessage(string message)
        => OnUi(() => LastMessage = message);

    private void OnLanguageChanged(object? sender, string lang) => OnUi(Refresh);

    /// <summary>
    /// 设置页改动后刷新指示灯。由 <c>DashboardPage.OnNavigatedTo</c> 调用 ——
    /// 同进程内跨页共享同一个 VM，但设置项的读写点在不同 VM 上，
    /// 这里不做事件风暴，改为「进入页面时同步一次」。
    /// </summary>
    public void RefreshFromSettings()
    {
        OnPropertyChanged(nameof(TriggersEnabled));
        OnPropertyChanged(nameof(TriggersDisabled));
        OnPropertyChanged(nameof(HapticsEnabled));
        OnPropertyChanged(nameof(HapticsDisabled));
        Refresh();
    }

    /// <summary>按当前会话状态与语言重算全部展示文案。</summary>
    private void Refresh()
    {
        bool active = _session.IsSessionActive;
        IsBridgeActive = active;

        if (!active)
        {
            SessionStateText = _loc.Get("Loc_PhaseEmpty");
            CurrentGameText = _loc.Get("Loc_NoActiveGame");
            ProfileText = _loc.Get("Loc_ProfileStandard");
            ProcessIdText = "-";
            return;
        }

        // 有会话但进程已死 = 失败态
        SessionStateText = _session.IsSessionHealthy
            ? _loc.Get("Loc_PhaseReady")
            : _loc.Get("Loc_PhaseFailed");

        CurrentGameText = _session.ActiveGameTitle;

        string profile = _session.ActiveProfile;
        ProfileText = string.Equals(profile, "none", StringComparison.OrdinalIgnoreCase)
            ? _loc.Get("Loc_ProfileStandard")
            : profile;

        int pid = _session.ActiveProcessId;
        ProcessIdText = pid != 0 ? pid.ToString() : "-";
    }

    /// <summary>把回调切回 UI 线程；拿不到调度器时直接执行（例如单元测试环境）。</summary>
    private void OnUi(Action action)
    {
        if (_dispatcher is null || _dispatcher.HasThreadAccess)
        {
            action();
        }
        else
        {
            _dispatcher.TryEnqueue(() => action());
        }
    }
}
