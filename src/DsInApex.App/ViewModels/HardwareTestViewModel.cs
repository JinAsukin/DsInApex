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

    [RelayCommand]
    private Task TestTriggerAsync() => RunAsync(
        "test-rt", null, _loc.Get("Loc_HwResultTestRt"));

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
        Add("Loc_HwField_AdaptiveTriggers",
            identity.AdaptiveTriggers ? _loc.Get("Loc_HwYes") : _loc.Get("Loc_HwNo"));
    }

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
