using System;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using Microsoft.Extensions.Logging;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

public partial class TpTask
{
    private bool _classifyTeleportFailure;
    private sealed class TeleportPanelNotOpenedException(string message) : TpPointNotActivate(message);

    private async Task<(double, double)> TpWithRetries(double tpX, double tpY, string mapName, bool force,
        bool classifyFailure = false)
    {
        _classifyTeleportFailure = classifyFailure;
        Exception? lastFailure = null;
        for (var i = 0; i < 3; i++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await TpOnce(tpX, tpY, mapName, force);
            }
            catch (TeleportPanelNotOpenedException e)
            {
                // Re-clicking the same missing panel cannot help. Keep its existing single-attempt limit.
                ct.ThrowIfCancellationRequested();
                InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(300, ct);
                if (classifyFailure) throw new TeleportFailureException(e.Message, e);
                throw;
            }
            catch (TpPointNotActivate e)
            {
                // Close the details panel even on the last attempt, but never send new input after a stop.
                ct.ThrowIfCancellationRequested();
                InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                await Delay(300, ct);
                lastFailure = e;
                Logger.LogWarning(e.Message + "  重试");
            }
            catch (Exception e) when (IsTaskStopException(e))
            {
                throw;
            }
            catch (Exception e)
            {
                // Configuration/engine errors are not route-local. Preserve the legacy retry path for other callers.
                // This token also carries the internal budget. Do not replace an unknown error with budget cancellation;
                // the route recovery boundary owns the original caller token and gives a real user stop priority.
                if (classifyFailure && !IsKnownTeleportFailure(e)) throw;
                ct.ThrowIfCancellationRequested();
                lastFailure = e;
                Logger.LogDebug(e, e.Message);
                Logger.LogWarning("传送异常" + e.Message);
                await Delay(300, ct);
            }
        }

        ct.ThrowIfCancellationRequested();
        if (classifyFailure) throw new TeleportFailureException("传送失败", lastFailure);
        throw new InvalidOperationException("传送失败", lastFailure);
    }

    private static bool IsKnownTeleportFailure(Exception failure) =>
        failure is TeleportFailureException or MapPositionNotRecognizedException { InnerException: null };

    private static bool IsTaskStopException(Exception exception) =>
        exception is NormalEndException or OperationCanceledException;
}
