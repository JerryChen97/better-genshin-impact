using System;

namespace BetterGenshinImpact.GameTask.AutoTrackPath;

/// <summary>
/// A known map/target operation failed. Only the structured point-policy route entry may
/// turn this into a local result, after its existing retries and verified UI recovery.
/// Unknown exceptions and configuration errors must never acquire this classification.
/// </summary>
internal sealed class TeleportFailureException(string message, Exception? innerException = null)
    : InvalidOperationException(message, innerException);

public class MapPositionNotRecognizedException : Exception
{
    public MapPositionNotRecognizedException(string message) : base(message)
    {
    }

    public MapPositionNotRecognizedException(string message, Exception innerException) : base(message, innerException)
    {
    }
}
