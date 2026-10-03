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
/// 驱动管理 ViewModel：检测 USBip / HidHide / ViGEmBus 的安装状态，并提供提权安装入口。
///
/// <para>
/// <b>安全边界：</b>本页面的「刷新检测」是**只读**的（读注册表 + 查 WMI）；
/// 只有「安装 / 修复」按钮会触发提权，且必然弹一次 UAC。
/// 因此即便用户只是打开这个页面，也不会对系统造成任何改动。
/// </para>
/// </summary>
public partial class DriversViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_drivers.log";

    private readonly DriverManifestService _manifest;
    private readonly DriverDetectionService _detection;
    private readonly DriverInstallerService _installer;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;

    public ObservableCollection<DriverCardItem> Drivers { get; } = [];

    [ObservableProperty]
    public partial bool IsScanning { get; set; }

    [ObservableProperty]
    public partial string SummaryText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ElevatedText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool NeedsRestart { get; set; }

    public DriversViewModel(
        DriverManifestService manifest,
        DriverDetectionService detection,
        DriverInstallerService installer,
        ILocalizationService loc)
    {
        _manifest = manifest;
        _detection = detection;
        _installer = installer;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        _loc.LanguageChanged += (_, _) => OnUi(RefreshLocalization);

        ElevatedText = DriverDetectionService.IsElevated
            ? _loc.Get("Loc_DriversElevated")
            : _loc.Get("Loc_DriversNotElevated");

        _ = RefreshAsync();
    }

    /// <summary>重新检测全部驱动（只读）。</summary>
    [RelayCommand]
    private async Task RefreshAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        try
        {
            IReadOnlyList<DriverStatus> statuses = await Task.Run(() => _detection.DetectAll())
                .ConfigureAwait(true);
            ApplyStatuses(statuses);
            StatusMessage = string.Empty;
        }
        catch (Exception ex)
        {
            StatusMessage = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"驱动检测失败：{AppLog.Describe(ex)}");
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>安装 / 修复单个驱动（提权）。</summary>
    [RelayCommand]
    private async Task InstallAsync(DriverCardItem? item)
    {
        if (item is null || item.IsBusy) return;

        item.IsBusy = true;
        StatusMessage = _loc.Format("Loc_DriversInstalling", item.DisplayName);

        try
        {
            DriverInstallResult result = await _installer.InstallAsync(item.Info);

            StatusMessage = result.Success
                ? _loc.Get("Loc_DriversInstallDone")
                : result.Message;

            NeedsRestart = result.NeedsRestart;

            AppLog.Info(LogFileName,
                $"安装 {item.Info.Id} 结束：成功={result.Success} 退出码={result.ExitCode} 需重启={result.NeedsRestart}");
        }
        catch (Exception ex)
        {
            StatusMessage = AppLog.Describe(ex);
            AppLog.Warn(LogFileName, $"安装 {item.Info.Id} 异常：{AppLog.Describe(ex)}");
        }
        finally
        {
            item.IsBusy = false;
        }

        // 不论成败都重新检测一次 —— 用户要看到「现在真实是什么状态」
        await RefreshAsync();
    }

    private void ApplyStatuses(IReadOnlyList<DriverStatus> statuses)
    {
        Drivers.Clear();

        foreach (DriverStatus status in statuses)
        {
            Drivers.Add(new DriverCardItem(status.Info, status, _loc));
        }

        int ready = statuses.Count(x => x.IsHealthy);
        int required = statuses.Count(x => x.Info.IsRequired);

        SummaryText = _loc.Format("Loc_DriversSummary", ready, statuses.Count);

        NeedsRestart = statuses.Any(x => x.NeedsRestart);

        AppLog.Info(LogFileName, $"驱动检测完成：就绪 {ready}/{statuses.Count}（必需项 {required} 项）");
    }

    private void RefreshLocalization()
    {
        ElevatedText = DriverDetectionService.IsElevated
            ? _loc.Get("Loc_DriversElevated")
            : _loc.Get("Loc_DriversNotElevated");

        foreach (DriverCardItem item in Drivers)
        {
            item.RefreshLocalization();
        }

        ApplyStatuses([.. Drivers.Select(x => x.Status)]);
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

/// <summary>驱动状态卡片的展示模型。</summary>
public partial class DriverCardItem : ObservableObject
{
    private readonly ILocalizationService _loc;

    public DriverCardItem(DriverInfo info, DriverStatus status, ILocalizationService loc)
    {
        Info = info;
        Status = status;
        _loc = loc;
    }

    public DriverInfo Info { get; }

    public DriverStatus Status { get; }

    public string DisplayName => _loc.Get($"Loc_Driver_{Info.Id}");

    public string StateText => _loc.Get($"Loc_DriverState_{Status.State}");

    public string VersionText => string.IsNullOrWhiteSpace(Status.InstalledVersion)
        ? _loc.Format("Loc_DriverVersionExpected", Info.Version)
        : _loc.Format("Loc_DriverVersionInstalled", Status.InstalledVersion);

    public string ServicesText => Status.Services.Count == 0
        ? string.Empty
        : _loc.Format("Loc_DriverServices",
            string.Join(", ", Status.Services.Select(kv => $"{kv.Key}:{kv.Value}")));

    public string InstallerText
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Info.Installer)) return _loc.Get("Loc_DriverInstallerNone");
            if (!Status.InstallerPresent) return _loc.Get("Loc_DriverInstallerMissing");
            return Status.InstallerVerified
                ? _loc.Get("Loc_DriverInstallerVerified")
                : _loc.Get("Loc_DriverInstallerUnverified");
        }
    }

    public bool IsHealthy => Status.IsHealthy;

    public bool NeedsAction => Status.NeedsAction;

    public bool IsOptional => Info.Optional;

    public bool NeedsRestart => Status.NeedsRestart;

    /// <summary>按钮文案：没装叫「安装」，装了但不对叫「修复」。</summary>
    public string ActionText => Status.State == DriverState.NotInstalled
        ? _loc.Get("Loc_DriversInstall")
        : _loc.Get("Loc_DriversRepair");

    /// <summary>是否允许点安装（安装器在位且通过完整性校验）。</summary>
    public bool CanInstall => Status.NeedsAction && Status.InstallerPresent && Status.InstallerVerified;

    [ObservableProperty]
    public partial bool IsBusy { get; set; }

    public void RefreshLocalization()
    {
        OnPropertyChanged(nameof(DisplayName));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(VersionText));
        OnPropertyChanged(nameof(ServicesText));
        OnPropertyChanged(nameof(InstallerText));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(IsHealthy));
        OnPropertyChanged(nameof(NeedsAction));
        OnPropertyChanged(nameof(NeedsRestart));
    }
}
