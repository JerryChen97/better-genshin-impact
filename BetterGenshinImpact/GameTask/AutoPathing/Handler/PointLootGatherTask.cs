using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoPathing.Handler;

/// <summary>Policy-owned gathering: current-party Kazuha only, no party switch and no unfiltered F.</summary>
internal static class PointLootGatherTask
{
    internal static async Task RunAsync(PointLootSession session, PointLootSession.Attempt attempt, CancellationToken ct)
    {
        // Disabled/unlisted points retain the combat task's normal return without extra UI operations.
        if (!session.CanGather(attempt) && !session.CanOpenWindow(attempt)) return;
        if (!await WaitForMainUiAsync(session, attempt, ct)) return;
        if (!session.CanGather(attempt)) return;
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, attempt.Revoked.Token);
        // Bound the extra gather operation independently of the JS pickup budget.
        linked.CancelAfter(TimeSpan.FromSeconds(15));
        CombatScenes? scenes = null;
        try
        {
            // Gathering is optional: a party or Kazuha-switch failure skips only the gather, never the route.
            // The pickup window still follows the post-gather main-UI check below.
            scenes = await RunnerContext.Instance.GetCombatScenes(linked.Token);
            var picker = scenes?.SelectAvatar("枫原万叶");
            if (scenes is null)
            {
                Logger.LogWarning("逐点拾取：无法识别当前队伍，跳过聚拢");
            }
            else if (picker is not null)
            {
                scenes.BeforeTask(linked.Token);
                linked.Token.ThrowIfCancellationRequested();
                InputHub.ReleaseAll();
                var switched = picker.TrySwitch(10, ct);
                linked.Token.ThrowIfCancellationRequested();
                if (switched)
                {
                    await Delay(100, linked.Token);
                    await picker.WaitSkillCd(linked.Token);
                    linked.Token.ThrowIfCancellationRequested();
                    Logger.LogInformation("逐点拾取：使用当前队伍的 枫原万叶-长E 聚拢");
                    await SimulateHoldElementalSkillAsync(800, linked.Token);
                    await SimulateMouseLeftClickLoopAsync(6, linked.Token);
                    await Delay(1500, linked.Token);
                    picker.AfterUseSkill();
                }
                else
                {
                    Logger.LogWarning("逐点拾取：切换枫原万叶失败，跳过聚拢");
                }
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && linked.IsCancellationRequested)
        {
            // A master revocation or optional gather budget expiry does not cancel the route.
        }
        catch (NormalEndException) when (!ct.IsCancellationRequested && linked.IsCancellationRequested)
        {
            // TaskControl.Delay/Sleep use NormalEndException at their cancellation boundaries.
        }
        finally
        {
            InputHub.ReleaseAll();
            scenes?.BeforeTask(ct);
        }
        ct.ThrowIfCancellationRequested();
        // Party lookup may itself restore the main UI. Recheck even when no Kazuha was found.
        await WaitForMainUiAsync(session, attempt, ct);
    }

    private static async Task<bool> WaitForMainUiAsync(PointLootSession session, PointLootSession.Attempt attempt,
        CancellationToken ct)
    {
        // Combat's party-setup check returns immediately after cancelling its UI animation.
        // Observe up to 20 fresh frames, 100 ms apart, without sending any additional input.
        // A task suspension may extend wall time; it must not consume the observation budget.
        var consecutiveMainUi = 0;
        for (var observation = 0; observation < 20; observation++)
        {
            ct.ThrowIfCancellationRequested();
            if (!session.CanGather(attempt) && !session.CanOpenWindow(attempt)) return false;
            bool revivePrompt;
            using (var capture = CaptureToRectArea())
            {
                revivePrompt = Bv.IsInRevivePrompt(capture);
                consecutiveMainUi = !revivePrompt && Bv.IsInMainUi(capture) ? consecutiveMainUi + 1 : 0;
            }
            // Same as a defeat during combat: close the prompt and recover at the statue with the route token
            // (never the optional gather budget). Always throws: the verified revive retry after a confirmed
            // recovery, otherwise the recovery failure or cancellation, exactly as for an in-fight defeat.
            if (revivePrompt) Avatar.RecoverFromRevivePrompt(ct);
            if (consecutiveMainUi >= 2) return true;
            if (observation < 19) await Delay(100, ct);
        }
        // Never gather or open an F window on an unknown UI; the route itself continues.
        Logger.LogWarning("逐点拾取：主界面未稳定，放弃本点的聚拢和拾取窗口，继续路线");
        session.Revoke(attempt);
        return false;
    }
}
