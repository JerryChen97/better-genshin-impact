using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;

const string route = "{\"positions\":[{\"action\":\"\"},{\"action\":\"fight\"}]}";
var passed = 0;
string Policy(int window = 1000, bool gather = true) => JsonSerializer.Serialize(new
{
    schema = 1, routeSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route))),
    points = new[] { new { pointIndex = 1, gather, windowMs = window } }
});
PointLootSession Session(int window = 1000) => PointLootSession.Parse(Policy(window), route, 2, i => i == 1);
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
async Task Throws<T>(Func<Task> work) where T : Exception
{
    try { await work(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name);
}
async Task<T> Catch<T>(Func<Task> work) where T : Exception
{
    try { await work(); } catch (T e) { passed++; return e; }
    throw new Exception("Expected " + typeof(T).Name);
}
var warnings = Microsoft.Extensions.Logging.LoggingExtensions.Warnings;
async Task<string> Context(PointLootSession session)
{
    for (var n = 0; n < 200; n++)
    {
        var context = session.GetContextJson();
        if (context != "null") return JsonDocument.Parse(context).RootElement.GetProperty("token").GetString()!;
        await Task.Delay(5);
    }
    throw new Exception("Window did not open");
}

// A normal battle return (including configured timeout) gates pickup until both combat and gather finish.
using (var session = Session())
{
    var fight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var gather = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = session.RunFightAsync(1, default, () => fight.Task, _ => gather.Task);
    Check(session.GetContextJson() == "null", "No F lease during combat");
    fight.SetResult();
    await Task.Yield();
    Check(session.GetContextJson() == "null", "No F lease while gathering");
    gather.SetResult();
    var token = await Context(session);
    Check(!session.Complete("wrong"), "Foreign token rejected");
    Check(!run.IsCompleted, "Route awaits window acknowledgement");
    Check(session.Complete(token), "Exact acknowledgement accepted");
    await run;
    Check(session.GetContextJson() == "null" && !session.Complete(token), "Consumed lease cannot be reused");
}

// Recovery/failure never opens a window; retry is a new attempt at the original point index.
using (var session = Session())
{
    var gathered = false;
    await Throws<InvalidOperationException>(() => session.RunFightAsync(1, default,
        () => Task.FromException(new InvalidOperationException("recovery")), _ => { gathered = true; return Task.CompletedTask; }));
    Check(!gathered && session.GetContextJson() == "null", "Recovery does not gather/open");
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask, _ => Task.CompletedTask);
    var token = await Context(session);
    Check(JsonDocument.Parse(session.GetContextJson()).RootElement.GetProperty("pointIndex").GetInt32() == 1, "Original index retained");
    session.SetEnabled(false);
    Check(session.GetContextJson() == "null", "Master revokes synchronously");
    session.SetEnabled(true);
    await run;
    Check(!session.Complete(token), "Re-enable cannot resurrect a token");
    var retry = session.RunFightAsync(1, default, () => Task.CompletedTask, _ => Task.CompletedTask);
    var retryToken = await Context(session);
    Check(retryToken != token, "Retry gets a unique token");
    session.Complete(retryToken);
    await retry;
}

using (var session = Session())
{
    session.SetEnabled(false);
    var fight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var run = session.RunFightAsync(1, default, () => fight.Task,
        attempt => { Check(!session.CanGather(attempt), "Disabled-at-start attempt stays revoked"); return Task.CompletedTask; });
    session.SetEnabled(true);
    fight.SetResult();
    await run;
    Check(session.GetContextJson() == "null", "Enabling mid-fight does not resurrect revoked attempt");
}

using (var session = Session())
using (var cts = new CancellationTokenSource())
{
    var run = session.RunFightAsync(1, cts.Token, () => Task.CompletedTask, _ => Task.CompletedTask);
    var token = await Context(session);
    cts.Cancel();
    await Throws<OperationCanceledException>(() => run);
    Check(session.GetContextJson() == "null" && !session.Complete(token), "Cancel clears and propagates");
}
using (var session = Session(25))
{
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask, _ => Task.CompletedTask);
    var token = await Context(session);
    await run;
    Check(session.GetContextJson() == "null" && !session.Complete(token), "Budget expiry invalidates lease");
}
using (var session = Session())
{
    await session.RunFightAsync(0, default, () => Task.CompletedTask,
        attempt => { Check(!session.CanGather(attempt), "Unlisted point does not gather"); return Task.CompletedTask; });
    Check(session.GetContextJson() == "null", "Unlisted point has no window");
}

// Run the actual public script bridge with a no-game executor boundary.
var bridge = new AutoPathingScript("", null);
bridge.SetPointLootEnabled(false);
PathExecutor.Execute = async (executor, task, ct) =>
{
    Check(task.Positions.Count == 2 && task.Positions[0].Action == "", "Full route preserved");
    Check(RunnerContext.Instance.StopCount == 1, "Trigger suppression owns whole route");
    await executor.PointLootSession!.RunFightAsync(1, ct, () => Task.CompletedTask,
        attempt => { Check(!executor.PointLootSession.CanGather(attempt), "Pre-run master retained"); return Task.CompletedTask; });
    executor.SuccessEnd = true;
};
var created = PathExecutor.Created;
await bridge.RunWithLootPolicy(route, Policy());
Check(PathExecutor.Created == created + 1 && RunnerContext.Instance.StopCount == 0 && bridge.GetPointLootContext() == "null", "One executor; owned suppression cleaned");

created = PathExecutor.Created;
await Throws<ArgumentException>(() => bridge.RunWithLootPolicy(route + " ", Policy()));
Check(PathExecutor.Created == created, "Wrong route hash causes no executor/game action");
await Throws<ArgumentException>(() => bridge.RunWithLootPolicy(route, Policy().Replace("\"pointIndex\":1", "\"pointIndex\":0")));
await Throws<ArgumentException>(() => bridge.RunWithLootPolicy(route, Policy(30001)));

var hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
PathExecutor.Execute = async (executor, _, _) => { await hold.Task; executor.SuccessEnd = true; };
var held = bridge.RunWithLootPolicy(route, Policy());
await Throws<InvalidOperationException>(() => bridge.RunWithLootPolicy(route, Policy()));
created = PathExecutor.Created;
await bridge.Run(route); // Legacy API still logs/swallows errors; it cannot enter the active new route.
Check(PathExecutor.Created == created, "Legacy overlap does not create an executor");
hold.SetResult();
await held;

PathExecutor.Execute = (_, _, _) => Task.FromException(new InvalidOperationException("failed route"));
await Throws<InvalidOperationException>(() => bridge.RunWithLootPolicy(route, Policy()));
Check(RunnerContext.Instance.StopCount == 0 && bridge.GetPointLootContext() == "null", "Fault cleanup");
await bridge.Run(route); // Existing legacy swallowing contract remains intact.
Check(RunnerContext.Instance.StopCount == 0, "Legacy remains unscoped");
PathExecutor.Execute = (_, _, _) => Task.CompletedTask;
await Throws<InvalidOperationException>(() => bridge.RunWithLootPolicy(route, Policy()));

using (var cts = new CancellationTokenSource())
{
    CancellationContext.Instance.Cts = cts;
    PathExecutor.Execute = async (_, _, ct) => { cts.Cancel(); await Task.Delay(1, ct); };
    await Throws<OperationCanceledException>(() => bridge.RunWithLootPolicy(route, Policy()));
    Check(RunnerContext.Instance.StopCount == 0 && bridge.GetPointLootContext() == "null", "Cancelled run releases ownership");
}
CancellationContext.Instance.Cts = new CancellationTokenSource();

// Exercise the actual native gather implementation against input/vision boundaries, including cancellation cleanup.
using (var session = Session())
{
    var attempt = session.BeginFight(1);
    var enteredCdWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RunnerContext.Instance.Scenes.Avatar.Wait = async token => { enteredCdWait.SetResult(); await Task.Delay(Timeout.Infinite, token); };
    var released = BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls;
    var holds = TaskControl.HoldCalls;
    var gather = PointLootGatherTask.RunAsync(session, attempt, default);
    await enteredCdWait.Task;
    session.SetEnabled(false);
    session.SetEnabled(true);
    await gather;
    Check(TaskControl.HoldCalls == holds, "Disable during CD cancels native gather before E");
    Check(BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls >= released + 2, "Native gather cancellation releases input");
    Check(!RunnerContext.Instance.Scenes.Token.CanBeCanceled, "Avatar cancellation context restored after gather");
    Check(!session.CanGather(attempt), "Re-enable cannot revive in-flight native gather");
    session.EndFight(attempt);
}
using (var session = Session())
using (var cts = new CancellationTokenSource())
{
    var attempt = session.BeginFight(1);
    var enteredCdWait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    RunnerContext.Instance.Scenes.Avatar.Wait = async token => { enteredCdWait.SetResult(); await Task.Delay(Timeout.Infinite, token); };
    var gather = PointLootGatherTask.RunAsync(session, attempt, cts.Token);
    await enteredCdWait.Task;
    cts.Cancel();
    await Throws<OperationCanceledException>(() => gather);
    Check(RunnerContext.Instance.Scenes.Token == cts.Token, "Route cancellation preserved after native gather cleanup");
    session.EndFight(attempt);
}
RunnerContext.Instance.Scenes.Avatar.Wait = _ => Task.CompletedTask;
using (var session = Session())
{
    var attempt = session.BeginFight(1);
    var holds = TaskControl.HoldCalls;
    var plunges = TaskControl.PlungeCalls;
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(TaskControl.HoldCalls == holds + 1 && TaskControl.PlungeCalls == plunges + 1, "Eligible point forces current-party gather");
    session.EndFight(attempt);
}
using (var session = Session())
{
    // An unstable UI revokes only this point's gather and window; it is not a route failure.
    var attempt = session.BeginFight(1);
    BetterGenshinImpact.GameTask.Common.BgiVision.Bv.MainUi = false;
    warnings.Clear();
    var holds = TaskControl.HoldCalls;
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(TaskControl.HoldCalls == holds && !session.CanGather(attempt) && !session.CanOpenWindow(attempt),
        "Unstable UI revokes this attempt instead of failing the route");
    Check(warnings.Contains("逐点拾取：主界面未稳定，放弃本点的聚拢和拾取窗口，继续路线"), "Unstable UI is reported");
    await session.WaitForWindowAsync(attempt, default);
    Check(session.GetContextJson() == "null", "Revoked attempt opens no pickup window");
    BetterGenshinImpact.GameTask.Common.BgiVision.Bv.MainUi = true;
    session.EndFight(attempt);
}

using (var session = Session())
{
    var attempt = session.BeginFight(1);
    RunnerContext.Instance.Scenes.Avatar.Switch = () => { session.SetEnabled(false); return false; };
    warnings.Clear();
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(!session.CanGather(attempt), "TrySwitch cancelled false is optional-gather cancellation, not route failure");
    Check(warnings.Count == 0, "A revoked switch is a cancellation, not a reported switch failure");
    RunnerContext.Instance.Scenes.Avatar.Switch = null;
    session.EndFight(attempt);
}
// Optional gather failures skip only the gather; the confirmed pickup window still opens.
using (var session = Session())
{
    RunnerContext.Instance.ScenesMissing = true;
    warnings.Clear();
    var holdBefore = TaskControl.HoldCalls;
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    if (run.IsFaulted) await run; // Preserve the native exception in a pre-fix reproduction.
    var token = await Context(session);
    Check(TaskControl.HoldCalls == holdBefore && warnings.Contains("逐点拾取：无法识别当前队伍，跳过聚拢"),
        "Unrecognized party skips only the gather");
    Check(session.Complete(token), "Unrecognized party still opens the pickup window");
    await run;
    RunnerContext.Instance.ScenesMissing = false;
}
using (var session = Session())
{
    RunnerContext.Instance.Scenes.Avatar.Switch = () => false; // e.g. Kazuha down or the switch key lost; attempt still live
    warnings.Clear();
    var holdBefore = TaskControl.HoldCalls;
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    if (run.IsFaulted) await run;
    var token = await Context(session);
    Check(TaskControl.HoldCalls == holdBefore && warnings.Contains("逐点拾取：切换枫原万叶失败，跳过聚拢"),
        "Failed Kazuha switch skips only the gather");
    Check(session.Complete(token), "Failed Kazuha switch still opens the pickup window");
    await run;
    Check(!RunnerContext.Instance.Scenes.Token.CanBeCanceled, "Failed switch restores the parent cancellation context");
    RunnerContext.Instance.Scenes.Avatar.Switch = null;
}
using (var session = Session())
{
    var attempt = session.BeginFight(1);
    var enteredGather = false;
    var releasesBefore = BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls;
    RunnerContext.Instance.Scenes.Avatar.Switch = () => { enteredGather = true; return true; };
    TaskControl.BeforeDelay = () => { if (enteredGather) session.SetEnabled(false); };
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(!session.CanGather(attempt), "NormalEndException from revoked Delay does not terminate route");
    Check(enteredGather && BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls >= releasesBefore + 2,
        "Revoked native gather Delay executes its key-release finally");
    Check(!RunnerContext.Instance.Scenes.Token.CanBeCanceled, "Revoked gather restores the parent cancellation context");
    TaskControl.BeforeDelay = null;
    RunnerContext.Instance.Scenes.Avatar.Switch = null;
    session.EndFight(attempt);
}
using (var session = Session())
using (var cts = new CancellationTokenSource())
{
    var attempt = session.BeginFight(1);
    var enteredGather = false;
    var releasesBefore = BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls;
    RunnerContext.Instance.Scenes.Avatar.Switch = () => { enteredGather = true; return true; };
    TaskControl.BeforeDelay = () => { if (enteredGather) cts.Cancel(); };
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException>(
        () => PointLootGatherTask.RunAsync(session, attempt, cts.Token));
    Check(enteredGather && BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls >= releasesBefore + 2,
        "Cancelled native gather Delay propagates and executes its key-release finally");
    Check(RunnerContext.Instance.Scenes.Token == cts.Token, "Cancelled gather restores the parent cancellation token");
    TaskControl.BeforeDelay = null;
    RunnerContext.Instance.Scenes.Avatar.Switch = null;
    session.EndFight(attempt);
}
using (var session = Session())
{
    var attempt = session.BeginFight(1);
    RunnerContext.Instance.BeforeGetScenes = token => { session.SetEnabled(false); token.ThrowIfCancellationRequested(); return Task.CompletedTask; };
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(!session.CanGather(attempt), "Party-cache setup also receives the revocable linked token");
    RunnerContext.Instance.BeforeGetScenes = null;
    session.EndFight(attempt);
}
using (var session = Session())
{
    var attempt = session.BeginFight(0);
    BetterGenshinImpact.GameTask.Common.BgiVision.Bv.MainUi = false;
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(true, "Unlisted point does not perform additional UI validation");
    session.EndFight(attempt);
    attempt = session.BeginFight(1);
    session.SetEnabled(false);
    await PointLootGatherTask.RunAsync(session, attempt, default);
    Check(true, "Master-off point does not perform additional UI validation");
    BetterGenshinImpact.GameTask.Common.BgiVision.Bv.MainUi = true;
    session.EndFight(attempt);
}
using (var session = Session())
{
    var attempt = session.BeginFight(1);
    RunnerContext.Instance.Scenes.Avatar.Wait = token => Task.Delay(Timeout.Infinite, token);
    await PointLootGatherTask.RunAsync(session, attempt, default); // Real 15-second optional gather deadline.
    Check(session.CanGather(attempt), "Optional gather deadline does not revoke the point policy");
    var window = session.WaitForWindowAsync(attempt, default);
    var token = await Context(session);
    Check(session.Complete(token), "Pickup remains available after optional gather budget expiry");
    await window;
    RunnerContext.Instance.Scenes.Avatar.Wait = _ => Task.CompletedTask;
    session.EndFight(attempt);
}

// A recovery entered by Avatar.TrySwitch uses the route token, so optional cancellation cannot
// interrupt a teleport and accidentally publish a window for the old point at the statue.
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = recoveryToken =>
    {
        session.SetEnabled(false);
        Check(recoveryToken == routeCts.Token && !recoveryToken.IsCancellationRequested,
            "Master cancellation does not cancel entered recovery");
        throw new BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException("recovered: retry original route segment");
    };
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(session.GetContextJson() == "null", "Recovery after master cancellation cannot publish a stale window");
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = null;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = recoveryToken =>
    {
        // This is the gather's real 15-second timer, independent from the token used inside recovery.
        Check(RunnerContext.Instance.Scenes.Token.WaitHandle.WaitOne(TimeSpan.FromSeconds(20)), "Optional gather budget elapsed during recovery");
        Check(recoveryToken == routeCts.Token && !recoveryToken.IsCancellationRequested,
            "Optional budget does not cancel entered recovery");
        throw new BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException("recovered after budget: retry original segment");
    };
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(session.GetContextJson() == "null", "Recovery after budget cancellation cannot publish a stale window");
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = null;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = recoveryToken =>
    {
        routeCts.Cancel();
        Check(recoveryToken.IsCancellationRequested, "Route cancellation still cancels recovery");
        throw new BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException("cancel recovery");
    };
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(session.GetContextJson() == "null", "Cancelled recovery cannot publish a stale window");
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = null;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    // A statue teleport that exceeded its budget is typed (before 11/08 it surfaced as NormalEndException). The
    // gather filters must not swallow it as an optional stop, even when the attempt was revoked meanwhile.
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = _ =>
    {
        session.SetEnabled(false);
        throw new BetterGenshinImpact.GameTask.AutoTrackPath.TeleportTimeoutException("单次传送超过 120 秒",
            new BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException("取消自动任务"));
    };
    await Throws<BetterGenshinImpact.GameTask.AutoTrackPath.TeleportTimeoutException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(RunnerContext.Instance.Scenes.Token == routeCts.Token, "A propagating recovery timeout still restores the route token");
    RunnerContext.Instance.Scenes.Avatar.RecoverSwitch = null;
}

// PointLootMainUi: the multi-frame main-UI gate behind statue recovery. It never sends game input while confirming.
{
    var timeout = new BetterGenshinImpact.GameTask.AutoTrackPath.TeleportTimeoutException("单次传送超过 120 秒",
        new BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException("取消自动任务"));
    void Frames(params bool[] frames) { Bv.MainUiFrames.Clear(); foreach (var f in frames) Bv.MainUiFrames.Enqueue(f); }
    var holdBefore = TaskControl.HoldCalls;
    var plungeBefore = TaskControl.PlungeCalls;

    Frames(true, true, false, true, true, true);
    Bv.MainUiReads = 0;
    Check(await PointLootMainUi.ConfirmAsync(default) && Bv.MainUiReads == 6, "A broken run restarts the 3-frame count");
    Frames();
    Bv.MainUi = false;
    Bv.MainUiReads = 0;
    Check(!await PointLootMainUi.ConfirmAsync(default) && Bv.MainUiReads == PointLootMainUi.MaxObservations,
        "No main UI within the observation budget is not confirmed");
    Bv.MainUi = true;
    Bv.RevivePrompt = true;
    Check(!await PointLootMainUi.ConfirmAsync(default), "A revive prompt is never a confirmed main UI");
    Bv.RevivePrompt = false;
    Bv.LowHp = true;
    Check(await PointLootMainUi.ConfirmAsync(default) && !await PointLootMainUi.ConfirmHealedAsync(default)
          && warnings.Contains("回七天神像后未能确认主界面稳定且血量正常，按普通重试处理"), "Healed confirmation also requires normal HP");
    Bv.LowHp = false;
    Check(await PointLootMainUi.ConfirmHealedAsync(default), "Stable main UI with normal HP is a healed confirmation");
    Check(TaskControl.HoldCalls == holdBefore && TaskControl.PlungeCalls == plungeBefore, "Confirmation sends no game input");

    var releasesBefore = BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls;
    BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts = 0;
    Frames(Enumerable.Repeat(false, PointLootMainUi.MaxObservations).Concat(new[] { true, true, true }).ToArray());
    Check(await PointLootMainUi.RestoreAfterTeleportTimeoutAsync(timeout, default)
          && BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts == 2
          && BetterGenshinImpact.Core.Input.InputHub.ReleaseCalls == releasesBefore + 1,
        "Restore releases inputs first and confirms in a second round");
    Frames();
    Bv.MainUi = false;
    BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts = 0;
    Check(!await PointLootMainUi.RestoreAfterTeleportTimeoutAsync(timeout, default)
          && BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts == 2
          && warnings[^1] == "七天神像传送超时后未能确认回到主界面，按原规则停止", "An unconfirmed restore gives up after two rounds");
    Bv.MainUi = true;

    using (var cancelled = new CancellationTokenSource())
    {
        cancelled.Cancel();
        Bv.MainUiReads = 0;
        BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts = 0;
        await Throws<OperationCanceledException>(() => PointLootMainUi.ConfirmAsync(cancelled.Token));
        await Throws<OperationCanceledException>(() => PointLootMainUi.RestoreAfterTeleportTimeoutAsync(timeout, cancelled.Token));
        Check(Bv.MainUiReads == 0 && BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask.Starts == 0,
            "A stopped run neither captures nor presses ESC");
    }
    using (var cancelled = new CancellationTokenSource())
    {
        Frames(false, false);
        TaskControl.BeforeDelay = () => cancelled.Cancel();
        await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException>(() => PointLootMainUi.ConfirmAsync(cancelled.Token));
        TaskControl.BeforeDelay = null;
        Frames();
    }
}

// Real combat CheckFightFinish cancels the party-setup animation and returns immediately.
// Its first capture may legitimately lack Paimon; two consecutive fresh main-UI captures
// are required before optional gather/window, without sending an extra key to close the UI.
using (var session = Session())
{
    foreach (var frame in new[] { false, false, true, false, true, true }) Bv.MainUiFrames.Enqueue(frame);
    Bv.MainUiReads = 0;
    var holdBefore = TaskControl.HoldCalls;
    var initialDelayChecks = 0;
    TaskControl.BeforeDelay = () =>
    {
        if (Bv.MainUiReads < 6)
        {
            Check(TaskControl.HoldCalls == holdBefore && session.GetContextJson() == "null",
                "Transient/flickering post-combat UI sends no gather input and exposes no token");
            initialDelayChecks++;
        }
    };
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    if (run.IsFaulted) await run; // Preserve the native exception in a pre-fix reproduction.
    var token = await Context(session);
    Check(initialDelayChecks == 5, "UI confirmation resets after a flicker and waits for two consecutive frames");
    Check(TaskControl.HoldCalls == holdBefore + 1, "Eligible gather runs only after UI settles");
    Check(session.Complete(token), "Settled UI permits the normal pickup window");
    await run;
    TaskControl.BeforeDelay = null;
}
using (var session = Session())
{
    RunnerContext.Instance.Scenes.KazuhaPresent = false;
    foreach (var frame in new[] { true, true, false, false, true, true }) Bv.MainUiFrames.Enqueue(frame);
    Bv.MainUiReads = 0;
    var holdBefore = TaskControl.HoldCalls;
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    var token = await Context(session);
    Check(Bv.MainUiReads == 6, "No-Kazuha return rechecks UI after party lookup before exposing a token");
    Check(TaskControl.HoldCalls == holdBefore, "No Kazuha does not synthesize gather input");
    Check(session.Complete(token), "No Kazuha still permits confirmed pickup");
    await run;
    RunnerContext.Instance.Scenes.KazuhaPresent = true;
}
using (var session = Session(30000))
{
    Bv.MainUi = false;
    Bv.MainUiReads = 0;
    var holdBefore = TaskControl.HoldCalls;
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    Check(await Task.WhenAny(run, Task.Delay(5000)) == run, "Unknown UI ends the point without waiting for a pickup window");
    await run;
    Check(Bv.MainUiReads == 20 && TaskControl.HoldCalls == holdBefore,
        "Unknown UI stops after bounded observations without gather input");
    Check(session.GetContextJson() == "null", "Persistent unknown UI never opens a token");
    Bv.MainUi = true;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    // A revive prompt is a defeat: the native statue recovery runs with the route token and ends with a retry.
    Bv.RevivePrompt = true;
    var recoveries = BetterGenshinImpact.GameTask.AutoFight.Model.Avatar.RecoverCalls;
    var holdBefore = TaskControl.HoldCalls;
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.RetryException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(BetterGenshinImpact.GameTask.AutoFight.Model.Avatar.RecoverCalls == recoveries + 1 &&
          BetterGenshinImpact.GameTask.AutoFight.Model.Avatar.RecoverToken == routeCts.Token,
        "Revive prompt enters the native statue recovery with the route token");
    Check(TaskControl.HoldCalls == holdBefore && session.GetContextJson() == "null",
        "Revive prompt cannot be reinterpreted as a stable main UI");
    Bv.RevivePrompt = false;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
using (PointLootRecoveryScope.Enter())
{
    // Inside a point-policy route the recovery ends with the verified revive retry, which PathExecutor retries
    // and, once exhausted, skips per point (revive-retry-exhausted) instead of aborting the whole group.
    var holdBefore = TaskControl.HoldCalls;
    TaskControl.BeforeDelay = () => { if (TaskControl.HoldCalls > holdBefore) Bv.RevivePrompt = true; };
    var retry = await Catch<PointLootRouteRetryException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(retry.Reason == PointLootRouteFailureReason.ReviveRetryExhausted &&
          PointLootRouteFailureException.CodeFor(retry.Reason) == "revive-retry-exhausted",
        "A defeat seen after gathering is the typed revive retry");
    Check(BetterGenshinImpact.GameTask.AutoFight.Model.Avatar.RecoverToken == routeCts.Token,
        "Post-gather recovery uses the route token, never the optional gather budget");
    Check(TaskControl.HoldCalls == holdBefore + 1 && session.GetContextJson() == "null",
        "Post-gather revive prompt opens no pickup window");
    TaskControl.BeforeDelay = null;
    Bv.RevivePrompt = false;
}
using (var session = Session())
{
    Bv.MainUi = false;
    TaskControl.BeforeDelay = () => session.SetEnabled(false);
    await session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    Check(session.GetContextJson() == "null", "Master-off while waiting revokes the attempt without a UI error");
    TaskControl.BeforeDelay = null;
    Bv.MainUi = true;
}
using (var session = PointLootSession.Parse(Policy(gather: false), route, 2, i => i == 1))
{
    Bv.MainUiFrames.Enqueue(false);
    Bv.MainUiFrames.Enqueue(true);
    Bv.MainUiFrames.Enqueue(true);
    Bv.MainUiReads = 0;
    RunnerContext.Instance.BeforeGetScenes = _ => throw new Exception("Window-only point must not inspect the party");
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    var token = await Context(session);
    Check(Bv.MainUiReads == 3 && session.Complete(token), "Window-only point waits for stable UI without gathering");
    await run;
    RunnerContext.Instance.BeforeGetScenes = null;
}
using (var session = Session())
using (var routeCts = new CancellationTokenSource())
{
    Bv.MainUi = false;
    TaskControl.BeforeDelay = routeCts.Cancel;
    await Throws<BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException>(() =>
        session.RunFightAsync(1, routeCts.Token, () => Task.CompletedTask,
            attempt => PointLootGatherTask.RunAsync(session, attempt, routeCts.Token)));
    Check(session.GetContextJson() == "null", "Route cancellation during UI settling propagates and leaves no token");
    TaskControl.BeforeDelay = null;
    Bv.MainUi = true;
}
using (var session = Session(30000))
{
    RunnerContext.Instance.Scenes.KazuhaPresent = false;
    RunnerContext.Instance.BeforeGetScenes = _ => { Bv.MainUi = false; return Task.CompletedTask; };
    var run = session.RunFightAsync(1, default, () => Task.CompletedTask,
        attempt => PointLootGatherTask.RunAsync(session, attempt, default));
    Check(await Task.WhenAny(run, Task.Delay(5000)) == run, "Lost UI after party lookup ends the point without a window wait");
    await run;
    Check(session.GetContextJson() == "null", "No-Kazuha lookup that loses UI context cannot expose a window");
    RunnerContext.Instance.Scenes.KazuhaPresent = true;
    RunnerContext.Instance.BeforeGetScenes = null;
    Bv.MainUi = true;
}
Console.WriteLine($"PASS {passed} assertions (linked production session + public bridge + native gather; no game/native input).");
