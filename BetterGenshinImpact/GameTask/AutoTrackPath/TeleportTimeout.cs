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

internal static class TeleportBudget
{
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
