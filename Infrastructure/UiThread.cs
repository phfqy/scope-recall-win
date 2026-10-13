using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace ScopeRecallWin.Infrastructure;

/// <summary>
/// UI 线程调度帮助。
///
/// 背景（实测踩坑，两轮定位）：
///   1) WinUI 3 里视图 <c>Loaded</c> 等事件触发时 <c>SynchronizationContext.Current</c> 可能为 null，
///      此时 <c>async</c> 方法的 await 续体会落到**线程池线程**，再去访问 UI 元素即抛
///      <c>COMException 0x8001010E</c>（RPC_E_WRONG_THREAD）；该异常在线程池线程上抛出，
///      绕过 Application.UnhandledException，直接走 AppDomain 未处理异常 → 进程退出（闪退）。
///   2) 仅把工作 <c>TryEnqueue</c> 到 DispatcherQueue **不足够**：TryEnqueue 的回调里
///      SynchronizationContext 仍可能是 null，续体依旧跑到线程池。
///
/// 因此这里在回调内**显式安装 DispatcherQueueSynchronizationContext**，
/// 使 async 续体稳定回到 UI 线程；异常在此兜底并写 crash.log，不冒泡到进程级。
/// </summary>
public static class UiThread
{
    /// <summary>把异步工作投递到 UI 线程执行（安装 UI 同步上下文 + 异常兜底）。</summary>
    public static void Run(DispatcherQueue? queue, string tag, Func<Task> work)
    {
        if (queue is null)
        {
            return;
        }

        void Body()
        {
            var previous = SynchronizationContext.Current;
            SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));
            _ = RunCoreAsync(work, tag, previous);
        }

        if (queue.HasThreadAccess)
        {
            Body();
        }
        else
        {
            queue.TryEnqueue(() => Body());
        }
    }

    private static async Task RunCoreAsync(Func<Task> work, string tag, SynchronizationContext? previous)
    {
        try
        {
            await work();
        }
        catch (Exception ex)
        {
            App.LogCrash(tag, ex);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    /// <summary>把同步工作投递到 UI 线程执行（含异常兜底）。</summary>
    public static void Run(DispatcherQueue? queue, string tag, Action work)
    {
        if (queue is null)
        {
            return;
        }

        void Body()
        {
            try
            {
                work();
            }
            catch (Exception ex)
            {
                App.LogCrash(tag, ex);
            }
        }

        if (queue.HasThreadAccess)
        {
            Body();
        }
        else
        {
            queue.TryEnqueue(() => Body());
        }
    }

    /// <summary>在 UI 线程执行一段 UI 更新（调用方可能不在 UI 线程）。</summary>
    public static void Post(DispatcherQueue? queue, Action action)
    {
        if (queue is null)
        {
            return;
        }
        if (queue.HasThreadAccess)
        {
            action();
        }
        else
        {
            queue.TryEnqueue(() => action());
        }
    }
}
