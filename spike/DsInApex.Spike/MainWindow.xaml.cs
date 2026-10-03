using System.Runtime.InteropServices;
using System.Text;
using DsInApex.Spike.Localization;
using DsInApex.Spike.Services;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.ApplicationModel;

namespace DsInApex.Spike;

public sealed partial class MainWindow : Window
{
    /// <summary>
    /// 初始化完成的护栏。RadioButton.Checked 会在 XAML 解析阶段抢先触发，
    /// 此时控件树尚未就绪，必须屏蔽 —— 上游 MainWindow 同样有 isInitialized 护栏。
    /// </summary>
    private bool _ready;

    private EngineRunner? _engine;
    private string? _lastEngineSummary;
    private readonly List<string> _vsHistory = new();

    public MainWindow()
    {
        InitializeComponent();

        PopulateEnvironmentInfo();
        SetupTrayIcon();
        SetupEngine();

        LocalizationManager.LanguageChanged += OnLanguageChanged;
        Closed += OnWindowClosed;

        // ── S5：关闭窗口不退出，隐藏到托盘 ──
        AppWindow.Closing += OnAppWindowClosing;

        SyncLanguageSelection();     // 必须在 _ready 置位之前，否则会误触发一次切换
        UpdateFormattedSamples();

        _ready = true;
        UpdateRefreshStats();
        WriteProbe("init");

        // 模板应用完成后才能 FindName 到模板内元素
        VsToggle.Loaded += (_, _) => CaptureVsState("initial");

        RunAutoTestIfRequested();
    }

    // ────────────────────────── S2：语言热切换 ──────────────────────────

    private void OnLanguageChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;

        string target = RadEn.IsChecked == true ? Langs.En : Langs.ZhCN;
        if (string.Equals(target, LocalizationManager.CurrentLanguage, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        LocalizationManager.SetLanguage(target);
    }

    private void OnLanguageChanged()
    {
        // 附加属性绑定的文案由 Localize.RefreshAll() 负责刷新；
        // 这里只处理"代码侧赋值"的内容（带参数的格式化文案等）。
        UpdateFormattedSamples();
        UpdateRefreshStats();
        WriteProbe("language-changed");
    }

    private void OnWindowClosed(object sender, WindowEventArgs args)
    {
        LocalizationManager.LanguageChanged -= OnLanguageChanged;
    }

    private void SyncLanguageSelection()
    {
        bool isZh = !string.Equals(LocalizationManager.CurrentLanguage, Langs.En,
                                   StringComparison.OrdinalIgnoreCase);
        if (isZh) RadZh.IsChecked = true;
        else RadEn.IsChecked = true;
    }

    private void UpdateFormattedSamples()
    {
        FormattedSampleText.Text = LocalizationManager.Format("Loc_MsgExcluded", "赛博朋克 2077");
    }

    private void UpdateRefreshStats()
    {
        RefreshStatsText.Text =
            $"✔ 当前语言 {LocalizationManager.CurrentLanguage}　|　" +
            $"刷新次数 {Localize.RefreshCount}　|　" +
            $"最近耗时 {Localize.LastRefreshMs:F2} ms　|　" +
            $"注册元素 {Localize.RegisteredCount} 个";
    }

    // ────────────────────────── S3：系统托盘 ──────────────────────────

    private void SetupTrayIcon()
    {
        try
        {
            TrayIcon.ForceCreate();

            string iconPath = System.IO.Path.Combine(
                AppContext.BaseDirectory, "Assets", "app.ico");
            int menuItems = (TrayIcon.ContextFlyout as MenuFlyout)?.Items.Count ?? 0;

            TrayInfoText.Text =
                $"托盘图标     : {(TrayIcon.IsCreated ? "已创建 ✅" : "未创建 ❌")}　|　" +
                $"菜单项 {menuItems} 个　|　" +
                $"图标文件存在 {System.IO.File.Exists(iconPath)}";
        }
        catch (Exception ex)
        {
            TrayInfoText.Text = $"托盘初始化异常: {ex.GetType().Name} - {ex.Message}";
        }
    }

    private void OnTrayOpenClick(object sender, RoutedEventArgs e)
    {
        try
        {
            AppWindow.Show();
            Activate();
        }
        catch
        {
            // Spike 阶段忽略
        }
    }

    private void OnTrayLanguageClick(object sender, RoutedEventArgs e)
    {
        bool isEn = string.Equals(LocalizationManager.CurrentLanguage, Langs.En,
                                  StringComparison.OrdinalIgnoreCase);
        LocalizationManager.SetLanguage(isEn ? Langs.ZhCN : Langs.En);
        SyncLanguageSelection();
    }

    private void OnTrayExitClick(object sender, RoutedEventArgs e)
    {
        WriteProbe("tray-exit");
        Application.Current.Exit();
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        // 拦截关闭：隐藏到托盘而不是退出（S5）
        args.Cancel = true;
        sender.Hide();
        WriteProbe("minimized-to-tray");
    }

    // ────────────────────────── S4：引擎调用 ──────────────────────────

    /// <summary>
    /// 定位引擎可执行文件：环境变量优先，其次从应用目录向上回溯找仓库根的 vendor/。
    /// P1 会替换为正式的 InstallLocator（读注册表安装路径）。
    /// </summary>
    private static string? LocateEngine()
    {
        string? env = Environment.GetEnvironmentVariable("DSINAPEX_ENGINE_PATH");
        if (!string.IsNullOrWhiteSpace(env) && System.IO.File.Exists(env))
        {
            return env;
        }

        DirectoryInfo? dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            string vendor = System.IO.Path.Combine(dir.FullName, "vendor");
            if (System.IO.Directory.Exists(vendor))
            {
                try
                {
                    string? hit = System.IO.Directory
                        .EnumerateFiles(vendor, "ApexSenseBridge.exe", SearchOption.AllDirectories)
                        .FirstOrDefault();
                    if (hit is not null) return hit;
                }
                catch
                {
                    // 目录不可读时继续向上找
                }
            }
            dir = dir.Parent;
        }

        return null;
    }

    private void SetupEngine()
    {
        string? path = LocateEngine();
        if (path is null)
        {
            EnginePathText.Text = "❌ 未找到引擎可执行文件（可用环境变量 DSINAPEX_ENGINE_PATH 指定）";
            BtnRunList.IsEnabled = false;
            BtnRunIdentify.IsEnabled = false;
            BtnRunDiagnose.IsEnabled = false;
            return;
        }

        _engine = new EngineRunner(path);
        EnginePathText.Text =
            $"引擎路径 : {path}{Environment.NewLine}" +
            $"文件存在 : {(_engine.Exists ? "是 ✅" : "否 ❌")}";
    }

    private async void OnRunListClick(object sender, RoutedEventArgs e)
        => await RunEngineCommandAsync("list", 20_000);

    private async void OnRunIdentifyClick(object sender, RoutedEventArgs e)
        => await RunEngineCommandAsync("identify", 25_000);

    private async void OnRunDiagnoseClick(object sender, RoutedEventArgs e)
        => await RunEngineCommandAsync("diagnose --json", 90_000);

    private async Task RunEngineCommandAsync(string command, int timeoutMs)
    {
        if (_engine is null) return;

        BtnRunList.IsEnabled = false;
        BtnRunIdentify.IsEnabled = false;
        BtnRunDiagnose.IsEnabled = false;
        EngineResultText.Text = $"正在执行 {command} ...";

        try
        {
            EngineResult result = await _engine.RunAsync(command, timeoutMs);

            var sb = new StringBuilder();
            sb.AppendLine($"命令     : {result.Command}");
            sb.AppendLine($"退出码   : {result.ExitCode}　耗时 {result.Duration.TotalMilliseconds:F0} ms　超时 {(result.TimedOut ? "是" : "否")}");

            if (!string.IsNullOrWhiteSpace(result.StandardOutput))
            {
                sb.AppendLine("── stdout ──");
                sb.AppendLine(result.StandardOutput.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(result.StandardError))
            {
                sb.AppendLine("── stderr ──");
                sb.AppendLine(result.StandardError.TrimEnd());
            }

            sb.AppendLine();
            sb.AppendLine("── 解析层 ──");

            if (command.StartsWith("list", StringComparison.Ordinal))
            {
                if (EngineOutputParser.HasNoDevice(result.StandardOutput))
                {
                    sb.AppendLine("引擎报告未找到设备（注意：退出码 2，不是 0）。");
                    sb.AppendLine("→ 解析层按设计降级：展示原始文本，不抛异常。");
                }
                else
                {
                    IReadOnlyList<Models.FlydigiDeviceInfo> devices =
                        EngineOutputParser.ParseDeviceList(result.StandardOutput);

                    if (devices.Count == 0)
                    {
                        sb.AppendLine("⚠ 未能解析出结构化条目 → 降级为显示原始文本（设计如此）。");
                    }
                    else
                    {
                        sb.AppendLine($"✔ 解析出 {devices.Count} 个设备：");
                        foreach (Models.FlydigiDeviceInfo d in devices)
                        {
                            sb.AppendLine($"   [{d.Index}] {d.Product}");
                            sb.AppendLine($"         VID:PID {d.VendorId}:{d.ProductId}　usage {d.UsagePage}/{d.Usage}　" +
                                          $"reports in={d.InputReportLength} out={d.OutputReportLength}");
                        }
                    }
                }
            }
            else if (command.Contains("--json", StringComparison.Ordinal))
            {
                sb.AppendLine("该命令原生支持 --json，P1 可直接用 System.Text.Json 反序列化，无需正则。");
            }
            else
            {
                sb.AppendLine("（本命令无结构化解析，仅展示原文）");
            }

            _lastEngineSummary = sb.ToString();
            EngineResultText.Text = _lastEngineSummary;
            WriteProbe($"engine:{command}");
        }
        catch (Exception ex)
        {
            EngineResultText.Text = $"引擎调用异常: {ex.GetType().Name} - {ex.Message}";
        }
        finally
        {
            BtnRunList.IsEnabled = true;
            BtnRunIdentify.IsEnabled = true;
            BtnRunDiagnose.IsEnabled = true;
        }
    }

    // ────────────────────────── S6：文件选择器 ──────────────────────────

    private async void OnPickFileClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FileOpenPicker
            {
                ViewMode = Windows.Storage.Pickers.PickerViewMode.List,
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");

            // unpackaged 模式下必须显式关联窗口句柄，否则 COMException
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var file = await picker.PickSingleFileAsync();
            PickerResultText.Text = file is null
                ? "✔ FilePicker 正常弹出，用户取消未选（说明 API 链路已通）"
                : $"✔ 已选择: {file.Path}";
            WriteProbe("picker-file");
        }
        catch (Exception ex)
        {
            PickerResultText.Text = $"❌ FilePicker 失败: {ex.GetType().Name} - {ex.Message}";
            WriteProbe("picker-file-failed");
        }
    }

    private async void OnPickFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Windows.Storage.Pickers.FolderPicker
            {
                SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.Desktop,
            };
            picker.FileTypeFilter.Add("*");

            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            var folder = await picker.PickSingleFolderAsync();
            PickerResultText.Text = folder is null
                ? "✔ FolderPicker 正常弹出，用户取消未选"
                : $"✔ 已选择文件夹: {folder.Path}";
            WriteProbe("picker-folder");
        }
        catch (Exception ex)
        {
            PickerResultText.Text = $"❌ FolderPicker 失败: {ex.GetType().Name} - {ex.Message}";
            WriteProbe("picker-folder-failed");
        }
    }

    // ────────────────────────── 自动化验证 ──────────────────────────

    // ────────────────────────── P1 障碍：VSM 样板验证 ──────────────────────────

    /// <summary>
    /// 读取改写后的 ControlTemplate 内元素的【实际属性值】。
    ///
    /// 为什么必须这样验证：Trigger→VSM 改写最容易出现"编译通过但状态不生效"
    /// （状态组名字写错、Target 路径写错都会静默失败）。只有看到 Track/Thumb
    /// 的真实属性值随 CheckStates 变化，才能判定改写真的可用。
    /// </summary>
    private void CaptureVsState(string phase)
    {
        try
        {
            // ⚠️ WinUI 3 的 ControlTemplate 没有 FindName（WPF 有），
            // 模板内元素只能靠遍历可视化树拿到。这本身也是一个迁移坑点。
            var track = FindInVisualTree<Border>(VsToggle, "Track");
            var thumb = FindInVisualTree<Ellipse>(VsToggle, "Thumb");

            if (track is null || thumb is null)
            {
                VsProbeText.Text = $"[{phase}] ⚠ 模板内元素未找到（模板可能尚未应用）";
                return;
            }

            string block =
                $"[{phase}] IsChecked={VsToggle.IsChecked}{Environment.NewLine}" +
                $"    Track.Background      = {BrushToString(track.Background)}{Environment.NewLine}" +
                $"    Track.BorderBrush     = {BrushToString(track.BorderBrush)}{Environment.NewLine}" +
                $"    Track.Opacity         = {track.Opacity:F2}{Environment.NewLine}" +
                $"    Thumb.HorizontalAlign = {thumb.HorizontalAlignment}{Environment.NewLine}" +
                $"    Thumb.Fill            = {BrushToString(thumb.Fill)}";

            _vsHistory.Add(block);
            VsProbeText.Text = block;
            VsStateText.Text = $"IsChecked = {VsToggle.IsChecked}";
        }
        catch (Exception ex)
        {
            VsProbeText.Text = $"VSM 采集异常: {ex.GetType().Name} - {ex.Message}";
        }
    }

    private static string BrushToString(Brush? brush)
        => brush is SolidColorBrush scb ? scb.Color.ToString() : brush?.GetType().Name ?? "(null)";

    /// <summary>
    /// 在可视化树中按名称查找元素（WinUI 3 替代 WPF 的 ControlTemplate.FindName）。
    /// </summary>
    private static T? FindInVisualTree<T>(DependencyObject root, string name)
        where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed && typed.Name == name)
            {
                return typed;
            }

            T? nested = FindInVisualTree<T>(child, name);
            if (nested is not null) return nested;
        }
        return null;
    }

    /// <summary>
    /// 自动化验证开关：设置环境变量 DSINAPEX_SPIKE_AUTOTEST=1 后，
    /// 应用启动会按时间线自动切换语言，无需人工点击，便于脚本取证。
    /// </summary>
    private async void RunAutoTestIfRequested()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("DSINAPEX_SPIKE_AUTOTEST"),
                "1", StringComparison.Ordinal))
        {
            return;
        }

        try
        {
            await Task.Delay(2500);
            LocalizationManager.SetLanguage(Langs.En);      // 中文 → English

            await Task.Delay(2500);
            LocalizationManager.SetLanguage(Langs.ZhCN);    // English → 中文（切回）

            await Task.Delay(1500);
            if (_engine is not null)
            {
                await RunEngineCommandAsync("list", 20_000);
            }

            // ── VSM 样板验证：切换 CheckBox，观察模板内元素的【实际属性值】是否随之变化 ──
            VsToggle.IsChecked = true;
            await Task.Delay(400);
            CaptureVsState("checked");

            VsToggle.IsChecked = false;
            await Task.Delay(400);
            CaptureVsState("unchecked");

            // ── S6 预检：unpackaged 模式下 FilePicker 的成败关键在于
            //    InitializeWithWindow 能否把 picker 关联到窗口句柄。
            //    这里只验证关联本身不抛异常，实际弹窗仍需人工点击。 ──
            try
            {
                var probePicker = new Windows.Storage.Pickers.FileOpenPicker();
                probePicker.FileTypeFilter.Add("*");
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(probePicker, hwnd);
                PickerResultText.Text = "✔ FilePicker InitializeWithWindow 成功（未实际弹出，需人工点击按钮验证）";
            }
            catch (Exception ex)
            {
                PickerResultText.Text = $"❌ FilePicker 关联失败: {ex.GetType().Name} - {ex.Message}";
            }
            WriteProbe("picker-precheck");

            await Task.Delay(500);
            WriteProbe("autotest-complete");
        }
        catch
        {
            // Spike 阶段不处理异常
        }
    }

    // ────────────────────────── 取证探针 ──────────────────────────

    private void WriteProbe(string phase)
    {
        try
        {
            var sb = new StringBuilder();
            sb.AppendLine($"# {DateTime.Now:HH:mm:ss.fff}  phase={phase}");
            sb.AppendLine($"lang={LocalizationManager.CurrentLanguage}");
            sb.AppendLine($"refreshCount={Localize.RefreshCount}");
            sb.AppendLine($"lastRefreshMs={Localize.LastRefreshMs:F3}");
            sb.AppendLine($"registered={Localize.RegisteredCount}");
            sb.AppendLine($"trayCreated={TrayIcon.IsCreated}");
            sb.AppendLine("--- applied values ---");

            foreach (string line in Localize.DumpApplied())
            {
                sb.AppendLine(line);
            }

            if (_lastEngineSummary is not null)
            {
                sb.AppendLine("--- engine ---");
                sb.AppendLine(_lastEngineSummary);
            }

            if (_vsHistory.Count > 0)
            {
                sb.AppendLine("--- vsm (Trigger → VisualStateManager 样板) ---");
                foreach (string block in _vsHistory)
                {
                    sb.AppendLine(block);
                }
            }

            if (!string.IsNullOrEmpty(PickerResultText.Text))
            {
                sb.AppendLine("--- picker (S6) ---");
                sb.AppendLine(PickerResultText.Text);
            }

            string temp = System.IO.Path.GetTempPath();
            string snapshot = sb.ToString();

            System.IO.File.WriteAllText(
                System.IO.Path.Combine(temp, "dsinapex_s2_probe.txt"), snapshot);

            System.IO.File.AppendAllText(
                System.IO.Path.Combine(temp, "dsinapex_s2_history.txt"),
                snapshot + "========================================" + Environment.NewLine);
        }
        catch
        {
            // 探针失败不影响界面
        }
    }

    // ────────────────────────── S1：环境自检 ──────────────────────────

    private void PopulateEnvironmentInfo()
    {
        string packageId;
        try
        {
            packageId = Package.Current.Id.Name;
        }
        catch (Exception ex)
        {
            packageId = $"（未打包，符合预期）{ex.GetType().Name}";
        }

        var lines = new[]
        {
            $"进程架构     : {(Environment.Is64BitProcess ? "x64" : "x86")} (unpackaged, self-contained)",
            $"OS 版本      : {Environment.OSVersion.Version}",
            $"CLR 版本     : {Environment.Version}　.NET 10",
            $"应用基目录   : {AppContext.BaseDirectory}",
            $"包标识       : {packageId}",
            $"DPI 感知     : {GetDpiAwareness()}",
        };

        EnvInfoText.Text = string.Join(Environment.NewLine, lines);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    private string GetDpiAwareness()
    {
        try
        {
            IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            return $"{GetDpiForWindow(hwnd)} dpi (PerMonitorV2)";
        }
        catch
        {
            return "未取到";
        }
    }
}
