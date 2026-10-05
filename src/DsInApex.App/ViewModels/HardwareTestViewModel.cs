using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;

namespace DsInApex.App.ViewModels;

/// <summary>
/// 一条展示用的结果行（标签 + 值）。
/// <para>用"行列表"而不是为每种报告写一套 UI：引擎报告有 8 种、
/// 字段上百个，逐种写模板既慢又容易漏。</para>
/// </summary>
public sealed record HardwareResultLine(string Label, string Value);

/// <summary>硬件测试页里的设备卡片。</summary>
public sealed class HardwareDeviceItem
{
    public required FlydigiDeviceInfo Device { get; init; }

    /// <summary>经过机型映射的显示名。</summary>
    public required string DisplayName { get; init; }

    public string VidPidText => $"VID:PID  {Device.VendorId}:{Device.ProductId}";

    public string UsageText => $"Usage page 0x{Device.UsagePage}  usage 0x{Device.Usage}";

    public string ReportsText =>
        Device.InputReportLength >= 0 && Device.OutputReportLength >= 0
            ? $"Reports  input={Device.InputReportLength} output={Device.OutputReportLength} bytes"
            : string.Empty;

    /// <summary>是否为 APEX 4 的厂商接口（VID 04B4 / PID 2412）。</summary>
    public bool IsApex4
        => string.Equals(Device.VendorId, "04B4", StringComparison.OrdinalIgnoreCase)
           && string.Equals(Device.ProductId, "2412", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// 硬件测试 ViewModel。
///
/// <para><b>安全边界（与驱动页同级）：</b></para>
/// <list type="bullet">
/// <item>只读命令（<c>list</c> / <c>identify</c> / <c>diagnose</c> /
/// <c>input-status</c> / <c>virtual-ds</c>）随时可跑；</item>
/// <item>写硬件的测试（<c>test-rt</c> / <c>test-rumble</c> / <c>apex4-port-test</c>）
/// 在<b>桥接会话活动期间一律禁用</b> —— 两者会抢同一个 HID 接口；</item>
/// <item>应急操作单独成区，其中 <c>stop-active-sessions</c> 与
/// <c>restore-controller-visibility</c> 会改变全局状态，界面必须加确认。</item>
/// </list>
/// </summary>
public partial class HardwareTestViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_hardware.log";

    private readonly HardwareTestService _hardware;
    private readonly EngineSessionManager _session;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;

    public ObservableCollection<HardwareDeviceItem> Devices { get; } = [];
    public ObservableCollection<HardwareResultLine> ResultLines { get; } = [];

    public HardwareTestViewModel(
        HardwareTestService hardware,
        EngineSessionManager session,
        ILocalizationService loc)
    {
        _hardware = hardware;
        _session = session;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _loc.LanguageChanged += (_, _) => OnUi(RefreshLocalization);
        _session.SessionStarted += (_, _) => OnUi(OnSessionStateChanged);
        _session.SessionStopped += _ => OnUi(OnSessionStateChanged);

        UpdateStatusLine();

        // 进页面就把设备信息拉一次：list 是纯枚举，identify 是只读身份交换
        _ = RefreshDeviceAsync();
    }

    // ═══════════════════════ 状态 ═══════════════════════

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string BusyText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusLine { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultTitle { get; set; } = string.Empty;

    /// <summary>解析失败提示：非空时界面要明确告诉用户"已降级为原始输出"。</summary>
    [ObservableProperty]
    public partial string ParseWarning { get; set; } = string.Empty;

    /// <summary>是否存在解析降级警告（供 InfoBar.IsOpen 绑定 —— 它是 bool，不能直接绑字符串）。</summary>
    public bool HasParseWarning => !string.IsNullOrWhiteSpace(ParseWarning);

    partial void OnParseWarningChanged(string value) => OnPropertyChanged(nameof(HasParseWarning));

    [ObservableProperty]
    public partial string RawOutput { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    /// <summary>设备识别结论（没有设备时为本地化的提示语）。</summary>
    [ObservableProperty]
    public partial string DeviceHeadline { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DeviceDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool DeviceFound { get; set; }

    /// <summary>输入监视 / 端口测试的采样秒数（界面上是 1–60 的滑杆）。</summary>
    [ObservableProperty]
    public partial double InputSecondsValue { get; set; } = 3;

    /// <summary>配置档切换的目标档位（1–4）。</summary>
    [ObservableProperty]
    public partial int ProfileTargetIndex { get; set; }

    /// <summary>引擎是否已定位。</summary>
    public bool EngineFound => _hardware.EngineAvailable;

    /// <summary>桥接会话是否正在运行。</summary>
    public bool SessionActive => _session.IsSessionActive;

    /// <summary>是否允许执行写硬件的测试。</summary>
    public bool CanRunHardwareTest => EngineFound && !SessionActive && !IsBusy;

    /// <summary>是否允许执行只读命令。</summary>
    public bool CanRunReadOnly => EngineFound && !IsBusy;

    /// <summary>
    /// 当前机型是否支持配置档切换测试。
    /// <para>
    /// ⚠️ 实测（2026-10-03）：<c>test-profile-switch</c> 对 APEX 4
    /// <b>直接拒绝</b>（"supports Apex 5 only"，退出码 3）。
    /// 因此只有确认识别为 APEX 5 时才展示这个按钮，避免用户点了才知道不支持。
    /// </para>
    /// </summary>
    public bool SupportsProfileSwitch => _identity?.IsApex5 == true;

    private ApexIdentity? _identity;

    partial void OnIsBusyChanged(bool value)
    {
        NotifyAvailabilityChanged();
    }

    private void NotifyAvailabilityChanged()
    {
        OnPropertyChanged(nameof(CanRunHardwareTest));
        OnPropertyChanged(nameof(CanRunReadOnly));
    }

    private void OnSessionStateChanged()
    {
        UpdateStatusLine();

        // SessionActive 与 CanRunHardwareTest 都是从会话状态算出来的只读属性，
        // 不会自己发通知 —— 必须手工触发，否则按钮的禁用状态不会跟着会话走。
        OnPropertyChanged(nameof(SessionActive));
        OnPropertyChanged(nameof(EngineFound));
        NotifyAvailabilityChanged();
    }

    private void UpdateStatusLine()
    {
        string engine = EngineFound
            ? _loc.Get("Loc_HwEngineReady")
            : _loc.Get("Loc_HwEngineMissing");

        string session = SessionActive
            ? _loc.Get("Loc_HwSessionRunning")
            : _loc.Get("Loc_HwSessionIdle");

        StatusLine = $"{engine} · {session}";
    }

    // ═══════════════════════ 只读命令 ═══════════════════════

    /// <summary>刷新设备识别（list + identify）。</summary>
    [RelayCommand]
    private async Task RefreshDeviceAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyText = _loc.Get("Loc_HwBusyListing");
        try
        {
            EngineCommandOutcome listOutcome = await _hardware.RunAsync("list");
            ApplyDeviceList(listOutcome);

            // 会话活动时 identify 会失败（设备被占用），跳过以免留下误导性结果
            if (!SessionActive && DeviceFound)
            {
                EngineCommandOutcome idOutcome = await _hardware.RunAsync("identify");
                ApplyIdentity(idOutcome);
                ApplyOutcomeToResultPanel(idOutcome, _loc.Get("Loc_HwResultIdentify"));
            }
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    /// <summary>HID 接口诊断（只读）。</summary>
    [RelayCommand]
    private Task DiagnoseAsync() => RunAsync(
        "diagnose", new EngineCommandOptions(Json: true), _loc.Get("Loc_HwResultDiagnose"));

    /// <summary>输入监视：采样若干秒，显示摇杆 / 扳机 / 按键。</summary>
    [RelayCommand]
    private Task InputMonitorAsync() => RunAsync(
        "input-status",
        new EngineCommandOptions(Json: true, Seconds: (int)InputSecondsValue),
        _loc.Get("Loc_HwResultInputStatus"));

    /// <summary>虚拟 DualSense 测试。</summary>
    [RelayCommand]
    private Task VirtualDsAsync() => RunAsync(
        "virtual-ds",
        new EngineCommandOptions(Json: true, Seconds: (int)InputSecondsValue),
        _loc.Get("Loc_HwResultVirtualDs"));

    /// <summary>XInput 槽位查询（只读，不需要 APEX 在位）。</summary>
    [RelayCommand]
    private Task XInputStatusAsync() => RunAsync(
        "xinput-status", null, _loc.Get("Loc_HwResultXInput"));

    /// <summary>测试报文预览（纯本地，无任何 HID 写入）。</summary>
    [RelayCommand]
    private Task DryRunAsync() => RunAsync(
        "dry-run", null, _loc.Get("Loc_HwResultDryRun"));

    // ═══════════════════════ 硬件测试（会真实驱动手柄） ═══════════════════════

    /// <summary>扳机测试的时长（秒）。引擎默认 2 秒，取 3 秒让手感更容易分辨。</summary>
    private const int TriggerTestSeconds = 3;

    /// <summary>
    /// 扳机强度档位索引（0–3，对应引擎 <c>--level 1..4</c>）。默认 1（即 level 2）。
    ///
    /// <para>
    /// 之所以要暴露给用户：引擎各档的力度差别很大（level 1 ≈ 30–40、
    /// level 4 ≈ 230–240），而默认的 level 2 相当轻 ——
    /// 「某一侧完全没反应」和「那一侧力度太轻感觉不到」是两回事，
    /// 不给档位就无法区分。
    /// </para>
    /// </summary>
    public int TriggerLevelIndex { get; set; } = 1;

    /// <summary>当前档位对应的引擎 <c>--level</c> 取值（1–4）。</summary>
    private int TriggerLevel => TriggerLevelIndex + 1;

    /// <summary>
    /// 左扳机自适应测试（<c>test-trigger --side lt</c>）。
    ///
    /// <para>
    /// ⚠️ 分侧测试是<b>上游 1.0.0 才具备的能力</b>（旧版只有 <c>test-rt</c>，
    /// 它固定驱动<b>右</b>扳机）。这正是"到底哪一侧坏"长期无法自证的原因 ——
    /// 硬件测试页以前只有一个按钮，点下去永远只动右边。
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task TestTriggerLeftAsync() => RunAsync(
        "test-trigger",
        new EngineCommandOptions(
            TriggerSide: "lt", Seconds: TriggerTestSeconds, TriggerLevel: TriggerLevel),
        _loc.Get("Loc_HwResultTestTrigger"));

    /// <summary>右扳机自适应测试（<c>test-trigger --side rt</c>）。</summary>
    [RelayCommand]
    private Task TestTriggerRightAsync() => RunAsync(
        "test-trigger",
        new EngineCommandOptions(
            TriggerSide: "rt", Seconds: TriggerTestSeconds, TriggerLevel: TriggerLevel),
        _loc.Get("Loc_HwResultTestTrigger"));

    /// <summary>双侧同时测试（<c>test-trigger --side both</c>）—— 用来做左右手感对照。</summary>
    [RelayCommand]
    private Task TestTriggerBothAsync() => RunAsync(
        "test-trigger",
        new EngineCommandOptions(
            TriggerSide: "both", Seconds: TriggerTestSeconds, TriggerLevel: TriggerLevel),
        _loc.Get("Loc_HwResultTestTrigger"));

    [RelayCommand]
    private Task TestRumbleAsync() => RunAsync(
        "test-rumble", null, _loc.Get("Loc_HwResultTestRumble"));

    [RelayCommand]
    private Task TestProfileSwitchAsync() => RunAsync(
        "test-profile-switch",
        new EngineCommandOptions(ProfileTarget: ProfileTargetIndex + 1),
        _loc.Get("Loc_HwResultProfileSwitch"));

    [RelayCommand]
    private Task PortTestAsync() => RunAsync(
        "apex4-port-test",
        new EngineCommandOptions(Seconds: (int)InputSecondsValue, Rumble: true, ForceAdapt: true),
        _loc.Get("Loc_HwResultPortTest"));

    // ═══════════════════════ 应急操作 ═══════════════════════

    /// <summary>清空 LT/RT 效果并停止震动（会写手柄，但只写"复位"）。</summary>
    [RelayCommand]
    private Task ClearEffectsAsync() => RunAsync(
        "clear", null, _loc.Get("Loc_HwResultClear"));

    /// <summary>停止所有活动会话（含其他程序持有的）。</summary>
    [RelayCommand]
    private Task StopSessionsAsync()
    {
        // 这个命令的效果就是"停会话"，因此不能等会话空闲（NeedsSessionIdle=false）
        return RunAsync("stop-active-sessions", null, _loc.Get("Loc_HwResultStopSessions"));
    }

    /// <summary>恢复被隐藏的手柄可见性并还原配置档。</summary>
    [RelayCommand]
    private Task RestoreVisibilityAsync() => RunAsync(
        "restore-controller-visibility", null, _loc.Get("Loc_HwResultRestoreVisibility"));

    // ═══════════════════════ 执行核心 ═══════════════════════

    private async Task RunAsync(string commandId, EngineCommandOptions? options, string title)
    {
        if (IsBusy) return;

        IsBusy = true;
        BusyText = _loc.Format("Loc_HwBusyRunning", commandId);
        try
        {
            EngineCommandOutcome outcome = await _hardware.RunAsync(commandId, options);
            ApplyOutcomeToResultPanel(outcome, title);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"{commandId} 执行异常：{AppLog.Describe(ex)}");
            ShowRaw(title, AppLog.Describe(ex), string.Empty);
        }
        finally
        {
            IsBusy = false;
            BusyText = string.Empty;
        }
    }

    // ═══════════════════════ 结果渲染 ═══════════════════════

    private void ApplyDeviceList(EngineCommandOutcome outcome)
    {
        Devices.Clear();

        if (outcome.Parsed is IReadOnlyList<FlydigiDeviceInfo> devices && devices.Count > 0)
        {
            foreach (FlydigiDeviceInfo device in devices)
            {
                Devices.Add(new HardwareDeviceItem
                {
                    Device = device,
                    DisplayName = MapProductName(device),
                });
            }

            DeviceFound = true;

            FlydigiDeviceInfo primary = devices[0];
            DeviceHeadline = MapProductName(primary);
            DeviceDetail = _loc.Format(
                "Loc_HwDeviceDetail", primary.VendorId, primary.ProductId,
                primary.InputReportLength, primary.OutputReportLength);

            // 身份态提示：详见 BuildIdentityHint 的注释。
            // 引擎的 identify 在两种身份下都报 "Apex 4"，只有这里能看出区别。
            string identityHint = BuildIdentityHint(primary);
            if (identityHint.Length > 0)
            {
                DeviceDetail += " ｜ " + identityHint;
            }
        }
        else
        {
            DeviceFound = false;
            DeviceHeadline = _loc.Get("Loc_HwNoDevice");
            DeviceDetail = _loc.Get("Loc_HwNoDeviceHint");
        }

        NotifyAvailabilityChanged();
    }

    private void ApplyIdentity(EngineCommandOutcome outcome)
    {
        if (outcome.Parsed is not ApexIdentity identity || identity.IsUnparsed)
        {
            return;
        }

        _identity = identity;
        DeviceHeadline = identity.ModelName;
        DeviceDetail = _loc.Format(
            "Loc_HwIdentityDetail",
            identity.Codename,
            identity.Firmware,
            LinkText(identity.Link));

        OnPropertyChanged(nameof(SupportsProfileSwitch));
    }

    private void ApplyOutcomeToResultPanel(EngineCommandOutcome outcome, string title)
    {
        ResultLines.Clear();

        if (outcome.Parsed is ApexIdentity identity)
        {
            FillIdentityLines(identity);
        }
        else if (outcome.Parsed is IReadOnlyList<FlydigiDeviceInfo> devices)
        {
            foreach (FlydigiDeviceInfo device in devices)
            {
                ResultLines.Add(new HardwareResultLine(
                    $"#{device.Index}", $"{MapProductName(device)}  {device.VendorId}:{device.ProductId}"));
            }
        }
        else if (outcome.Parsed is HidDiagnosticReport diagnose)
        {
            ResultLines.Add(new HardwareResultLine(
                _loc.Get("Loc_HwField_InterfaceCount"),
                diagnose.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)));
            ResultLines.Add(new HardwareResultLine(
                _loc.Get("Loc_HwField_Apex4Interfaces"),
                diagnose.Apex4Interfaces.Count.ToString(System.Globalization.CultureInfo.CurrentCulture)));

            foreach (HidDeviceDetail device in diagnose.Apex4Interfaces)
            {
                ResultLines.Add(new HardwareResultLine(
                    $"  {device.InterfaceNumber}", $"{device.Product}  {device.VendorIdHex}:{device.ProductIdHex}  " +
                                                 $"in={device.InputReportLength} out={device.OutputReportLength}"));
            }
        }
        else if (outcome.Parsed is InputStatusReport input)
        {
            FillInputStatusLines(input);
        }
        else if (outcome.Parsed is VirtualDsReport virtualDs)
        {
            FillVirtualDsLines(virtualDs);
        }
        else if (outcome.Parsed is IReadOnlyList<int> slots)
        {
            ResultLines.Add(new HardwareResultLine(
                _loc.Get("Loc_HwField_XInputSlots"),
                slots.Count == 0
                    ? _loc.Get("Loc_HwSummary_XInputNone")
                    : string.Join(", ", slots)));
        }

        // 结论行永远放最前面
        if (!string.IsNullOrWhiteSpace(outcome.Summary))
        {
            ResultLines.Insert(0, new HardwareResultLine(_loc.Get("Loc_HwField_Conclusion"), outcome.Summary));
        }

        if (!string.IsNullOrWhiteSpace(outcome.ParseError))
        {
            ParseWarning = _loc.Format("Loc_HwParseDegraded", outcome.ParseError);
        }
        else
        {
            ParseWarning = string.Empty;
        }

        ResultTitle = title;
        RawOutput = outcome.RawText.Trim();
        HasResult = true;
    }

    private void FillIdentityLines(ApexIdentity identity)
    {
        void Add(string key, string value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                ResultLines.Add(new HardwareResultLine(_loc.Get(key), value));
            }
        }

        Add("Loc_HwField_Model", identity.ModelName);
        Add("Loc_HwField_Codename", identity.Codename);
        Add("Loc_HwField_DeviceType", identity.DeviceType?.ToString() ?? string.Empty);
        Add("Loc_HwField_Firmware", identity.Firmware);
        Add("Loc_HwField_Link", LinkText(identity.Link));
        Add("Loc_HwField_Battery",
            identity.BatteryPercent is { } percent
                ? $"{percent}%" + (identity.IsCharging ? " " + _loc.Get("Loc_HwCharging") : string.Empty)
                : string.Empty);
        Add("Loc_HwField_AdaptiveTriggers", AdaptiveTriggerText(identity));

        // 降级态下引擎会随 Action: 行给出「怎么修」——原文照搬，别改写。
        if (identity.AdaptiveTriggerAction.Length > 0)
        {
            Add("Loc_HwField_Action", identity.AdaptiveTriggerAction);
        }
    }

    /// <summary>
    /// 把自适应扳机能力翻成界面文案。
    ///
    /// <para>
    /// 🔴 必须区分三态而不是布尔：引擎 1.0.0-beta.10 起（上游 issue #26）
    /// 降级 32 字节接口会报 <c>partial</c> —— 此时 <b>LT 与震动仍然可用，只有 RT 不生效</b>。
    /// 若沿用旧的 yes/no 展示，用户会得到"不支持"的错误结论，进而误以为整支手柄废了。
    /// </para>
    /// </summary>
    private string AdaptiveTriggerText(ApexIdentity identity) => identity.AdaptiveTriggerState switch
    {
        ApexTriggerCapability.Full => _loc.Get("Loc_HwAdaptiveFull"),
        ApexTriggerCapability.Partial => _loc.Get("Loc_HwAdaptivePartial"),
        ApexTriggerCapability.None => _loc.Get("Loc_HwNo"),
        // Unknown：引擎没打印该行（beta.9 及更早的旧引擎，或非 APEX 4 机型）
        _ => _loc.Get("Loc_HwAdaptiveUnknown"),
    };

    private void FillInputStatusLines(InputStatusReport input)
    {
        void Add(string key, string value)
            => ResultLines.Add(new HardwareResultLine(_loc.Get(key), value));

        Add("Loc_HwField_Backend", input.Backend);
        Add("Loc_HwField_EventDriven", input.EventDriven ? _loc.Get("Loc_HwYes") : _loc.Get("Loc_HwNo"));
        Add("Loc_HwField_ReceivedState", input.ReceivedState ? _loc.Get("Loc_HwYes") : _loc.Get("Loc_HwNo"));
        Add("Loc_HwField_Reports", input.Reports.ToString());
        Add("Loc_HwField_StateChanges", input.StateChanges.ToString());
        Add("Loc_HwField_Timeouts", input.Timeouts.ToString());
        Add("Loc_HwField_ParseFailures", input.ParseFailures.ToString());
        Add("Loc_HwField_LeftStick", $"{input.LeftStickX}, {input.LeftStickY}");
        Add("Loc_HwField_RightStick", $"{input.RightStickX}, {input.RightStickY}");
        Add("Loc_HwField_Triggers", $"{input.LeftTrigger}, {input.RightTrigger}");
        Add("Loc_HwField_DPad", $"{input.DPadName} ({input.DPad})");
        Add("Loc_HwField_SeenDPad", $"{input.SeenDPadDirections} ({input.SeenDPad})");
        Add("Loc_HwField_Buttons",
            input.ButtonNames.Count == 0 ? _loc.Get("Loc_HwNone") : string.Join(", ", input.ButtonNames));

        if (!string.IsNullOrWhiteSpace(input.Warning))
        {
            Add("Loc_HwField_Warning", input.Warning);
        }
    }

    private void FillVirtualDsLines(VirtualDsReport virtualDs)
    {
        void Add(string key, string value)
            => ResultLines.Add(new HardwareResultLine(_loc.Get(key), value));

        Add("Loc_HwField_VirtualConnected",
            virtualDs.Connected ? _loc.Get("Loc_HwYes") : _loc.Get("Loc_HwNo"));
        Add("Loc_HwField_Backend", virtualDs.Backend);
        Add("Loc_HwField_BackendVersion", virtualDs.BackendVersion);
        Add("Loc_HwField_DsFirmware",
            virtualDs.DualSenseFirmware + (virtualDs.FirmwareCurrent ? " (" + _loc.Get("Loc_HwLatest") + ")" : string.Empty));
        Add("Loc_HwField_InputMode", virtualDs.InputMode);
        Add("Loc_HwField_ApexRouting", virtualDs.ApexRouting);
        Add("Loc_HwField_OutputReports", virtualDs.OutputReports.ToString());
        Add("Loc_HwField_TriggerReports", virtualDs.TriggerReports.ToString());
        Add("Loc_HwField_RumbleReports", virtualDs.RumbleReports.ToString());
        Add("Loc_HwField_AudioHaptics", virtualDs.AudioHapticsFrames.ToString());
        Add("Loc_HwField_MalformedFrames", virtualDs.MalformedFrames.ToString());
        Add("Loc_HwField_UnknownFrames", virtualDs.UnknownFrames.ToString());
    }

    private void ShowRaw(string title, string text, string parseWarning)
    {
        ResultLines.Clear();
        ResultTitle = title;
        RawOutput = text.Trim();
        ParseWarning = parseWarning;
        HasResult = true;
    }

    // ═══════════════════════ 辅助 ═══════════════════════

    /// <summary>
    /// 机型名称映射。
    ///
    /// <para>
    /// <b>为什么必须做：</b>APEX 4 的四个 HID 接口上都写着旧版固件遗留的产品字符串
    /// <c>Flydigi VADER3</c>（VID_04B4 是 Cypress 的通用 VID）。
    /// 直接显示引擎返回的名字会让用户以为"接错手柄了"。
    /// 判定依据是 VID:PID —— 只有确认是 <c>04B4:2412</c> 时才改写成 APEX 4。
    /// </para>
    /// </summary>
    private string MapProductName(FlydigiDeviceInfo device)
    {
        bool isApex4VidPid =
            string.Equals(device.VendorId, "04B4", StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.ProductId, "2412", StringComparison.OrdinalIgnoreCase);

        if (isApex4VidPid && device.Product.Contains("VADER3", StringComparison.OrdinalIgnoreCase))
        {
            return _loc.Format("Loc_HwLegacyAlias", "APEX 4", device.Product);
        }

        return device.Product;
    }

    /// <summary>
    /// 生成「手柄身份态」提示。
    ///
    /// <para>
    /// 🔴 实测结论（2026-10-03，APEX 4 / firmware 0x6837）：
    /// 同一台 APEX 4 会以<b>两种不同的 USB 身份</b>出现 ——
    /// <b>VID:PID 都还是 04B4:2412，但产品名与输出报告长度不同</b>：
    /// <list type="bullet">
    /// <item><c>Flydigi APEX 4</c>（输出报告 <b>64</b> 字节、<c>Connection: wired</c>）
    /// —— 左右扳机的 FORCEADAPT <b>都可用</b>；</item>
    /// <item><c>Flydigi VADER3</c>（引擎侧旧别名；浏览器 WebHID 常显示为
    /// <c>Flydigi Direwolf 3</c>，输出报告 <b>32</b> 字节、<c>Connection: dongle</c>）
    /// —— <b>仅 LT 有效，RT 的自适应不生效</b>。</item>
    /// </list>
    /// 触发条件在物理连接上：<b>接收器在位的同时用有线接入</b>（或先有线再插接收器）
    /// 才进入完整身份；单独插拔、以及桥接启停引起的重新枚举，都会退回降级身份。
    /// </para>
    ///
    /// <para>
    /// ⚠️ 之所以必须写在界面上：引擎 <c>identify</c> 两种情况下都返回 "Apex 4"
    /// （它只看 <c>deviceType == 84</c>），<b>机型名看不出任何区别</b>。
    /// 唯一能区分的是 HID 产品名与输出报告长度 —— 而这两项恰好都在
    /// <see cref="FlydigiDeviceInfo"/> 里，所以这是让用户自行判断的唯一手段。
    /// </para>
    /// </summary>
    private string BuildIdentityHint(FlydigiDeviceInfo device)
    {
        bool isApex4VidPid =
            string.Equals(device.VendorId, "04B4", StringComparison.OrdinalIgnoreCase)
            && string.Equals(device.ProductId, "2412", StringComparison.OrdinalIgnoreCase);

        if (!isApex4VidPid)
        {
            return string.Empty;
        }

        // "Flydigi APEX 4" 才是完整身份；VADER3 / Direwolf 3 属于降级身份
        bool fullIdentity = device.Product.Contains("APEX 4", StringComparison.OrdinalIgnoreCase);

        return fullIdentity
            ? _loc.Get("Loc_HwIdentityFull")
            : _loc.Get("Loc_HwIdentityDegraded");
    }

    private string LinkText(LinkMode link) => _loc.Get(link switch
    {
        LinkMode.Wired => "Loc_Link_Wired",
        LinkMode.Dongle => "Loc_Link_Dongle",
        LinkMode.Bluetooth => "Loc_Link_Bluetooth",
        _ => "Loc_Link_Unknown",
    });

    private void RefreshLocalization()
    {
        UpdateStatusLine();
        OnPropertyChanged(nameof(DeviceHeadline));
        OnPropertyChanged(nameof(DeviceDetail));
    }

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
