using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>
/// Per-point skip decisions for point-policy routes (pure logic, no game access).
/// When the current fight point cannot be completed (verified defeat recovery or stuck retries
/// exhausted, or a positioning failure), only that encounter is skipped. Fights at the same spot
/// that directly follow it (the same boss fought twice) belong to the same encounter and are skipped
/// together. If an unfinished fight remains in the segment, the executor replays the segment from
/// its own teleport waypoint with every action suppressed until the waypoint after the skipped
/// encounter (the existing retry-replay mechanism), then continues normally.
/// </summary>
internal static class PointSkipPlanner
{
    /// <summary>At most this many encounters may be skipped in one route run.</summary>
    internal const int MaxSkipsPerRoute = 2;

    /// <summary>Fights closer than this (map units) to the skipped fight count as the same encounter.</summary>
    internal const double SameSpotDistance = 5.0;

    /// <param name="Finished">Fight already completed or already skipped in this run.</param>
    internal readonly record struct Node(bool IsFight, bool Finished, bool IsTeleport, double X, double Y);

    internal enum Outcome
    {
        /// <summary>Keep the previous behaviour (terminal failure for this route).</summary>
        NotAllowed,
        /// <summary>Nothing left to fight in this segment: stop the segment here.</summary>
        FinishSegment,
        /// <summary>Replay the segment from its teleport, suppressing actions before <see cref="Decision.ResumeIndex"/>.</summary>
        Resume
    }

    internal sealed record Decision(Outcome Outcome, IReadOnlyList<int> SkippedIndices, int ResumeIndex, string Reason);

    internal static Decision Decide(IReadOnlyList<Node> nodes, int failingIndex, int skipsSoFar)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var start = Math.Max(0, failingIndex);
        var failed = -1;
        for (var i = start; i < nodes.Count; i++)
        {
            if (nodes[i].IsFight && !nodes[i].Finished)
            {
                failed = i;
                break;
            }
        }

        if (failed < 0)
            return new Decision(Outcome.FinishSegment, Array.Empty<int>(), -1, "no-unfinished-fight");
        if (skipsSoFar >= MaxSkipsPerRoute)
            return new Decision(Outcome.NotAllowed, Array.Empty<int>(), -1, "skip-limit");

        // Same encounter: unfinished fights at (almost) the same spot right after the failed one,
        // with only same-spot non-fight waypoints (e.g. orientation) in between.
        var group = new List<int> { failed };
        var last = failed;
        for (var i = failed + 1; i < nodes.Count; i++)
        {
            if (Distance(nodes[i], nodes[failed]) >= SameSpotDistance) break;
            if (nodes[i].IsFight && !nodes[i].Finished) group.Add(i);
            last = i;
        }

        var next = -1;
        for (var i = last + 1; i < nodes.Count; i++)
        {
            if (nodes[i].IsFight && !nodes[i].Finished)
            {
                next = i;
                break;
            }
        }

        if (next < 0)
            return new Decision(Outcome.FinishSegment, group, -1, "no-next-fight");
        if (nodes.Count == 0 || !nodes[0].IsTeleport)
            return new Decision(Outcome.NotAllowed, Array.Empty<int>(), -1, "segment-has-no-teleport");
        return new Decision(Outcome.Resume, group, last + 1, "resume");
    }

    private static double Distance(Node a, Node b)
    {
        var dx = a.X - b.X;
        var dy = a.Y - b.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
