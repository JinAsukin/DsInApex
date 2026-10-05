using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.Core.Localization;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Xaml.Controls;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 设置页 ViewModel：检测策略、启动条件、通知、强制激活、触觉阈值、超时、外观。
///
/// <para>
/// 所有修改<b>立即写入</b> <c>tray_settings.json</c>（上游也是即时保存语义）——
/// 这样用户不需要找「保存」按钮，也不存在「改了没生效」的歧义。
/// </para>
///
/// <para>
/// 语言与主题这两块<b>不在这里重新实现</b>，而是转发给
/// <see cref="MainWindowViewModel"/>（它已负责持久化与窗口级副作用）。
/// 设置页通过 <see cref="Shell"/> 属性直接绑定它的选中态，
/// 避免同一份状态出现两个真值来源。
/// </para>
/// </summary>
public partial class SettingsViewModel : ObservableObject
{
    /// <summary>触觉阈值合法区间（引擎契约：0–95）。</summary>
    private const int HapticMin = 0;
    private const int HapticMax = 95;

    /// <summary>初始化超时合法区间（秒）。</summary>
    private const int TimeoutMin = 5;
    private const int TimeoutMax = 120;

    /// <summary>
    /// APEX 4 陀螺仪灵敏度合法区间（引擎契约：25–400）。
    /// 直接沿用 Core 的常量，避免两处各写一份、日后改动时漏掉一边。
    /// </summary>
    private const int GyroMin = EngineSessionManager.GyroPercentMin;
    private const int GyroMax = EngineSessionManager.GyroPercentMax;

    /// <summary>强制持续激活在设置文件中的取值（沿用上游语义：standard / none）。</summary>
    private const string ForcedProfileOn = "standard";
    private const string ForcedProfileOff = "none";

    private readonly TraySettings _settings;
    private readonly UpdateCheckerService _updateChecker;

    /// <summary>开机自启快照。真源是注册表，这里只缓存供绑定（读写都重新采一次）。</summary>
    private AutoStartStatus _autoStart;

    /// <summary>便携部署报告（P6）。采集含少量文件系统探测，构造时一次即可。</summary>
    private readonly PortableLayoutReport _portable;

    /// <summary>控制器恢复状态快照（P6，只读）。</summary>
    private RecoveryStatus _recovery;

    public SettingsViewModel(TraySettings settings, MainWindowViewModel shell, UpdateCheckerService updateChecker)
    {
        _settings = settings;
        _updateChecker = updateChecker;
        Shell = shell;

        _autoStart = AutoStartService.Query();
        _portable = PortableLayoutService.Collect();
        _recovery = RecoveryStateService.Collect();

        // 主题 / 语言的真值在 Shell 上，这里只是转发。Shell 状态变化时同步本页选中态，
        // 例如从托盘菜单切语言后，设置页的单选钮也要跟着动。
        Shell.PropertyChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(ThemeIndex));
            OnPropertyChanged(nameof(LanguageIndex));
        };

        // 本页大量文案是 VM 计算属性（不走 loc 附加属性注册表），
        // 语言热切换时必须自己重算一遍，否则整块「便携部署 / 恢复状态」会停在旧语言。
        LocalizationService.Shared.LanguageChanged += (_, _) => RaiseLocalizedTexts();
    }

    /// <summary>外壳 VM（语言 / 主题的绑定目标）。</summary>
    public MainWindowViewModel Shell { get; }

    // ═══════════════ 外观（转发给 Shell，不重复实现） ═══════════════

    /// <summary>主题单选索引：0 = 跟随系统，1 = 浅色，2 = 深色。</summary>
    public int ThemeIndex
    {
        get => _settings.Theme switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        set
        {
            switch (value)
            {
                case 1: Shell.IsLightThemeSelected = true; break;
                case 2: Shell.IsDarkThemeSelected = true; break;
                default: Shell.IsSystemThemeSelected = true; break;
            }
            OnPropertyChanged();
        }
    }

    /// <summary>语言单选索引：0 = 简体中文，1 = English。</summary>
    public int LanguageIndex
    {
        get => Shell.IsChineseSelected ? 0 : 1;
        set
        {
            if (value == 0) Shell.IsChineseSelected = true;
            else Shell.IsEnglishSelected = true;
            OnPropertyChanged();
        }
    }

    // ═══════════════ 自动检测 ═══════════════

    public bool AutoDetectGames
    {
        get => _settings.AutoDetectGames;
        set
        {
            if (_settings.AutoDetectGames == value) return;
            _settings.AutoDetectGames = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool TriggerOnAdaptiveTriggers
    {
        get => _settings.TriggerOnAdaptiveTriggers;
        set
        {
            if (_settings.TriggerOnAdaptiveTriggers == value) return;
            _settings.TriggerOnAdaptiveTriggers = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool TriggerOnHapticFeedback
    {
        get => _settings.TriggerOnHapticFeedback;
        set
        {
            if (_settings.TriggerOnHapticFeedback == value) return;
            _settings.TriggerOnHapticFeedback = value;
            Save();
            OnPropertyChanged();
        }
    }

    // ═══════════════ 配置 ═══════════════

    /// <summary>「强制持续激活」。映射到上游的 <c>ForcedProfile == "standard"</c>。</summary>
    public bool ManualBridge
    {
        get => string.Equals(_settings.ForcedProfile, ForcedProfileOn, StringComparison.OrdinalIgnoreCase);
        set
        {
            string target = value ? ForcedProfileOn : ForcedProfileOff;
            if (string.Equals(_settings.ForcedProfile, target, StringComparison.OrdinalIgnoreCase)) return;
            _settings.ForcedProfile = target;
            Save();
            OnPropertyChanged();
        }
    }

    public bool EnableNotifications
    {
        get => _settings.EnableNotifications;
        set
        {
            if (_settings.EnableNotifications == value) return;
            _settings.EnableNotifications = value;
            Save();
            OnPropertyChanged();
        }
    }

    public bool EnableRumble
    {
        get => _settings.EnableRumble;
        set
        {
            if (_settings.EnableRumble == value) return;
            _settings.EnableRumble = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>触觉阈值（0–95）。用 <c>double</c> 是为了配合 <c>NumberBox.Value</c>。</summary>
    public double HapticThreshold
    {
        get => _settings.HapticThresholdPercent;
        set
        {
            int clamped = Clamp((int)Math.Round(value), HapticMin, HapticMax);
            if (_settings.HapticThresholdPercent == clamped) return;
            _settings.HapticThresholdPercent = clamped;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>初始化超时（5–120 秒）。</summary>
    public double InitTimeoutSeconds
    {
        get => _settings.InitializationTimeoutSeconds;
        set
        {
            int clamped = Clamp((int)Math.Round(value), TimeoutMin, TimeoutMax);
            if (_settings.InitializationTimeoutSeconds == clamped) return;
            _settings.InitializationTimeoutSeconds = clamped;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// APEX 4 陀螺仪总灵敏度（25–400，100 = 引擎原始标定强度）。
    ///
    /// <para>
    /// 用 <c>double</c> 是为了配合 <c>NumberBox.Value</c>；区间必须与引擎一致，
    /// 越界会被 C++ 参数解析器拒绝并导致<b>整个桥接启动失败</b>。
    /// </para>
    /// </summary>
    public double Apex4GyroStrength
    {
        get => _settings.Apex4GyroStrengthPercent;
        set
        {
            int clamped = Clamp((int)Math.Round(value), GyroMin, GyroMax);
            if (_settings.Apex4GyroStrengthPercent == clamped) return;
            _settings.Apex4GyroStrengthPercent = clamped;
            Save();
            OnPropertyChanged();
            NotifyGyroDerived();
        }
    }

    /// <summary>APEX 4 陀螺仪 yaw 轴单独修正（25–400，100 = 不修正）。</summary>
    public double Apex4GyroYawStrength
    {
        get => _settings.Apex4GyroYawStrengthPercent;
        set
        {
            int clamped = Clamp((int)Math.Round(value), GyroMin, GyroMax);
            if (_settings.Apex4GyroYawStrengthPercent == clamped) return;
            _settings.Apex4GyroYawStrengthPercent = clamped;
            Save();
            OnPropertyChanged();
            NotifyGyroDerived();
        }
    }

    /// <summary>两个「派生自陀螺仪取值」的属性 —— 任一档位变化都要一起刷新。</summary>
    private void NotifyGyroDerived()
    {
        OnPropertyChanged(nameof(Apex4GyroIsCustomized));
        OnPropertyChanged(nameof(Apex4GyroStateText));
    }

    /// <summary>陀螺仪是否处于非默认取值（用于界面上提示"改了才生效"）。</summary>
    public bool Apex4GyroIsCustomized
        => _settings.Apex4GyroStrengthPercent != 100
        || _settings.Apex4GyroYawStrengthPercent != 100;

    /// <summary>把陀螺仪两档恢复默认（100 / 100）。</summary>
    [RelayCommand]
    private void ResetApex4Gyro()
    {
        bool changed = false;

        if (_settings.Apex4GyroStrengthPercent != 100)
        {
            _settings.Apex4GyroStrengthPercent = 100;
            changed = true;
            OnPropertyChanged(nameof(Apex4GyroStrength));
        }

        if (_settings.Apex4GyroYawStrengthPercent != 100)
        {
            _settings.Apex4GyroYawStrengthPercent = 100;
            changed = true;
            OnPropertyChanged(nameof(Apex4GyroYawStrength));
        }

        if (changed)
        {
            Save();
            OnPropertyChanged(nameof(Apex4GyroIsCustomized));
            OnPropertyChanged(nameof(Apex4GyroStateText));
        }
    }

    /// <summary>陀螺仪档位的一句话状态（默认 / 已自定义 + 具体数值）。</summary>
    public string Apex4GyroStateText => Apex4GyroIsCustomized
        ? LocalizationService.Shared.Format(
            "Loc_GyroStateCustom",
            _settings.Apex4GyroStrengthPercent,
            _settings.Apex4GyroYawStrengthPercent)
        : LocalizationService.Shared.Get("Loc_GyroStateDefault");

    // ═══════════════ 诊断展示 ═══════════════

    /// <summary>设置文件位置（让用户能找到、能备份）。</summary>
    public string SettingsFilePath => TraySettings.FilePath;

    // ═══════════════ P6 · 启动与后台 ═══════════════

    /// <summary>关闭窗口时隐藏到托盘（默认开）。</summary>
    public bool CloseWindowToTray
    {
        get => _settings.CloseWindowToTray;
        set
        {
            if (_settings.CloseWindowToTray == value) return;
            _settings.CloseWindowToTray = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>启动时静默检查更新。</summary>
    public bool CheckUpdatesOnStartup
    {
        get => _settings.CheckUpdatesOnStartup;
        set
        {
            if (_settings.CheckUpdatesOnStartup == value) return;
            _settings.CheckUpdatesOnStartup = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 开机自启开关。
    ///
    /// <para>
    /// <b>直接读写注册表</b>（HKCU Run 键），不落任何 json ——
    /// 注册表是唯一真源。写完之后立刻回读一次并刷新界面，
    /// 保证「界面显示的状态 = 系统实际状态」，绝不出现「开关是开的但开机没反应」。
    /// </para>
    /// </summary>
    public bool StartWithWindows
    {
        get => _autoStart.IsEffective;
        set
        {
            string? error = null;

            bool ok = value
                ? AutoStartService.Enable(out error)
                : AutoStartService.Disable(out error);

            if (!ok)
            {
                _autoStart = AutoStartService.Query();
                AutoStartErrorMessage = error ?? string.Empty;
            }
            else
            {
                AutoStartErrorMessage = null;
                _autoStart = AutoStartService.Query();
            }

            RaiseAutoStartProperties();
        }
    }

    /// <summary>自启项读取/写入失败时的原因。</summary>
    private string? AutoStartErrorMessage { get; set; }

    /// <summary>是否需要显示自启问题提示条（路径漂移或读取失败）。</summary>
    public bool AutoStartIssueVisible =>
        _autoStart.IsStale || _autoStart.State == AutoStartState.Unreadable
        || !string.IsNullOrEmpty(AutoStartErrorMessage);

    public InfoBarSeverity AutoStartIssueSeverity =>
        _autoStart.IsStale ? InfoBarSeverity.Warning : InfoBarSeverity.Error;

    public string AutoStartIssueTitle => _autoStart.IsStale
        ? L("Loc_StartWithWindowsStaleTitle")
        : L("Loc_StartWithWindows");

    public string AutoStartIssueMessage => _autoStart.IsStale
        ? L("Loc_StartWithWindowsStale")
        : LF("Loc_StartWithWindowsUnreadable",
            AutoStartErrorMessage ?? _autoStart.Error ?? string.Empty);

    /// <summary>只有「路径漂移」才给修复按钮 —— 读取失败是权限问题，重写注册表帮不上忙。</summary>
    public bool AutoStartCanRepair => _autoStart.IsStale;

    /// <summary>当前登记的命令行（诊断展示）。</summary>
    public string AutoStartRegisteredCommand =>
        string.IsNullOrWhiteSpace(_autoStart.RegisteredCommand)
            ? L("Loc_PortableMissing")
            : _autoStart.RegisteredCommand;

    /// <summary>本机当前应有的命令行（诊断展示）。</summary>
    public string AutoStartExpectedCommand => _autoStart.ExpectedCommand;

    /// <summary>一键把自启项改写成当前路径（便携版换目录后用）。</summary>
    public void RepairAutoStart()
    {
        _autoStart = AutoStartService.RepairIfStale();

        if (_autoStart.IsStale)
        {
            AutoStartErrorMessage = "repair-failed";
        }
        else
        {
            AutoStartErrorMessage = null;
        }

        RaiseAutoStartProperties();
    }

    /// <summary>重新采样自启状态（从托盘切换后回到设置页时调用）。</summary>
    public void RefreshAutoStart()
    {
        _autoStart = AutoStartService.Query();
        RaiseAutoStartProperties();
    }

    private void RaiseAutoStartProperties()
    {
        OnPropertyChanged(nameof(StartWithWindows));
        OnPropertyChanged(nameof(AutoStartIssueVisible));
        OnPropertyChanged(nameof(AutoStartIssueSeverity));
        OnPropertyChanged(nameof(AutoStartIssueTitle));
        OnPropertyChanged(nameof(AutoStartIssueMessage));
        OnPropertyChanged(nameof(AutoStartCanRepair));
        OnPropertyChanged(nameof(AutoStartRegisteredCommand));
        OnPropertyChanged(nameof(AutoStartExpectedCommand));
    }

    /// <summary>
    /// 「立即检查更新」。返回<b>已本地化</b>的一句话，由 code-behind 弹对话框。
    ///
    /// <para>
    /// 为什么不在这里直接弹 UI：VM 不该碰 ContentDialog（它需要 XamlRoot，
    /// 而 XamlRoot 属于页面）。VM 只负责拿数据与组织文案。
    /// </para>
    /// </summary>
    public async Task<string> CheckUpdatesNowAsync()
    {
        UpdateInfo info = await _updateChecker.CheckAsync(silent: false);

        if (!info.CheckSucceeded)
        {
            return L("Loc_UpdateError") + (info.Error ?? string.Empty);
        }

        if (info.IsNoReleaseYet)
        {
            return LF("Loc_UpdateNoRelease", info.CurrentVersion);
        }

        if (!info.HasUpdate)
        {
            return LF("Loc_UpdateUpToDate", info.CurrentVersion) + "\n\n" + L("Loc_UpdatePortableHint");
        }

        string body = LF("Loc_UpdateAvailableBody", info.CurrentVersion, info.LatestVersion ?? "?");
        return body + "\n\n" + L("Loc_UpdatePortableHint") + "\n" + (info.ReleaseUrl ?? string.Empty);
    }

    /// <summary>发布页地址（供「打开发布页」按钮使用）。</summary>
    public static string ReleasesPageUrl => UpdateCheckerService.ReleasesPageUrl;

    // ═══════════════ P6 · 便携部署 ═══════════════

    public string PortableAppDir => _portable.AppDirectory;

    public string PortableEngine => _portable.EnginePresent
        ? _portable.EnginePath
        : L("Loc_PortableEngineMissing");

    public string PortablePrereq => _portable.PrerequisiteFileCount > 0
        ? $"{_portable.PrerequisiteFileCount} × {L("Loc_PortablePresent")}"
        : L("Loc_PortableMissing");

    /// <summary>
    /// 用户数据目录。
    ///
    /// ⚠️ 它<b>不</b>随程序目录走 —— 这是刻意与官方版共用同一份设置/学习记录的兼容红线，
    /// 因此界面必须把它显式写出来，免得用户以为"绿色版删目录就干净了"。
    /// </summary>
    public string PortableDataDir => _portable.DataDirectory;

    public string PortableLicense => _portable.LicensePresent
        ? L("Loc_PortablePresent")
        : L("Loc_PortableLicenseMissing");

    /// <summary>程序目录不可写时的提示（可见性由 XAML 绑定）。</summary>
    public bool PortableWritableWarningVisible => !_portable.AppDirectoryWritable;

    public string PortableWritableWarningText => L("Loc_PortableWritableNo");

    /// <summary>部署是否可用（引擎与许可证齐备）。</summary>
    public bool PortableReady => _portable.IsDeploymentUsable;

    // ═══════════════ P6 · 控制器恢复状态 ═══════════════

    /// <summary>重新采样恢复状态（用户点「刷新」或恢复动作之后）。</summary>
    public void RefreshRecovery()
    {
        _recovery = RecoveryStateService.Collect();

        OnPropertyChanged(nameof(RecoverySeverity));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(RecoveryDetail));
        OnPropertyChanged(nameof(RecoveryNeedsAttention));
        OnPropertyChanged(nameof(RecoveryRunOnceText));
    }

    public InfoBarSeverity RecoverySeverity => _recovery.State switch
    {
        RecoveryMarkerState.PendingRecovery => InfoBarSeverity.Warning,
        RecoveryMarkerState.Unreadable => InfoBarSeverity.Error,
        RecoveryMarkerState.ActiveSession => InfoBarSeverity.Informational,
        _ => InfoBarSeverity.Success,
    };

    public string RecoverySummary => _recovery.State switch
    {
        RecoveryMarkerState.Clean => L("Loc_RecoveryClean"),
        RecoveryMarkerState.ActiveSession => L("Loc_RecoveryActive"),
        RecoveryMarkerState.PendingRecovery => L("Loc_RecoveryPending"),
        _ => LF("Loc_RecoveryUnreadable", _recovery.Error ?? string.Empty),
    };

    public string RecoveryDetail => _recovery.State switch
    {
        RecoveryMarkerState.PendingRecovery => LF(
            "Loc_RecoveryPendingDetail", _recovery.OriginalWhitelistCount, _recovery.OriginalBlacklistCount),
        RecoveryMarkerState.ActiveSession => $"PID {_recovery.OwnerProcessId} / phase {_recovery.Phase}",
        _ => _recovery.MarkerKeyPath,
    };

    public bool RecoveryNeedsAttention => _recovery.NeedsUserAttention;

    public string RecoveryRunOnceText => LF(
        "Loc_RecoveryRunOnce",
        _recovery.RunOnceRegistered ? L("Loc_RecoveryRegistered") : L("Loc_RecoveryNotRegistered"));

    /// <summary>恢复状态详情（含键路径，供排障复制）。</summary>
    public string RecoveryMarkerPathText => _recovery.MarkerKeyPath;

    // ═══════════════ 工具 ═══════════════

    /// <summary>取本地化文案的短别名（属性体里用得多，直接写 <c>LocalizationService.Shared.Get</c> 太吵）。</summary>
    private static string L(string key) => LocalizationService.Shared.Get(key);

    /// <summary>带占位符的本地化文案（<c>Get</c> 的另一半）。</summary>
    private static string LF(string key, params object?[] args) => LocalizationService.Shared.Format(key, args);

    /// <summary>语言切换后重算所有「非 loc 附加属性」驱动的文案（VM 计算属性不在 Localize 注册表里）。</summary>
    private void RaiseLocalizedTexts()
    {
        OnPropertyChanged(nameof(AutoStartIssueTitle));
        OnPropertyChanged(nameof(AutoStartIssueMessage));
        OnPropertyChanged(nameof(AutoStartRegisteredCommand));
        OnPropertyChanged(nameof(PortableEngine));
        OnPropertyChanged(nameof(PortablePrereq));
        OnPropertyChanged(nameof(PortableLicense));
        OnPropertyChanged(nameof(PortableWritableWarningText));
        OnPropertyChanged(nameof(RecoverySummary));
        OnPropertyChanged(nameof(RecoveryDetail));
        OnPropertyChanged(nameof(RecoveryRunOnceText));
        // ⚠️ 新增 VM 侧本地化文案时【必须加到这里】：
        //    走 {x:Bind} 的计算属性不在 loc 附加属性注册表里，
        //    P8 本地化审计的控件树腿扫不到它们 —— 漏了不会有任何提示，
        //    只会表现为"切英文后这一块还是中文"。
        OnPropertyChanged(nameof(Apex4GyroStateText));
    }

    private static int Clamp(int value, int min, int max)
        => value < min ? min : (value > max ? max : value);

    private void Save() => _settings.Save();
}
