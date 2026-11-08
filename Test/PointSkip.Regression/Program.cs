// Linked production PointSkipPlanner (pure logic). No game, executor or file access.
using System.Text.Json;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Node = BetterGenshinImpact.GameTask.AutoPathing.Model.PointSkipPlanner.Node;

var passed = 0;
void Check(bool value, string description) { if (!value) throw new Exception("FAILED: " + description); passed++; }
void Throws<T>(Action run, string description) where T : Exception
{
    try { run(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name + ": " + description);
}
Node P(double x, double y) => new(false, false, x, y);        // path / teleport / orientation waypoint
Node F(double x, double y, bool done = false) => new(true, done, x, y);

// Typical segment: 0 teleport, 1-2 path, 3 fight, 4-5 path, 6 fight, 7 path, 8 fight
var seg = new List<Node> { P(0, 0), P(10, 0), P(20, 0), F(30, 0), P(40, 0), P(50, 0), F(60, 0), P(70, 0), F(80, 0) };

// Failure while walking towards fight #3 (current waypoint 1 or 2) skips fight #3.
var plan = PointSkipPlanner.Create(seg, 1);
Check(plan.FailedFight == 3 && plan.NextFight == 6, "Failure on approach skips the fight being approached");
plan = PointSkipPlanner.Create(seg, 3);
Check(plan.FailedFight == 3 && plan.NextFight == 6, "Failure during the fight skips that fight");
plan = PointSkipPlanner.Create(seg, 0);
Check(plan.FailedFight == 3, "Failure at the teleport waypoint targets the first fight");
plan = PointSkipPlanner.Create(seg, -5);
Check(plan.FailedFight == 3, "Negative current index is clamped");

// A fight that already completed in this run is never the skipped point.
var doneFirst = new List<Node>(seg) { [3] = F(30, 0, done: true) };
plan = PointSkipPlanner.Create(doneFirst, 3);
Check(plan.FailedFight == 6 && plan.NextFight == 8, "Completed fight is not re-skipped; failure after it moves on");

// Last fight fails: nothing left to fight in the segment.
plan = PointSkipPlanner.Create(seg, 8);
Check(plan.HasFailedFight && plan.FailedFight == 8 && !plan.HasNextFight, "Failing last fight has no next fight");
plan = PointSkipPlanner.Create(seg, 7);
Check(plan.FailedFight == 8 && !plan.HasNextFight, "Failure on approach to last fight has no next fight");

// All fights done before the failure point: no fight to skip.
var allDone = seg.Select(n => n.IsFight ? n with { Completed = true } : n).ToList();
plan = PointSkipPlanner.Create(allDone, 4);
Check(!plan.HasFailedFight && !plan.HasNextFight, "No unfinished fight -> finish segment");
plan = PointSkipPlanner.Create(seg, 99);
Check(!plan.HasFailedFight, "Index past the end has no fight");
plan = PointSkipPlanner.Create(new List<Node>(), 0);
Check(!plan.HasFailedFight, "Empty segment has no fight");
Throws<ArgumentNullException>(() => PointSkipPlanner.Create(null!, 0), "Null segment rejected");

// Join index: closest waypoint in (failed, next] to the landing teleport point.
plan = PointSkipPlanner.Create(seg, 1);           // skip 3, next 6
Check(PointSkipPlanner.JoinIndex(seg, plan, 41, 0) == 4, "Joins at the nearest downstream path waypoint");
Check(PointSkipPlanner.JoinIndex(seg, plan, 60, 5) == 6, "Landing next to the next fight joins at the fight");
Check(PointSkipPlanner.JoinIndex(seg, plan, 0, 0) == 4, "Landing behind the skipped fight still joins after it");
Check(PointSkipPlanner.JoinIndex(seg, plan, 1000, 0) == 6, "Landing beyond the next fight joins at the next fight");
var tie = new List<Node> { P(0, 0), F(10, 0), P(20, 10), P(20, -10), F(40, 0) };
var tiePlan = PointSkipPlanner.Create(tie, 1);
Check(PointSkipPlanner.JoinIndex(tie, tiePlan, 20, 0) == 3, "Ties keep the later waypoint (no replayed path)");
foreach (var join in Enumerable.Range(0, 200).Select(k => PointSkipPlanner.JoinIndex(seg, plan, k - 50, (k % 7) - 3)))
    Check(join > plan.FailedFight && join <= plan.NextFight, "Join index always strictly after the skipped fight and not past the next fight");
Throws<ArgumentException>(() => PointSkipPlanner.JoinIndex(seg, PointSkipPlanner.Create(seg, 8), 0, 0), "No next fight -> no join index");
Throws<ArgumentException>(() => PointSkipPlanner.JoinIndex(seg, new PointSkipPlanner.Plan(-1, -1), 0, 0), "No failed fight -> no join index");
Throws<ArgumentException>(() => PointSkipPlanner.JoinIndex(seg, new PointSkipPlanner.Plan(6, 3), 0, 0), "Inverted plan rejected");
Throws<ArgumentException>(() => PointSkipPlanner.JoinIndex(seg, new PointSkipPlanner.Plan(3, 99), 0, 0), "Out-of-range next fight rejected");

// Adjacent fights: join can only be the next fight itself.
var adjacent = new List<Node> { P(0, 0), F(10, 0), F(12, 0), P(30, 0) };
var adjacentPlan = PointSkipPlanner.Create(adjacent, 1);
Check(adjacentPlan.FailedFight == 1 && adjacentPlan.NextFight == 2, "Adjacent fights plan");
Check(PointSkipPlanner.JoinIndex(adjacent, adjacentPlan, -100, -100) == 2, "Adjacent fights join at the next fight");

Check(PointSkipPlanner.MaxSkipsPerRoute == 2, "Per-route skip limit is 2");

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0, scope = "Linked production PointSkipPlanner; pure planning logic only." }));
