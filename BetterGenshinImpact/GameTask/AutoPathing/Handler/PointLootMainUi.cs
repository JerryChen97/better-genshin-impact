using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>Main-UI confirmation for point-policy statue recovery (fresh frames only).</summary>
internal static class PointLootMainUi
{
    private const int RequiredConsecutiveFrames = 3;
    private const int ObservationIntervalMs = 250;
    private const int MaxObservations = 24;

    /// <summary>
    /// Observes without sending input. True after 3 consecutive fresh frames (250 ms apart, so at least 0.5 s) in the
    /// main UI without a revive prompt, within about 6 s. A main UI that shows only briefly before a teleport's
    /// loading screen does not pass.
    /// </summary>
    internal static async Task<bool> ConfirmAsync(CancellationToken ct)
    {
        var consecutive = 0;
        for (var observation = 0; observation < MaxObservations; observation++)
        {
            ct.ThrowIfCancellationRequested();
            using (var capture = CaptureToRectArea())
            {
                consecutive = Bv.IsInMainUi(capture) && !Bv.IsInRevivePrompt(capture) ? consecutive + 1 : 0;
            }

            if (consecutive >= RequiredConsecutiveFrames) return true;
            if (observation < MaxObservations - 1) await Delay(ObservationIntervalMs, ct);
        }

        return false;
    }

    /// <summary>
    /// After a statue teleport exceeded its own budget, the big map or teleport panel may still be open, or the
    /// teleport may still be loading. Returns to the main UI with the upstream routine (ESC), then confirms. A second
    /// round covers an ESC that landed just as loading finished and opened the menu. False if never confirmed.
    /// </summary>
    internal static async Task<bool> RestoreAfterTeleportTimeoutAsync(CancellationToken ct)
    {
        Logger.LogWarning("七天神像传送超时，返回主界面并确认后按恢复重试处理");
        for (var round = 0; round < 2; round++)
        {
            await new ReturnMainUiTask().Start(ct);
            if (await ConfirmAsync(ct)) return true;
        }

        Logger.LogWarning("七天神像传送超时后未能确认回到主界面，按原规则停止");
        return false;
    }
}
