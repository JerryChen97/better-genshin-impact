using System;
using System.Runtime.ExceptionServices;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

internal enum PointLootRouteFailureReason
{
    PositionUnrecognized,
    TargetTooFar,
    RouteStuck,
    ReviveRetryExhausted
}

/// <summary>
/// An explicitly classified terminal failure for one point-policy route. Never a
/// cancellation, unfinished recovery, policy error or general HandledException.
/// </summary>
internal sealed class PointLootRouteFailureException : Exception
{
    internal string Code { get; }

    internal PointLootRouteFailureException(PointLootRouteFailureReason reason, string message) : base(message)
    {
        Code = CodeFor(reason);
    }

    internal static string CodeFor(PointLootRouteFailureReason reason) => reason switch
    {
        PointLootRouteFailureReason.PositionUnrecognized => "position-unrecognized",
        PointLootRouteFailureReason.TargetTooFar => "target-too-far",
        PointLootRouteFailureReason.RouteStuck => "route-stuck",
        PointLootRouteFailureReason.ReviveRetryExhausted => "revive-retry-exhausted",
        _ => throw new ArgumentOutOfRangeException(nameof(reason))
    };
}

/// <summary>Retains normal route retries; only the executor may promote the final attempt.</summary>
internal sealed class PointLootRouteRetryException : RetryException
{
    private readonly PointLootRouteFailureReason _reason;

    internal PointLootRouteRetryException(PointLootRouteFailureReason reason, string message) : base(message)
    {
        if (reason is not (PointLootRouteFailureReason.RouteStuck or PointLootRouteFailureReason.ReviveRetryExhausted))
            throw new ArgumentOutOfRangeException(nameof(reason));
        _reason = reason;
    }

    internal PointLootRouteFailureReason Reason => _reason;

    internal PointLootRouteFailureException ToTerminalFailure(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return new PointLootRouteFailureException(_reason, Message);
    }

    internal static void ThrowIfExhausted(RetryException retry, bool pointPolicyActive, bool lastAttempt,
        CancellationToken ct)
    {
        if (!pointPolicyActive || !lastAttempt) return;
        ct.ThrowIfCancellationRequested();
        if (retry is PointLootRouteRetryException local) throw local.ToTerminalFailure(ct);
        ExceptionDispatchInfo.Capture(retry).Throw();
    }
}
