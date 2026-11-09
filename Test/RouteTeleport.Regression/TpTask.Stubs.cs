using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoTrackPath
{

// Only the map/game attempt is stubbed. The production retry loop, error types and budget are linked.
public partial class TpTask(CancellationToken ct)
{
    // Omit wall-clock waits between recognition frames in the linked real NewRetry.Do.
    private const int BigMapRectRetryIntervalMs = 0;
    public Func<(bool Matched, Rect Rect)> RecognizeRect { get; set; } = () => (true, new Rect(1, 2, 3, 4));
    private RecognitionObject GetQuickTeleportRecognitionObject(string name, Capture capture) => new();
    private bool TryGetBigMapRect(Capture capture, string map, out Rect rect)
    {
        var result = RecognizeRect(); rect = result.Rect; return result.Matched;
    }
    public Func<Task<(double, double)>> Attempt { get; set; } = () => Task.FromResult((1d, 2d));
    private Task<(double, double)> TpOnce(double x, double y, string map, bool force) => Attempt();
    public Task<(double, double)> Run(bool classify = true) => TpWithRetries(1, 2, "Teyvat", false, classify);
    public static Exception MissingPanel() => new TeleportPanelNotOpenedException("panel absent");
}
}
namespace OpenCvSharp
{
    public readonly record struct Rect(int X, int Y, int Width, int Height);
}
