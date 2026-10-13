using System.Diagnostics;

namespace ScopeRecallWin.Infrastructure;

/// <summary>外部进程一次执行的结果（stdout/stderr 分离，UTF-8 解码）。</summary>
public sealed record ProcessResult(
    string FileName,
    string Arguments,
    int ExitCode,
    string StdOut,
    string StdErr,
    TimeSpan Duration,
    bool TimedOut,
    bool StartFailed,
    string? StartError)
{
    public bool Success => !StartFailed && !TimedOut && ExitCode == 0;
}

/// <summary>
/// 外部进程包装。用于调用官方 CLI（shared-venv 的 python -m scope_recall.maintenance.cli …）
/// 与安装器等外部工具。约束：
///   - 全程 UTF-8（官方 CLI 输出含中文，GBK 控制台默认会让中文变问号）；
///   - 超时强杀（Kill entireProcessTree，防 worker 子进程残留）；
///   - 捕获 stdout/stderr，不继承父进程控制台。
/// </summary>
public static class ProcessRunner
{
    public static async Task<ProcessResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        int timeoutMs = 120_000,
        CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.StartNew();
        var info = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = string.IsNullOrEmpty(workingDirectory) ? Directory.GetCurrentDirectory() : workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8,
        };

        Process? proc;
        try
        {
            proc = Process.Start(info);
        }
        catch (Exception ex)
        {
            return new ProcessResult(fileName, arguments, -1, "", "", start.Elapsed, false, true, ex.Message);
        }

        if (proc is null)
        {
            return new ProcessResult(fileName, arguments, -1, "", "", start.Elapsed, false, true, "Process.Start 返回 null");
        }

        using var _ = proc;
        // 异步读管道，避免输出缓冲区写满导致子进程阻塞死锁
        var outTask = proc.StandardOutput.ReadToEndAsync(cancellationToken);
        var errTask = proc.StandardError.ReadToEndAsync(cancellationToken);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeoutMs);

        try
        {
            await proc.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            TryKillTree(proc);
            return new ProcessResult(
                fileName, arguments, -1,
                await SafeRead(outTask), await SafeRead(errTask),
                start.Elapsed, true, false, null);
        }

        var stdout = await SafeRead(outTask);
        var stderr = await SafeRead(errTask);
        return new ProcessResult(fileName, arguments, proc.ExitCode, stdout, stderr, start.Elapsed, false, false, null);
    }

    private static async Task<string> SafeRead(Task<string> task)
    {
        try
        {
            return await task.ConfigureAwait(false);
        }
        catch
        {
            return "";
        }
    }

    private static void TryKillTree(Process proc)
    {
        try
        {
            proc.Kill(entireProcessTree: true);
        }
        catch
        {
            // 进程可能已自行退出；忽略
        }
    }
}
