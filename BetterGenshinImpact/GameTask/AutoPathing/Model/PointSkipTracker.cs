using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoPathing.Model;

/// <summary>
/// Per-route state machine for per-point skip (pure logic, no game access). It owns the
/// completed/skipped fight bookkeeping (original point indices), the per-route skip budget and the
/// per-segment replay floor. The executor only asks it what to do and applies the answer.
/// </summary>
internal sealed class PointSkipTracker
{
    internal readonly record struct Point(bool IsFight, int OriginalIndex, bool IsTeleport, double X, double Y);

    private readonly HashSet<int> _completed = new();
    private readonly HashSet<int> _skippedSet = new();
    private readonly List<(int OriginalIndex, string Code)> _skipped = new();
    private readonly Dictionary<int, int> _floorBySegment = new();

    internal int SkipEvents { get; private set; }
    internal IReadOnlyCollection<int> Completed => _completed;
    internal IReadOnlyList<(int OriginalIndex, string Code)> Skipped => _skipped;

    /// <summary>A fight that finished normally. A fight already skipped as part of an encounter stays skipped.</summary>
    internal void MarkCompleted(int originalIndex)
    {
        if (!_skippedSet.Contains(originalIndex)) _completed.Add(originalIndex);
    }

    /// <summary>Completed or skipped fights are never executed again in this run.</summary>
    internal bool IsDone(int originalIndex) => _completed.Contains(originalIndex) || _skippedSet.Contains(originalIndex);

    /// <summary>A retry inside a segment never records a recovery point before that segment's replay floor.</summary>
    internal int ClampRecordIndex(int segment, int index) =>
        _floorBySegment.TryGetValue(segment, out var floor) ? Math.Max(index, floor) : index;

    internal bool HasFloor(int segment) => _floorBySegment.ContainsKey(segment);

    /// <summary>
    /// Decide and record a skip for a failure at <paramref name="failingIndex"/> in <paramref name="segment"/>.
    /// A failure that happens before the segment's replay floor (the replay could not even get past the
    /// encounter skipped earlier) never selects a new, untried fight: it returns NotAllowed so the route
    /// keeps the old local-failure result.
    /// </summary>
    internal PointSkipPlanner.Decision Apply(int segment, IReadOnlyList<Point> points, int failingIndex, string code)
    {
        ArgumentNullException.ThrowIfNull(points);
        if (_floorBySegment.TryGetValue(segment, out var floor) && failingIndex < floor)
            return new PointSkipPlanner.Decision(PointSkipPlanner.Outcome.NotAllowed, Array.Empty<int>(), -1, "replay-before-floor");

        var nodes = new List<PointSkipPlanner.Node>(points.Count);
        foreach (var p in points)
        {
            nodes.Add(new PointSkipPlanner.Node(p.IsFight,
                p.IsFight && _completed.Contains(p.OriginalIndex),
                p.IsFight && _skippedSet.Contains(p.OriginalIndex),
                p.IsTeleport, p.X, p.Y));
        }

        var decision = PointSkipPlanner.Decide(nodes, failingIndex, SkipEvents);
        if (decision.Outcome == PointSkipPlanner.Outcome.NotAllowed || decision.SkippedIndices.Count == 0)
            return decision;

        SkipEvents++;
        foreach (var index in decision.SkippedIndices)
        {
            var original = points[index].OriginalIndex;
            _completed.Remove(original); // an encounter only counts when all of its fights completed
            if (_skippedSet.Add(original)) _skipped.Add((original, code));
        }

        if (decision.Outcome == PointSkipPlanner.Outcome.Resume)
            _floorBySegment[segment] = decision.ResumeIndex;
        return decision;
    }
}
