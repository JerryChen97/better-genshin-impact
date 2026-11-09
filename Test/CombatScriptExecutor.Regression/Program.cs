using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;

var passed = 0;
void Check(bool value, string message)
{
    if (!value) throw new Exception(message);
    passed++;
}
CombatScript Script(Action action) => new([CombatScriptParser.CurrentAvatarName], [new CombatCommand(action)]);
async Task<Exception> Catch(Func<Task> action)
{
    try { await action(); }
    catch (Exception error) { return error; }
    throw new Exception("Expected command failure to reach the caller.");
}

var scenarios = new (string Name, Action Command, Type Expected)[]
{
    ("unconfirmed recovery UI", () => PointLootRecoveryScope.RecoverThenRetry(
        () => { }, () => false, default, true, new RetryException("defeat")), typeof(InvalidOperationException)),
    ("unrestored statue timeout", () => PointLootRecoveryScope.RecoverThenRetry(
        () => throw new TeleportTimeoutException("statue", new TimeoutException()), () => true, default, true,
        new RetryException("defeat"), _ => false), typeof(TeleportTimeoutException)),
    ("persistent statue timeout", () =>
    {
        StatueTimeoutGuard.Converted();
        PointLootRecoveryScope.RecoverThenRetry(
            () => throw new TeleportTimeoutException("second statue", new TimeoutException()), () => true, default, true,
            new RetryException("defeat"), _ => true);
    }, typeof(TeleportTimeoutException)),
    ("user stop", () => throw new NormalEndException("stop"), typeof(NormalEndException)),
    ("cancellation", () => throw new OperationCanceledException(), typeof(OperationCanceledException)),
    ("unknown command failure", () => throw new InvalidOperationException("command"), typeof(InvalidOperationException))
};

foreach (var scenario in scenarios)
{
    StatueTimeoutGuard.TeleportCompleted();
    var scenes = new CombatScenes();
    var disposed = CombatScenes.Disposed;
    var continued = false;
    var error = await Catch(async () =>
    {
        // This is the strict shared-executor path used by policy JSON pre-actions.
        await CombatScriptExecutor.ExecuteAsync(Script(scenario.Command), default, TaskControl.Logger, scenes,
            propagateErrors: true);
        continued = true;
    });
    Check(error.GetType() == scenario.Expected, scenario.Name + " preserves the failure type");
    Check(!continued, scenario.Name + " cannot be treated as a successful pre-action");
    Check(CombatScenes.Disposed == disposed, scenario.Name + " does not dispose caller-owned scenes");

    StatueTimeoutGuard.TeleportCompleted();
    using (PointLootRecoveryScope.Enter())
    {
        error = await Catch(() => new CombatScriptHandler().RunAsync(default,
            new WaypointForTrack { CombatScript = Script(scenario.Command) }));
        Check(error.GetType() == scenario.Expected, scenario.Name + " propagates through the real pathing handler");
    }
    Check(!PointLootRecoveryScope.IsActive, scenario.Name + " leaves no active recovery scope");
}
StatueTimeoutGuard.TeleportCompleted();

// An ordinary caller retains the shared executor's existing log-and-return behavior.
await CombatScriptExecutor.ExecuteAsync(Script(() => throw new InvalidOperationException("legacy")), default,
    TaskControl.Logger, new CombatScenes());
Check(true, "ordinary shared-executor callers retain the default contract");
await new CombatScriptHandler().RunAsync(default,
    new WaypointForTrack { CombatScript = Script(() => throw new InvalidOperationException("legacy pathing")) });
Check(true, "ordinary combat_script callers retain their legacy contract");

foreach (var strict in new[] { false, true })
{
    var retry = new RetryException("retry");
    var error = await Catch(() => CombatScriptExecutor.ExecuteAsync(Script(() => throw retry), default,
        TaskControl.Logger, new CombatScenes(), propagateErrors: strict));
    Check(ReferenceEquals(error, retry), "retry identity is preserved in both modes");
}

var failure = new InvalidOperationException("original failure");
var ownedDisposed = CombatScenes.Disposed;
var capturesDisposed = Capture.Disposed;
var ownedError = await Catch(() => CombatScriptExecutor.ExecuteAsync(Script(() => throw failure), default,
    TaskControl.Logger, propagateErrors: true));
Check(ReferenceEquals(ownedError, failure), "owned-scene execution preserves the original exception");
Check(CombatScenes.Disposed == ownedDisposed + 1, "owned scenes are disposed on strict failure");
Check(Capture.Disposed == capturesDisposed + 1, "team-initialization capture is disposed");

CombatScenes.DisposeError = new Exception("cleanup failure");
try
{
    ownedError = await Catch(() => CombatScriptExecutor.ExecuteAsync(Script(() => throw failure), default,
        TaskControl.Logger, propagateErrors: true));
    Check(ReferenceEquals(ownedError, failure), "cleanup cannot replace the command failure");
}
finally { CombatScenes.DisposeError = null; }

using (var cts = new CancellationTokenSource())
{
    cts.Cancel();
    var executed = false;
    var error = await Catch(() => CombatScriptExecutor.ExecuteAsync(Script(() => executed = true), cts.Token,
        TaskControl.Logger, new CombatScenes(), propagateErrors: true));
    Check(error is OperationCanceledException && !executed, "a stopped strict caller executes no command");
}
Console.WriteLine($"PASS {passed} assertions (linked production executor, combat_script handler and recovery classifier; no game/native input).");
