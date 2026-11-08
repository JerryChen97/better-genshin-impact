using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>
/// Per-point skip decisions for point-policy routes (pure logic, no game access).
/// When the current fight point cannot be completed (verified defeat recovery or stuck retries
/// exhausted, or a positioning failure), only that encounter is skipped. An encounter is the
/// contiguous run of waypoints at the same spot (&lt; 5 map units) around the failed fight, so a boss
/// fought twice at one spot is skipped as a whole, including a round that already finished.
/// If an unfinished fight remains in the segment, the executor replays the segment from its own
/// teleport waypoint with fight actions suppressed until the waypoint after the encounter.
/// </summary>
internal static class PointSkipPlanner
{
    /// <summary>At most this many encounters may be skipped in one route run.</summary>
    internal const int MaxSkipsPerRoute = 2;

    /// <summary>Waypoints closer than this (map units) to the failed fight belong to the same encounter.</summary>
    internal const double SameSpotDistance = 5.0;

    /// <param name="Completed">Fight finished normally in this run.</param>
    /// <param name="Skipped">Fight already skipped in this run (never selected again).</param>
    internal readonly record struct Node(bool IsFight, bool Completed, bool Skipped, bool IsTeleport, double X, double Y)
    {
        internal bool Finished => Completed || Skipped;
    }

    internal enum Outcome
    {
        /// <summary>Keep the previous behaviour (terminal failure for this route).</summary>
        NotAllowed,
        /// <summary>Nothing left to fight in this segment: stop the segment here.</summary>
        FinishSegment,
        /// <summary>Replay the segment from its teleport, suppressing fights before <see cref="Decision.ResumeIndex"/>.</summary>
        Resume
    }

    /// <param name="SkippedIndices">Segment indices of the fights to record as skipped (may include a same-encounter fight that already completed).</param>
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

        // Encounter = contiguous same-spot run around the failed fight (both directions).
        var first = failed;
        while (first - 1 >= 0 && Distance(nodes[first - 1], nodes[failed]) < SameSpotDistance) first--;
        var last = failed;
        while (last + 1 < nodes.Count && Distance(nodes[last + 1], nodes[failed]) < SameSpotDistance) last++;
        var group = new List<int>();
        for (var i = first; i <= last; i++)
        {
            if (nodes[i].IsFight && !nodes[i].Skipped) group.Add(i);
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
