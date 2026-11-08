// Linked production PointSkipPlanner + PointSkipTracker (pure logic). No game, executor or file access.
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Node = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipPlanner.Node;
using Outcome = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipPlanner.Outcome;
using Pt = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipTracker.Point;

var passed = 0;
void Check(bool value, string description) { if (!value) throw new Exception("FAILED: " + description); passed++; }
void Throws<T>(Action run, string description) where T : Exception
{
    try { run(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + description);
}
Node T(double x, double y) => new(false, false, false, true, x, y);                       // teleport
Node P(double x, double y) => new(false, false, false, false, x, y);                      // path / orientation, no fight
Node F(double x, double y, bool done = false, bool skipped = false) => new(true, done, skipped, false, x, y);
string Ix(PointSkipPlanner.Decision d) => string.Join(",", d.SkippedIndices);

// ---------------- Planner ----------------
// Segment: 0 teleport, 1-2 path, 3 fight, 4-5 path, 6 fight, 7 path, 8 fight
var seg = new List<Node> { T(0, 0), P(10, 0), P(20, 0), F(30, 0), P(40, 0), P(50, 0), F(60, 0), P(70, 0), F(80, 0) };
foreach (var failing in new[] { 0, 1, 2, 3 })
{
    var d = PointSkipPlanner.Decide(seg, failing, 0);
    Check(d.Outcome == Outcome.Resume && Ix(d) == "3" && d.ResumeIndex == 4, "Skip the encounter being approached/fought: failing=" + failing);
}
Check(PointSkipPlanner.Decide(seg, -7, 0).ResumeIndex == 4, "Negative index clamped");
var firstDone = new List<Node>(seg) { [3] = F(30, 0, done: true) };
var d2 = PointSkipPlanner.Decide(firstDone, 3, 0);
Check(d2.Outcome == Outcome.Resume && Ix(d2) == "6" && d2.ResumeIndex == 7, "Completed #3 is passed; the next unfinished fight is the failed one");
var firstSkipped = new List<Node>(seg) { [3] = F(30, 0, skipped: true) };
Check(Ix(PointSkipPlanner.Decide(firstSkipped, 0, 1)) == "6", "A stale index never re-selects a skipped fight");
var last = PointSkipPlanner.Decide(seg, 8, 1);
Check(last.Outcome == Outcome.FinishSegment && Ix(last) == "8", "Failing last fight finishes the segment and records it");
Check(PointSkipPlanner.Decide(seg, 7, 0).Outcome == Outcome.FinishSegment, "Approach to the last fight finishes the segment");
var allDone = seg.Select(n => n.IsFight ? n with { Completed = true } : n).ToList();
var none = PointSkipPlanner.Decide(allDone, 4, PointSkipPlanner.MaxSkipsPerRoute);
Check(none.Outcome == Outcome.FinishSegment && none.SkippedIndices.Count == 0, "All fights finished -> finish segment, no skip, no budget needed");
Check(PointSkipPlanner.Decide(new List<Node>(), 0, 0).Outcome == Outcome.FinishSegment, "Empty segment finishes");
Check(PointSkipPlanner.Decide(seg, 99, 0).Outcome == Outcome.FinishSegment, "Index past the end finishes");
var limited = PointSkipPlanner.Decide(seg, 3, PointSkipPlanner.MaxSkipsPerRoute);
Check(limited.Outcome == Outcome.NotAllowed && limited.SkippedIndices.Count == 0, "At the limit nothing is skipped and the old failure applies");
Check(PointSkipPlanner.Decide(seg, 3, PointSkipPlanner.MaxSkipsPerRoute - 1).Outcome == Outcome.Resume, "Below the limit skipping is allowed");

// Same encounter, both directions (E018/ZE001/G002 style).
var boss = new List<Node> { T(0, 0), P(20, 0), F(50, 50), P(50.5, 50), F(50, 50), P(120, 50), F(200, 50) };
var bd = PointSkipPlanner.Decide(boss, 2, 0);
Check(bd.Outcome == Outcome.Resume && Ix(bd) == "2,4" && bd.ResumeIndex == 5, "Same-spot repeat fights (with same-spot orientation between) form one encounter");
var bossFirstDone = new List<Node>(boss) { [2] = F(50, 50, done: true) };
var bfd = PointSkipPlanner.Decide(bossFirstDone, 4, 0);
Check(Ix(bfd) == "2,4" && bfd.ResumeIndex == 5, "Backward: an already completed round of the same encounter is skipped with it");
var bossOnly = new List<Node> { T(0, 0), P(20, 0), F(50, 50), F(51, 50) };
var bod = PointSkipPlanner.Decide(bossOnly, 2, 0);
Check(bod.Outcome == Outcome.FinishSegment && Ix(bod) == "2,3", "Two same-spot fights at the end finish the segment together");
var near = new List<Node> { T(0, 0), F(50, 50), F(54.9, 50), F(60, 50), P(70, 50), F(90, 50) };
var nd = PointSkipPlanner.Decide(near, 1, 0);
Check(Ix(nd) == "1,2" && nd.ResumeIndex == 3, "Encounter stops at the first waypoint >= 5 units away");
var withSkipped = new List<Node> { T(0, 0), F(50, 50, skipped: true), F(50, 50, done: true), F(50, 50), P(100, 50), F(150, 50) };
var wsd = PointSkipPlanner.Decide(withSkipped, 3, 1);
Check(Ix(wsd) == "2,3" && wsd.ResumeIndex == 4, "Already skipped fights are never recorded twice");
Check(PointSkipPlanner.SameSpotDistance == 5.0, "Same-spot threshold is 5 map units");
var noTp = new List<Node> { P(0, 0), F(10, 0), P(20, 0), F(30, 0) };
var nt = PointSkipPlanner.Decide(noTp, 1, 0);
Check(nt.Outcome == Outcome.NotAllowed && nt.SkippedIndices.Count == 0, "No teleport at segment start -> no replay, old behaviour");
Check(PointSkipPlanner.Decide(noTp, 3, 0).Outcome == Outcome.FinishSegment, "Finishing a segment does not need a teleport");
for (var skips = 0; skips <= PointSkipPlanner.MaxSkipsPerRoute; skips++)
for (var failing = -2; failing <= seg.Count + 1; failing++)
{
    var d = PointSkipPlanner.Decide(seg, failing, skips);
    if (d.Outcome == Outcome.Resume)
    {
        Check(d.SkippedIndices.Count > 0 && d.ResumeIndex > d.SkippedIndices.Max(), "Resume index strictly after every skipped index");
        Check(d.ResumeIndex < seg.Count && seg.Skip(d.ResumeIndex).Any(n => n.IsFight && !n.Finished), "Resume only when an unfinished fight remains");
        Check(d.SkippedIndices.All(i => seg[i].IsFight && !seg[i].Skipped), "Only non-skipped fights are recorded");
        Check(skips < PointSkipPlanner.MaxSkipsPerRoute, "Resume never exceeds the per-route limit");
    }
    if (d.Outcome == Outcome.NotAllowed) Check(d.SkippedIndices.Count == 0, "NotAllowed never records skips");
}
Throws<ArgumentNullException>(() => PointSkipPlanner.Decide(null!, 0, 0), "Null segment rejected");
Check(PointSkipPlanner.MaxSkipsPerRoute == 2, "Per-route skip limit is 2");

// ---------------- Tracker (executor state machine) ----------------
Pt TP(int o, double x, double y) => new(false, o, true, x, y);
Pt PP(int o, double x, double y) => new(false, o, false, x, y);
Pt FP(int o, double x, double y) => new(true, o, false, x, y);
// Single segment: 0 tp, 1-2 path, 3 fight, 4-5 path, 6 fight, 7 path, 8 fight (original index == segment index)
var route = new List<Pt> { TP(0, 0, 0), PP(1, 10, 0), PP(2, 20, 0), FP(3, 30, 0), PP(4, 40, 0), PP(5, 50, 0), FP(6, 60, 0), PP(7, 70, 0), FP(8, 80, 0) };
{
    var t = new PointSkipTracker();
    Check(t.ClampRecordIndex(0, 2) == 2 && !t.HasFloor(0), "No floor before any skip");
    Check(t.ReplayFailureCode(0) is null, "No replay code before any skip");
    var a = t.Apply(0, route, 3, "revive-retry-exhausted");
    Check(a.Outcome == Outcome.Resume && a.ResumeIndex == 4 && t.SkipEvents == 1, "First skip resumes after #3");
    Check(t.IsDone(3) && t.Skipped.Count == 1 && t.Skipped[0] == (3, "revive-retry-exhausted"), "#3 recorded as skipped with its reason");
    Check(t.HasFloor(0) && t.ClampRecordIndex(0, 2) == 4 && t.ClampRecordIndex(0, 6) == 6, "Replay retries never record a point before the floor");
    Check(t.ReplayFailureCode(0) == "revive-retry-exhausted" && t.ReplayFailureCode(1) is null,
        "A replayed segment remembers the skip that started its replay");
    var before = t.Apply(0, route, 2, "route-stuck");
    Check(before.Outcome == Outcome.NotAllowed && before.Reason == "replay-before-floor", "A failure on the replay approach never skips a new, untried fight");
    Check(t.Skipped.Count == 1 && t.SkipEvents == 1, "Replay-before-floor records nothing");
    Check(t.ReplayFailureCode(0) == "revive-retry-exhausted", "A refused replay-before-floor keeps the replay code");
    t.MarkCompleted(3);
    Check(!t.Completed.Contains(3), "A skipped fight is never resurrected as completed");
    var b = t.Apply(0, route, 6, "route-stuck");
    Check(b.Outcome == Outcome.Resume && b.ResumeIndex == 7 && t.SkipEvents == 2, "Second skip after the floor");
    Check(t.Apply(0, route, 8, "target-too-far").Outcome == Outcome.NotAllowed, "Third encounter hits the per-route limit");
    Check(t.ReplayFailureCode(0) == "route-stuck", "A refused skip at the limit keeps the latest replay code");
    Check(t.Skipped.Select(s => s.OriginalIndex).SequenceEqual(new[] { 3, 6 }) && t.Completed.Count == 0, "Bookkeeping after the limit");
}
{
    // G002 style: round one (#6) completed, the team wipes twice on round two (#7) at the same spot.
    var g = new List<Pt> { TP(0, 0, 0), PP(1, 20, 0), FP(6, 50, 50), FP(7, 50, 50), PP(8, 120, 50), FP(9, 200, 50) };
    var t = new PointSkipTracker();
    t.MarkCompleted(6);
    var a = t.Apply(0, g, 3, "revive-retry-exhausted");
    Check(a.Outcome == Outcome.Resume && Ix(a) == "2,3" && a.ResumeIndex == 4, "Whole encounter skipped, including the finished round");
    Check(!t.Completed.Contains(6) && t.IsDone(6) && t.IsDone(7), "Finished round no longer counts as a completed encounter");
    t.MarkCompleted(9);
    Check(t.Completed.SequenceEqual(new[] { 9 }), "Only the genuinely completed encounter counts");
    var only = new List<Pt> { TP(0, 0, 0), FP(6, 50, 50), FP(7, 50, 50) };
    var t2 = new PointSkipTracker();
    t2.MarkCompleted(6);
    var f = t2.Apply(0, only, 2, "revive-retry-exhausted");
    Check(f.Outcome == Outcome.FinishSegment && t2.Completed.Count == 0 && t2.Skipped.Count == 2, "Legend route with one encounter: nothing completed -> all-skipped failure, no CD");
}
{
    // Floors are per segment; finishing a segment never consumes the budget when everything is done.
    var t = new PointSkipTracker();
    t.Apply(0, route, 3, "route-stuck");
    Check(t.ClampRecordIndex(1, 2) == 2 && !t.HasFloor(1) && t.ReplayFailureCode(1) is null, "Another segment has no floor");
    var tl = new PointSkipTracker();
    var finish = tl.Apply(0, route, 8, "route-stuck");
    Check(finish.Outcome == Outcome.FinishSegment && !tl.HasFloor(0) && tl.ReplayFailureCode(0) is null,
        "Finishing a segment starts no replay");
    var t3 = new PointSkipTracker();
    foreach (var o in new[] { 3, 6, 8 }) t3.MarkCompleted(o);
    var done = t3.Apply(0, route, 8, "route-stuck");
    Check(done.Outcome == Outcome.FinishSegment && done.SkippedIndices.Count == 0 && t3.SkipEvents == 0, "Failure after all fights: finish segment, no skip event");
    Throws<ArgumentNullException>(() => t3.Apply(0, null!, 0, "x"), "Null points rejected");
}
{
    // Pinned boundaries: a failure exactly at the floor is past the skipped encounter; a second replay raises
    // the floor and its code; completed (not skipped) fights are done, plain path points are not.
    var t = new PointSkipTracker();
    t.Apply(0, route, 3, "revive-retry-exhausted");
    var atFloor = t.Apply(0, route, 4, "route-stuck");
    Check(atFloor.Outcome == Outcome.Resume && Ix(atFloor) == "6" && atFloor.ResumeIndex == 7, "A failure at the floor index is past the skipped encounter");
    Check(t.ClampRecordIndex(0, 5) == 7 && t.ReplayFailureCode(0) == "route-stuck", "A second replay raises the floor and updates the replay code");
    t.MarkCompleted(8);
    Check(t.IsDone(8) && t.Completed.Contains(8) && !t.IsDone(5), "Completed fights are done; plain path points are not");
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0, scope = "Linked production PointSkipPlanner.Decide + PointSkipTracker; pure decision and executor state logic." }));
