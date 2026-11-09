using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// 单次传送超过了 TpTask 自身的时间预算，而调用方的令牌没有取消。这不是停止或取消。
/// </summary>
public sealed class TeleportTimeoutException(string message, Exception innerException)
    : TimeoutException(message, innerException);

/// <summary>
/// 进程内的连续神像超时保险。逐点策略的恢复把一次七天神像传送超时转成重试后，直到下一次七天神像传送完成之前，
/// 再次超时就视为持续故障：不再转换，原样传播（与修复前一样尽早停止），避免每条后续路线都反复回神像。
/// </summary>
internal static class StatueTimeoutGuard
{
    private static int s_convertedSinceCompleted;

    internal static bool MayConvert => Volatile.Read(ref s_convertedSinceCompleted) == 0;

    internal static void Converted() => Interlocked.Increment(ref s_convertedSinceCompleted);

    /// <summary>任何一次七天神像传送正常完成（TpToStatueOfTheSeven）都说明神像传送恢复正常。</summary>
    internal static void TeleportCompleted() => Volatile.Write(ref s_convertedSinceCompleted, 0);
}

internal static class TeleportBudget
{
    internal const int RouteTimeoutMs = 60_000;
    internal const int StatueTimeoutMs = 120_000;

    /// <summary>
    /// 在独立的时间预算内执行一次传送。预算到点时，内部可能在 await 中抛出 OperationCanceledException，
    /// 也可能在 TaskControl.Delay 入口抛出 NormalEndException("取消自动任务")，和手动停止一模一样。
    /// 只要调用方令牌未取消，两者都统一报告为 <see cref="TeleportTimeoutException"/>；
    /// 调用方令牌已取消（F11、停止按钮、路线令牌）时原样传播，停止语义不变。
    /// </summary>
    internal static async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> teleport, int timeoutMs,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(timeoutMs);
        try
        {
            return await teleport(budget.Token);
        }
        catch (Exception e) when ((e is NormalEndException or OperationCanceledException)
                                  && !ct.IsCancellationRequested && budget.IsCancellationRequested)
        {
            throw new TeleportTimeoutException($"单次传送超过 {timeoutMs / 1000} 秒", e);
        }
    }
}
