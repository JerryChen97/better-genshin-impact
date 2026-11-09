using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>Main-UI confirmation for point-policy statue recovery (fresh frames only).</summary>
internal static class PointLootMainUi
{
    internal const int RequiredConsecutiveFrames = 3;
    internal const int MaxObservations = 24;
    private const int ObservationIntervalMs = 250;

    /// <summary>
    /// Observes without sending input. True after 3 consecutive fresh frames (250 ms apart, so at least 0.5 s) in the
    /// main UI without a revive prompt (and, when <paramref name="requireHealthy"/>, without a low-HP current avatar),
    /// within 24 observations (about 6 s). A main UI that shows only briefly before a teleport's loading screen does
    /// not pass.
    /// </summary>
    internal static async Task<bool> ConfirmAsync(CancellationToken ct, bool requireHealthy = false)
    {
        var consecutive = 0;
        for (var observation = 0; observation < MaxObservations; observation++)
        {
            ct.ThrowIfCancellationRequested();
            using (var capture = CaptureToRectArea())
            {
                var stable = Bv.IsInMainUi(capture) && !Bv.IsInRevivePrompt(capture) &&
                             !(requireHealthy && Bv.CurrentAvatarIsLowHp(capture));
                consecutive = stable ? consecutive + 1 : 0;
            }

            if (consecutive >= RequiredConsecutiveFrames) return true;
            if (observation < MaxObservations - 1) await Delay(ObservationIntervalMs, ct);
        }

        return false;
    }

    /// <summary>
    /// After a completed low-HP or revive-modal statue trip: the main UI is stable and the current avatar is no longer
    /// low on HP. Otherwise (e.g. a persistent low-HP misreading or an exhausted statue) the plain retry keeps its
    /// original contract.
    /// </summary>
    internal static async Task<bool> ConfirmHealedAsync(CancellationToken ct)
    {
        if (await ConfirmAsync(ct, requireHealthy: true)) return true;
        Logger.LogWarning("回七天神像后未能确认主界面稳定且血量正常，按普通重试处理");
        return false;
    }

    /// <summary>
    /// Route teleport failures may leave a held map drag, an open map or a loading screen.
    /// Only a freshly recognized map receives ESC; unknown screens receive no input.
    /// Two bounded observation rounds allow loading to settle. No Enter or dialog clicks.
    /// </summary>
    internal static async Task<bool> RestoreAfterRouteTeleportFailureAsync(Exception failure, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Logger.LogDebug(failure, "路线传送失败，释放输入并确认主界面");
        InputHub.ReleaseAll();
        await Delay(100, ct);
        for (var round = 0; round < 2; round++)
        {
            ct.ThrowIfCancellationRequested();
            bool mapOpen;
            using (var capture = CaptureToRectArea())
                mapOpen = Bv.IsInBigMapUi(capture) && !Bv.IsInRevivePrompt(capture);
            ct.ThrowIfCancellationRequested();
            if (mapOpen)
            {
                InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(900, ct);
            }
            var confirmed = await ConfirmAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (confirmed) return true;
        }

        Logger.LogWarning("路线传送失败后未能确认主界面，停止配置组");
        return false;
    }

    /// <summary>
    /// After a statue teleport exceeded its own budget, the big map or teleport panel may still be open (a map drag may
    /// even have left the left button down), or the teleport may still be loading. Releases all inputs, returns to the
    /// main UI with the upstream routine (ESC), then confirms. A second round covers an ESC that landed just as loading
    /// finished and opened the menu. False if never confirmed.
    /// </summary>
    internal static async Task<bool> RestoreAfterTeleportTimeoutAsync(TeleportTimeoutException timeout,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Logger.LogDebug(timeout, "七天神像传送超时");
        Logger.LogWarning("七天神像传送超时，返回主界面并确认后按恢复重试处理");
        InputHub.ReleaseAll();
        for (var round = 0; round < 2; round++)
        {
            ct.ThrowIfCancellationRequested();
            await new ReturnMainUiTask().Start(ct);
            if (await ConfirmAsync(ct)) return true;
        }

        Logger.LogWarning("七天神像传送超时后未能确认回到主界面，按原规则停止");
        return false;
    }
}
