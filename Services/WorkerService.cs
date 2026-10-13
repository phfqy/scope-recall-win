using System.Diagnostics;

namespace ScopeRecallWin.Services;

/// <summary>后台 worker 启停（端侧记忆优化模型管线）。P3 实现。</summary>
public interface IWorkerService
{
    Task<WorkerStatus> GetStatusAsync();
    Task<bool> StartAsync(string configPath, string pythonPath);
    Task<bool> StopAsync();
}

public sealed record WorkerStatus(bool Running, int? ExitCode, DateTimeOffset? StartedAt, DateTimeOffset? LastHeartbeat);

/// <summary>
/// worker 启停骨架。P3 落实：
///   - Start：Process spawn <VENV_PY> 运行官方 worker（--config 指向 runtime-config.json），轮询状态；
///   - Stop：发停止信号 → 等优雅退出 → 超时强停；记录启停时间入台账；
///   - autostart enable/pause 注册（--config/--python/--env-file）；resident recall server start/stop。
/// 推理由官方 worker 发起，桌面端绝不直接向模型发推理请求（约束 4）。
/// </summary>
public sealed class WorkerService : IWorkerService
{
    private readonly object _sync = new();
    // P3 赋值：StartAsync 启动官方 worker 进程后回填此处（当前骨架占位，禁告警）
#pragma warning disable CS0649
    private Process? _process;
#pragma warning restore CS0649

    public Task<WorkerStatus> GetStatusAsync()
    {
        lock (_sync)
        {
            var running = _process is { HasExited: false };
            return Task.FromResult(new WorkerStatus(running, running ? null : _process?.ExitCode,
                null, null));
        }
    }

    public Task<bool> StartAsync(string configPath, string pythonPath)
    {
        // 骨架占位：P3 实现 Process 启动。
        return Task.FromResult(false);
    }

    public Task<bool> StopAsync()
    {
        lock (_sync)
        {
            if (_process is { HasExited: false })
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        return Task.FromResult(true);
    }
}
