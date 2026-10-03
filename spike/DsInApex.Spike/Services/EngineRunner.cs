using System.Diagnostics;
using System.Text;

namespace DsInApex.Spike.Services;

/// <summary>一次引擎调用的完整结果。</summary>
public sealed record EngineResult(
    string Command,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    TimeSpan Duration,
    bool TimedOut)
{
    public bool Succeeded => !TimedOut && ExitCode == 0;
}

/// <summary>
/// 引擎进程调用器。
///
/// 设计约束（来自 Spike 实测）：
/// 1. 引擎是控制台程序，必须重定向 stdout/stderr 且不创建窗口；
/// 2. <c>list</c> 在找不到设备时返回**退出码 2**，不能只看 0/非 0 判成败；
/// 3. 输出需按行异步读取，避免大输出时管道缓冲区写满导致死锁。
/// </summary>
public sealed class EngineRunner
{
    public EngineRunner(string executablePath)
    {
        ExecutablePath = executablePath;
    }

    public string ExecutablePath { get; }

    public bool Exists => File.Exists(ExecutablePath);

    public async Task<EngineResult> RunAsync(
        string arguments,
        int timeoutMs = 30_000,
        CancellationToken cancellationToken = default)
    {
        if (!Exists)
        {
            return new EngineResult(
                arguments, -1, string.Empty,
                $"引擎可执行文件不存在：{ExecutablePath}",
                TimeSpan.Zero, false);
        }

        var psi = new ProcessStartInfo
        {
            FileName = ExecutablePath,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // 引擎对产品名做了 narrowAscii() 处理，输出为纯 ASCII；
            // 仍按 UTF-8 解码以便未来切换到非 ASCII 文案时不至于立刻乱码。
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory,
        };

        var sw = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };

        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            return new EngineResult(
                arguments, -1, string.Empty,
                $"引擎启动失败：{ex.GetType().Name} - {ex.Message}",
                sw.Elapsed, false);
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            // WaitForExit() 无参重载会等待异步输出回调把剩余数据读完，
            // 缺少这一步偶发丢最后几行。
            process.WaitForExit();
        }
        catch (OperationCanceledException)
        {
            timedOut = true;
            try { process.Kill(entireProcessTree: true); } catch { /* 进程可能已退出 */ }
        }

        sw.Stop();

        return new EngineResult(
            arguments,
            timedOut ? -1 : process.ExitCode,
            stdout.ToString(),
            stderr.ToString(),
            sw.Elapsed,
            timedOut);
    }
}
