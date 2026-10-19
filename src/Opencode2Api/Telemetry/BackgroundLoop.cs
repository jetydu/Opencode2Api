namespace Opencode2Api.Telemetry;

/// <summary>
/// 后台周期任务的统一执行骨架。
/// Go 版里每个后台任务（代理健康检查、模型目录刷新、models.dev 价格刷新、免费模型可用性探测）
/// 都手写一遍 ticker + select 样板，且各自的异常处理宽窄不一；这里合并为一份实现，
/// 语义保持与 Go 一致：
/// - runImmediately 为 true 时先立即执行一次，之后按 interval 周期性执行；
/// - 单次执行的异常只记录日志、不会终止循环（Go 中每个数据来源都会自行汇报错误）；
/// - 收到取消信号或定时器被释放时退出循环。
/// </summary>
internal static class BackgroundLoop
{
    /// <summary>在独立后台任务中启动周期循环，不阻塞调用方。</summary>
    /// <param name="name">循环名，仅用于失败日志定位。</param>
    /// <param name="logger">结构化日志器，可为空（如独立的存储对象尚未接日志时）。</param>
    /// <param name="interval">周期间隔。</param>
    /// <param name="runImmediately">是否在启动时立刻执行一次。</param>
    /// <param name="body">循环体。</param>
    /// <param name="cancellationToken">循环生命周期令牌。</param>
    public static void Start(
        string name, StructuredLogger? logger, TimeSpan interval, bool runImmediately,
        Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            if (runImmediately)
            {
                await RunOnce(name, logger, body, cancellationToken);
            }
            using var timer = new PeriodicTimer(interval);
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await WaitForTickAsync(timer, cancellationToken))
                {
                    return;
                }
                await RunOnce(name, logger, body, cancellationToken);
            }
        }, CancellationToken.None);
    }

    /// <summary>执行一次循环体，把异常收敛成日志，保证循环不被单次失败打断。</summary>
    private static async Task RunOnce(
        string name, StructuredLogger? logger, Func<CancellationToken, Task> body, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return;
        }
        try
        {
            await body(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // 停机取消属于正常路径，不需要告警。
        }
        catch (Exception error)
        {
            logger?.Warn("background loop iteration failed",
                ("component", "loop"), ("event", "loop_iteration_failed"), ("loop", name), ("error", error));
        }
    }

    /// <summary>
    /// 等待下一次心跳。返回 false 表示定时器已被释放（宿主提前关闭），循环应当退出；
    /// 因取消而抛出的异常在这里收敛。
    /// </summary>
    private static async Task<bool> WaitForTickAsync(PeriodicTimer timer, CancellationToken cancellationToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
