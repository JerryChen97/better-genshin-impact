// Linked production PointSkipPlanner (pure logic). No game, executor or file access.
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Node = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipPlanner.Node;
using Outcome = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipPlanner.Outcome;

var passed = 0;
void Check(bool value, string description) { if (!value) throw new Exception("FAILED: " + description); passed++; }
void Throws<T>(Action run, string description) where T : Exception
{
    try { run(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + description);
}
Node T(double x, double y) => new(false, false, true, x, y);                          // teleport
Node P(double x, double y) => new(false, false, false, x, y);                         // path / orientation, no fight
Node F(double x, double y, bool finished = false) => new(true, finished, false, x, y);
string Ix(PointSkipPlanner.Decision d) => string.Join(",", d.SkippedIndices);

// Segment: 0 teleport, 1-2 path, 3 fight, 4-5 path, 6 fight, 7 path, 8 fight
var seg = new List<Node> { T(0, 0), P(10, 0), P(20, 0), F(30, 0), P(40, 0), P(50, 0), F(60, 0), P(70, 0), F(80, 0) };

// Failure while approaching or fighting #3: skip #3, replay to the waypoint after it.
foreach (var failing in new[] { 0, 1, 2, 3 })
{
    var d = PointSkipPlanner.Decide(seg, failing, 0);
    Check(d.Outcome == Outcome.Resume && Ix(d) == "3" && d.ResumeIndex == 4, "Skip the encounter being approached/fought: failing=" + failing);
}
Check(PointSkipPlanner.Decide(seg, -7, 0).ResumeIndex == 4, "Negative index clamped");

// Already finished (completed or skipped) fights are never picked again.
var firstDone = new List<Node>(seg) { [3] = F(30, 0, finished: true) };
var d2 = PointSkipPlanner.Decide(firstDone, 3, 0);
Check(d2.Outcome == Outcome.Resume && Ix(d2) == "6" && d2.ResumeIndex == 7, "Finished #3 is skipped over; the next unfinished fight is the failed one");
var stale = PointSkipPlanner.Decide(firstDone, 0, 1);
Check(Ix(stale) == "6", "A stale current index still never re-selects a finished/skipped fight");

// Last fight fails -> finish segment, record the skip.
var last = PointSkipPlanner.Decide(seg, 8, 1);
Check(last.Outcome == Outcome.FinishSegment && Ix(last) == "8", "Failing last fight finishes the segment and records it");
Check(PointSkipPlanner.Decide(seg, 7, 0).Outcome == Outcome.FinishSegment, "Approach to the last fight finishes the segment");

// Nothing unfinished at/after the failure -> finish segment without recording a skip, even at the limit.
var allDone = seg.Select(n => n.IsFight ? n with { Finished = true } : n).ToList();
var none = PointSkipPlanner.Decide(allDone, 4, PointSkipPlanner.MaxSkipsPerRoute);
Check(none.Outcome == Outcome.FinishSegment && none.SkippedIndices.Count == 0, "All fights finished -> finish segment, no skip, no budget needed");
Check(PointSkipPlanner.Decide(new List<Node>(), 0, 0).Outcome == Outcome.FinishSegment, "Empty segment finishes");
Check(PointSkipPlanner.Decide(seg, 99, 0).Outcome == Outcome.FinishSegment, "Index past the end finishes");

// Skip limit per route.
var limited = PointSkipPlanner.Decide(seg, 3, PointSkipPlanner.MaxSkipsPerRoute);
Check(limited.Outcome == Outcome.NotAllowed && limited.SkippedIndices.Count == 0, "At the limit nothing is skipped and the old failure applies");
Check(PointSkipPlanner.Decide(seg, 3, PointSkipPlanner.MaxSkipsPerRoute - 1).Outcome == Outcome.Resume, "Below the limit skipping is allowed");

// Same encounter: E018/ZE001-style repeated fights at the same spot are skipped together.
var boss = new List<Node> { T(0, 0), P(20, 0), F(50, 50), P(50.5, 50), F(50, 50), P(120, 50), F(200, 50) };
var bd = PointSkipPlanner.Decide(boss, 2, 0);
Check(bd.Outcome == Outcome.Resume && Ix(bd) == "2,4" && bd.ResumeIndex == 5, "Same-spot repeat fights (with same-spot orientation between) form one encounter");
var bossOnly = new List<Node> { T(0, 0), P(20, 0), F(50, 50), F(51, 50) };
var bod = PointSkipPlanner.Decide(bossOnly, 2, 0);
Check(bod.Outcome == Outcome.FinishSegment && Ix(bod) == "2,3", "Two same-spot fights at the end finish the segment together");
var near = new List<Node> { T(0, 0), F(50, 50), F(54.9, 50), F(60, 50), P(70, 50), F(90, 50) };
var nd = PointSkipPlanner.Decide(near, 1, 0);
Check(Ix(nd) == "1,2" && nd.ResumeIndex == 3, "Encounter grouping stops at the first waypoint >= 5 units away");
var finishedRepeat = new List<Node> { T(0, 0), F(50, 50), F(50, 50, finished: true), F(50, 50), P(100, 50), F(150, 50) };
var fd = PointSkipPlanner.Decide(finishedRepeat, 1, 0);
Check(Ix(fd) == "1,3" && fd.ResumeIndex == 4, "A finished fight inside the same spot is passed over, not re-recorded");
Check(PointSkipPlanner.SameSpotDistance == 5.0, "Same-spot threshold is 5 map units");

// Resume needs the segment's own teleport; finishing does not.
var noTp = new List<Node> { P(0, 0), F(10, 0), P(20, 0), F(30, 0) };
var nt = PointSkipPlanner.Decide(noTp, 1, 0);
Check(nt.Outcome == Outcome.NotAllowed && nt.SkippedIndices.Count == 0, "No teleport at segment start -> no replay, old behaviour");
Check(PointSkipPlanner.Decide(noTp, 3, 0).Outcome == Outcome.FinishSegment, "Finishing a segment does not need a teleport");

// Invariants over every failing index and skip count.
for (var skips = 0; skips <= PointSkipPlanner.MaxSkipsPerRoute; skips++)
for (var failing = -2; failing <= seg.Count + 1; failing++)
{
    var d = PointSkipPlanner.Decide(seg, failing, skips);
    if (d.Outcome == Outcome.Resume)
    {
        Check(d.SkippedIndices.Count > 0 && d.ResumeIndex > d.SkippedIndices.Max(), "Resume index strictly after every skipped index");
        Check(d.ResumeIndex < seg.Count && seg.Skip(d.ResumeIndex).Any(n => n.IsFight && !n.Finished), "Resume only when an unfinished fight remains");
        Check(d.SkippedIndices.All(i => seg[i].IsFight && !seg[i].Finished), "Only unfinished fights are skipped");
        Check(skips < PointSkipPlanner.MaxSkipsPerRoute, "Resume never exceeds the per-route limit");
    }
    if (d.Outcome == Outcome.NotAllowed) Check(d.SkippedIndices.Count == 0, "NotAllowed never records skips");
}
Throws<ArgumentNullException>(() => PointSkipPlanner.Decide(null!, 0, 0), "Null segment rejected");
Check(PointSkipPlanner.MaxSkipsPerRoute == 2, "Per-route skip limit is 2");

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0, scope = "Linked production PointSkipPlanner.Decide; pure decision logic only." }));
