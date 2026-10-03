using System.Collections.ObjectModel;
using System.Diagnostics;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using DsInApex.Core.Localization;
using DsInApex.Core.Logging;
using DsInApex.Core.Models;
using DsInApex.Core.Services;
using Microsoft.UI.Dispatching;

namespace DsInApex.App.ViewModels;

/// <summary>诊断步骤的展示项。</summary>
public sealed record DiagnosticStepItem(string Label, string Detail, bool Success, string Glyph);

/// <summary>
/// 诊断面板 ViewModel。
///
/// <para>
/// 把上游 1127 行 PowerShell 脚本的能力收成"填几个选项 → 点一下 → 拿到 ZIP"。
/// 收集<b>全程只读</b>，唯一的写动作是往临时目录落文件和打包 ZIP。
/// </para>
///
/// <para>
/// 与硬件测试页的分工：硬件测试页做<b>单点验证</b>（这个扳机好不好使），
/// 诊断页做<b>整体取证</b>（环境到底什么样，好发给别人看）。
/// </para>
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private const string LogFileName = "dsinapex_diagnostics.log";

    private readonly DiagnosticsCollectorService _collector;
    private readonly ILocalizationService _loc;
    private readonly DispatcherQueue? _dispatcher;
    private readonly IProgress<string> _progress;

    private CancellationTokenSource? _cts;

    public ObservableCollection<string> LogLines { get; } = [];
    public ObservableCollection<DiagnosticStepItem> Steps { get; } = [];
    public ObservableCollection<string> Warnings { get; } = [];

    public DiagnosticsViewModel(DiagnosticsCollectorService collector, ILocalizationService loc)
    {
        _collector = collector;
        _loc = loc;
        _dispatcher = DispatcherQueue.GetForCurrentThread();

        // 在 UI 线程构造 → 回调自动回到 UI 线程
        _progress = new Progress<string>(line => LogLines.Add(line));

        OutputDirectory = DefaultOutputDirectory();

        _loc.LanguageChanged += (_, _) => OnUi(RefreshLocalization);
    }

    // ═══════════════════════ 选项 ═══════════════════════

    /// <summary>ZIP 输出目录（可手工编辑，默认桌面）。</summary>
    [ObservableProperty]
    public partial string OutputDirectory { get; set; } = string.Empty;

    /// <summary>XInput 采样秒数。</summary>
    [ObservableProperty]
    public partial double InputSampleSeconds { get; set; } = 8;

    /// <summary>跳过交互式采样（仍会枚举槽位）。</summary>
    [ObservableProperty]
    public partial bool SkipInputSample { get; set; }

    /// <summary>保留未压缩的中间文件（排查用）。</summary>
    [ObservableProperty]
    public partial bool KeepWorkingDirectory { get; set; }

    // ═══════════════════════ 状态 ═══════════════════════

    [ObservableProperty]
    public partial bool IsCollecting { get; set; }

    [ObservableProperty]
    public partial string ProgressText { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasResult { get; set; }

    [ObservableProperty]
    public partial string ArchivePath { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultHeadline { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ResultDetail { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool HasSteps { get; set; }

    public bool HasWarnings => Warnings.Count > 0;

    public bool CanCollect => !IsCollecting;

    partial void OnIsCollectingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanCollect));
    }

    // ═══════════════════════ 命令 ═══════════════════════

    /// <summary>一键收集诊断信息并导出 ZIP。</summary>
    [RelayCommand]
    private async Task CollectAsync()
    {
        if (IsCollecting) return;

        IsCollecting = true;
        LogLines.Clear();
        Steps.Clear();
        Warnings.Clear();
        HasResult = false;
        OnPropertyChanged(nameof(HasWarnings));
        ProgressText = _loc.Get("Loc_DiagCollecting");

        _cts = new CancellationTokenSource();

        var options = new DiagnosticCollectionOptions(
            OutputDirectory: OutputDirectory,
            InputSampleSeconds: (int)InputSampleSeconds,
            SkipInputSample: SkipInputSample,
            KeepWorkingDirectory: KeepWorkingDirectory);

        try
        {
            DiagnosticCollectionResult result = await _collector.CollectAsync(options, _progress, _cts.Token);
            ApplyResult(result);
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"诊断收集异常：{AppLog.Describe(ex)}");
            ResultHeadline = _loc.Get("Loc_DiagFailed");
            ResultDetail = AppLog.Describe(ex);
            HasResult = true;
        }
        finally
        {
            IsCollecting = false;
            ProgressText = string.Empty;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>中止收集。</summary>
    [RelayCommand]
    private void Cancel()
    {
        try
        {
            _cts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 已结束，忽略
        }
    }

    /// <summary>在资源管理器中打开输出目录。</summary>
    [RelayCommand]
    private void OpenOutputDirectory()
    {
        string target = OutputDirectory;

        // 收集成功后优先打开 ZIP 所在目录（用户可能改过路径）
        if (!string.IsNullOrWhiteSpace(ArchivePath))
        {
            string? directory = Path.GetDirectoryName(ArchivePath);
            if (!string.IsNullOrWhiteSpace(directory)) target = directory;
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(target) && Directory.Exists(target))
            {
                Process.Start(new ProcessStartInfo("explorer.exe", target) { UseShellExecute = true });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"打开输出目录失败：{AppLog.Describe(ex)}");
        }
    }

    /// <summary>选中 ZIP 文件（在资源管理器中高亮它）。</summary>
    [RelayCommand]
    private void RevealArchive()
    {
        try
        {
            if (File.Exists(ArchivePath))
            {
                // /select 让资源管理器定位到具体文件
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{ArchivePath}\"")
                {
                    UseShellExecute = true,
                });
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn(LogFileName, $"定位压缩包失败：{AppLog.Describe(ex)}");
        }
    }

    // ═══════════════════════ 结果处理 ═══════════════════════

    private void ApplyResult(DiagnosticCollectionResult result)
    {
        if (result.Summary is { } summary)
        {
            foreach (DiagnosticStep step in summary.Steps)
            {
                Steps.Add(new DiagnosticStepItem(
                    Label: _loc.Get($"Loc_DiagStep_{step.Name}"),
                    Detail: step.Detail,
                    Success: step.Success,
                    Glyph: step.Success ? "\uE73E" : "\uE711"));
            }

            HasSteps = Steps.Count > 0;

            foreach (string warning in summary.Warnings)
            {
                Warnings.Add(warning);
            }
        }

        OnPropertyChanged(nameof(HasWarnings));

        if (result.Success)
        {
            ResultHeadline = _loc.Get("Loc_DiagSuccess");
            ArchivePath = result.ArchivePath ?? string.Empty;
            ResultDetail = string.IsNullOrWhiteSpace(ArchivePath)
                ? string.Empty
                : _loc.Format("Loc_DiagArchivePath", ArchivePath);
        }
        else
        {
            ResultHeadline = _loc.Get("Loc_DiagFailed");
            ArchivePath = string.Empty;
            ResultDetail = result.Error
                + Environment.NewLine
                + _loc.Format("Loc_DiagPartialKept", result.WorkingDirectory);
        }

        HasResult = true;
    }

    private static string DefaultOutputDirectory()
    {
        try
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            if (!string.IsNullOrWhiteSpace(desktop)) return desktop;
        }
        catch
        {
            // 取桌面路径失败时退回临时目录
        }

        return Path.GetTempPath();
    }

    private void RefreshLocalization()
    {
        // 步骤标签是本地化文案，语言切换时重建一次
        if (Steps.Count == 0) return;

        var snapshot = Steps.ToList();
        Steps.Clear();
        foreach (DiagnosticStepItem item in snapshot)
        {
            Steps.Add(item);
        }
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
