using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>
/// Per-point skip planning for point-policy routes (pure logic, no game access).
/// When one fight point cannot be completed (verified defeat recovery or stuck retries
/// exhausted, or a positioning failure), only that fight point is skipped. The executor
/// then teleports to the teleport point nearest the next unfinished fight point and
/// rejoins the remaining path at the waypoint closest to where it landed.
/// </summary>
internal static class PointSkipPlanner
{
    /// <summary>At most this many fight points may be skipped in one route run.</summary>
    internal const int MaxSkipsPerRoute = 2;

    internal readonly record struct Node(bool IsFight, bool Completed, double X, double Y);

    /// <summary>
    /// FailedFight: segment index of the first unfinished fight point at or after the failing
    /// waypoint (-1 when no unfinished fight remains in the segment).
    /// NextFight: segment index of the next unfinished fight point after FailedFight
    /// (-1 when nothing is left to fight in the segment).
    /// </summary>
    internal readonly record struct Plan(int FailedFight, int NextFight)
    {
        internal bool HasFailedFight => FailedFight >= 0;
        internal bool HasNextFight => NextFight >= 0;
    }

    internal static Plan Create(IReadOnlyList<Node> nodes, int failingIndex)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        var start = Math.Max(0, failingIndex);
        var failed = -1;
        for (var i = start; i < nodes.Count; i++)
        {
            if (nodes[i].IsFight && !nodes[i].Completed)
            {
                failed = i;
                break;
            }
        }

        if (failed < 0) return new Plan(-1, -1);

        var next = -1;
        for (var i = failed + 1; i < nodes.Count; i++)
        {
            if (nodes[i].IsFight && !nodes[i].Completed)
            {
                next = i;
                break;
            }
        }

        return new Plan(failed, next);
    }

    /// <summary>
    /// Waypoint index in (failedFight, nextFight] closest to the landing point; ties keep the
    /// later (closer to the next fight) index so no path towards the next fight is replayed
    /// unnecessarily. Never returns an index at or before the skipped fight point.
    /// </summary>
    internal static int JoinIndex(IReadOnlyList<Node> nodes, Plan plan, double landX, double landY)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        if (!plan.HasFailedFight || !plan.HasNextFight || plan.NextFight <= plan.FailedFight || plan.NextFight >= nodes.Count)
            throw new ArgumentException("A join index needs a skipped fight followed by a next fight.", nameof(plan));
        var best = plan.NextFight;
        var bestDistance = Distance(nodes[best], landX, landY);
        for (var i = plan.NextFight - 1; i > plan.FailedFight; i--)
        {
            var distance = Distance(nodes[i], landX, landY);
            if (distance < bestDistance)
            {
                best = i;
                bestDistance = distance;
            }
        }

        return best;
    }

    private static double Distance(Node node, double x, double y)
    {
        var dx = node.X - x;
        var dy = node.Y - y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
