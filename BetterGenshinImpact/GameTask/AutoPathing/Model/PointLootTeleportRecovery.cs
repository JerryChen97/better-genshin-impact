using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoTrackPath;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>
/// Owned by one script bridge, whose route calls are serialized. One safely recovered route
/// teleport failure may be local; another without an intervening successful route teleport
/// is a persistent fault. Other bridges/sessions and statue recovery have separate state.
/// </summary>
internal sealed class PointLootTeleportRecovery
{
    private bool _failedSinceSuccess;

    internal async Task<T> RunAsync<T>(Func<Task<T>> teleport, Func<Exception, Task<bool>> restoreMainUi,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            var result = await teleport();
            ct.ThrowIfCancellationRequested();
            _failedSinceSuccess = false;
            return result;
        }
        catch (Exception failure) when (failure is TeleportFailureException or TeleportTimeoutException)
        {
            ct.ThrowIfCancellationRequested();
            bool restored;
            try
            {
                restored = await restoreMainUi(failure);
            }
            catch
            {
                ct.ThrowIfCancellationRequested();
                throw;
            }
            ct.ThrowIfCancellationRequested();
            if (!restored) throw;
            if (_failedSinceSuccess)
                throw new InvalidOperationException("路线传送连续失败，期间没有成功传送；停止配置组，不再跳过后续路线。", failure);

            _failedSinceSuccess = true;
            var reason = failure is TeleportTimeoutException
                ? PointLootRouteFailureReason.TeleportTimeout
                : PointLootRouteFailureReason.TeleportFailed;
            throw new PointLootRouteFailureException(reason, "路线传送失败，已确认返回主界面；本路线不记完成：" + failure.Message);
        }
        catch
        {
            ct.ThrowIfCancellationRequested();
            throw;
        }
    }
}
