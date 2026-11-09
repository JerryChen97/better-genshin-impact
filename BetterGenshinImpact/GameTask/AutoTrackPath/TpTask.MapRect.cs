using System;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.Common;
using OpenCvSharp;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

public partial class TpTask
{
    // Distinguish this helper's own recognition misses from unrelated RetryException/configuration errors.
    private sealed class MapRectRetryException(string message) : RetryException(message);

    public Rect GetBigMapRect(string mapName)
    {
        var rect = new Rect();
        try
        {
            NewRetry.Do(() =>
            {
                ct.ThrowIfCancellationRequested();
                using var ra = CaptureToRectArea();
                using var mapScaleButtonRa = ra.Find(GetQuickTeleportRecognitionObject("MapScaleButton", ra));
                if (mapScaleButtonRa.IsExist())
                {
                    try
                    {
                        if (!TryGetBigMapRect(ra, mapName, out rect)) rect = default;
                    }
                    catch (Exception error) when (!_classifyTeleportFailure || IsKnownTeleportFailure(error))
                    {
                        rect = default;
                    }

                    if (rect == default) throw new MapRectRetryException("识别大地图位置失败");
                }
                else
                {
                    throw new MapRectRetryException("当前不在地图界面");
                }
            }, TimeSpan.FromMilliseconds(BigMapRectRetryIntervalMs), 5,
                retry => !_classifyTeleportFailure || retry is MapRectRetryException);
        }
        catch (MapRectRetryException failure) when (_classifyTeleportFailure)
        {
            ct.ThrowIfCancellationRequested();
            throw new TeleportFailureException("多次重试后，识别大地图位置失败", failure);
        }

        ct.ThrowIfCancellationRequested();
        return rect;
    }
}
