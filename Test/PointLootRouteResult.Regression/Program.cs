using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

const string route = "{\"positions\":[{\"action\":\"fight\"}]}";
var routeHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route))).ToLowerInvariant();
var plan = JsonSerializer.Serialize(new { schema = 1, routeSha256 = routeHash,
    points = new[] { new { pointIndex = 0, gather = true, windowMs = 1000 } } });
var passed = 0;
void Check(bool value, string description) { if (!value) throw new Exception(description); passed++; }
async Task Throws<T>(Func<Task> run) where T : Exception
{
    try { await run(); } catch (T) { passed++; return; }
    throw new Exception("Expected " + typeof(T).Name);
}
AutoPathingScript Fresh()
{
    CancellationContext.Instance.Cts = new CancellationTokenSource();
    PathExecutor.Execute = (executor, _, _) => { executor.SuccessEnd = true; return Task.CompletedTask; };
    return new AutoPathingScript("mock-no-files", null);
}
void Clean(AutoPathingScript bridge, int cleared)
{
    Check(bridge.GetPointLootContext() == "null", "No leftover point context");
    Check(!PointLootRecoveryScope.IsActive, "Recovery scope unwound");
    Check(RunnerContext.Instance.StopCount == 0, "AutoPick suspension unwound");
    Check(PathExecutor.Cleared == cleared + 1, "Exact executor cleanup");
    Check(!bridge.CompletePointLootWindow("stale"), "Stale token rejected");
}

var successBridge = Fresh();
var success = JsonDocument.Parse(await successBridge.RunWithLootPolicyResult(route, plan)).RootElement;
Check(success.EnumerateObject().Count() == 5, "Exact result fields");
Check(success.GetProperty("schema").GetInt32() == 1 && success.GetProperty("success").GetBoolean(), "Explicit success schema");
Check(success.GetProperty("code").GetString() == "completed", "Explicit success code");
Check(success.GetProperty("routeSha256").GetString() == routeHash, "Result bound to route text SHA");

foreach (var reason in Enum.GetValues<PointLootRouteFailureReason>())
{
    var bridge = Fresh();
    PointLootSession.Attempt? oldAttempt = null;
    PathExecutor.Execute = (executor, _, _) => {
        oldAttempt = executor.PointLootSession!.BeginFight(0);
        throw new PointLootRouteFailureException(reason, "specific local failure");
    };
    var cleared = PathExecutor.Cleared;
    var result = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement;
    Check(!result.GetProperty("success").GetBoolean(), "Local positioning failure is not success");
    Check(result.GetProperty("code").GetString() == new PointLootRouteFailureException(reason, "").Code, "Exact failure code");
    Check(result.GetProperty("routeSha256").GetString() == routeHash, "Failure route identity");
    Check(oldAttempt!.Revoked.IsCancellationRequested, "Failure revokes old attempt");
    Clean(bridge, cleared);
    // The deployed API is deliberately NOT softened for old JS runtimes.
    await Throws<PointLootRouteFailureException>(() => bridge.RunWithLootPolicy(route, plan));
    Check(bridge.GetPointLootContext() == "null", "Old API rejection cleans context");
    PathExecutor.Execute = (executor, _, _) => { executor.SuccessEnd = true; return Task.CompletedTask; };
    Check(JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement.GetProperty("success").GetBoolean(), "Same bridge accepts next route after local failure");
}

foreach (var failure in new Exception[] {
    new InvalidOperationException("Point loot stopped because the game is not in the main UI."),
    new HandledException("达成结束条件，结束地图追踪"),
    new RetryException("检测到复苏界面，存在角色被击败"),
    new NormalEndException("Cancelled at Delay entry"),
    new OperationCanceledException("cancelled"),
    new ArgumentException("configuration failure") })
{
    var bridge = Fresh();
    PathExecutor.Execute = (_, _, _) => throw failure;
    var cleared = PathExecutor.Cleared;
    Exception? caught = null;
    try { await bridge.RunWithLootPolicyResult(route, plan); } catch (Exception error) { caught = error; }
    Check(ReferenceEquals(caught, failure), "Nonlocal failure propagates unchanged: " + failure.GetType().Name);
    Clean(bridge, cleared);
}

var cancelBridge = Fresh();
PathExecutor.Execute = (_, _, _) => {
    CancellationContext.Instance.Cts.Cancel();
    throw new PointLootRouteFailureException(PointLootRouteFailureReason.TargetTooFar, "concurrent cancellation");
};
await Throws<OperationCanceledException>(() => cancelBridge.RunWithLootPolicyResult(route, plan));
Check(cancelBridge.GetPointLootContext() == "null", "Cancellation takes precedence and cleans context");

var badPlanBridge = Fresh();
var created = PathExecutor.Created;
await Throws<ArgumentException>(() => badPlanBridge.RunWithLootPolicyResult(route, plan.Replace(routeHash, new string('0', 64))));
Check(PathExecutor.Created == created, "SHA mismatch fails before executor construction");
await Throws<JsonException>(() => badPlanBridge.RunWithLootPolicyResult(route, "{bad"));
Check(PathExecutor.Created == created, "Malformed policy fails before executor construction");
Check(JsonDocument.Parse(await badPlanBridge.RunWithLootPolicyResult(route, plan)).RootElement.GetProperty("success").GetBoolean(), "Validation failure releases run lock");

var unfinishedBridge = Fresh();
PathExecutor.Execute = (_, _, _) => Task.CompletedTask;
await Throws<InvalidOperationException>(() => unfinishedBridge.RunWithLootPolicyResult(route, plan));

// Concurrent callers cannot replace the active session; cleanup belongs to its owner.
var concurrentBridge = Fresh();
var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
PathExecutor.Execute = async (executor, _, _) => { await gate.Task; executor.SuccessEnd = true; };
var pending = concurrentBridge.RunWithLootPolicyResult(route, plan);
await Throws<InvalidOperationException>(() => concurrentBridge.RunWithLootPolicyResult(route, plan));
Check(RunnerContext.Instance.StopCount == 1, "Rejected second call preserves active AutoPick suspension");
gate.SetResult();
Check(JsonDocument.Parse(await pending).RootElement.GetProperty("success").GetBoolean(), "First route remains intact");
Check(RunnerContext.Instance.StopCount == 0, "Final AutoPick cleanup");

// Actual production retry classifier: only the final policy attempt becomes a soft failure.
foreach (var reason in new[] { PointLootRouteFailureReason.RouteStuck, PointLootRouteFailureReason.ReviveRetryExhausted })
{
    var bridge = Fresh();
    var attempts = 0;
    PointLootSession.Attempt? lastAttempt = null;
    PathExecutor.Execute = (executor, _, ct) => {
        Check(PointLootRecoveryScope.IsActive, "Only new API enters strict recovery scope");
        for (var i = 0; i < 2; i++)
        {
            lastAttempt = executor.PointLootSession!.BeginFight(0);
            attempts++;
            try
            {
                throw new PointLootRouteRetryException(reason, "specific retry exhausted");
            }
            catch (RetryException retry)
            {
                PointLootRouteRetryException.ThrowIfExhausted(retry, executor.PointLootSession is not null, i == 1, ct);
            }
            finally { executor.PointLootSession!.Revoke(); }
        }
        return Task.CompletedTask;
    };
    var cleared = PathExecutor.Cleared;
    var result = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement;
    Check(attempts == 2, "First typed retry still retries; only exhaustion returns");
    Check(!result.GetProperty("success").GetBoolean(), "Exhaustion never reports completed");
    Check(result.GetProperty("code").GetString() == (reason == PointLootRouteFailureReason.RouteStuck ? "route-stuck" : "revive-retry-exhausted"), "Closed exhaustion code");
    Check(lastAttempt!.Revoked.IsCancellationRequested, "Exhausted retry revokes attempt before return");
    Clean(bridge, cleared);
    attempts = 0;
    await Throws<PointLootRouteFailureException>(() => bridge.RunWithLootPolicy(route, plan));
    Check(attempts == 2 && !PointLootRecoveryScope.IsActive, "Old policy API rejects after unchanged retries and cleans scope");
    using var cancelled = new CancellationTokenSource();
    cancelled.Cancel();
    await Throws<OperationCanceledException>(() => Task.Run(() => PointLootRouteRetryException.ThrowIfExhausted(
        new PointLootRouteRetryException(reason, "cancel takes priority"), true, true, cancelled.Token)));
    PointLootRouteRetryException.ThrowIfExhausted(new PointLootRouteRetryException(reason, "legacy"), false, true, default);
    Check(true, "Legacy executor does not acquire new terminal behavior");
}
var genericRetry = new RetryException("unclassified recovery failure");
try { PointLootRouteRetryException.ThrowIfExhausted(genericRetry, true, true, default); throw new Exception("Expected original retry"); }
catch (RetryException failure) { Check(ReferenceEquals(genericRetry, failure), "Unknown retry propagates same instance"); }

// Actual strict recovery helper: no inferred success when recovery, UI or cancellation fails.
var recovered = 0;
var checkedUi = 0;
RetryException request = new("defeated");
try
{
    PointLootRecoveryScope.RecoverThenRetry(() => recovered++, () => { checkedUi++; return true; }, default, true, request);
    throw new Exception("Expected verified defeat retry");
}
catch (PointLootRouteRetryException retry)
{
    Check(recovered == 1 && checkedUi == 1, "Defeat retry requires completed recovery and fresh UI check");
    Check(retry.ToTerminalFailure(default).Code == "revive-retry-exhausted", "Verified defeat has closed reason");
}
await Throws<InvalidOperationException>(() => Task.Run(() => PointLootRecoveryScope.RecoverThenRetry(
    () => { }, () => false, default, true, request)));
foreach (var recoveryFailure in new Exception[] { new InvalidOperationException("capture unavailable"), new RetryException("recovery movement failed"),
    new NormalEndException("F11 stop"), new OperationCanceledException("stop"), new ArgumentException("invalid statue config") })
{
    checkedUi = 0;
    Exception? caught = null;
    try { PointLootRecoveryScope.RecoverThenRetry(() => throw recoveryFailure, () => { checkedUi++; return true; }, default, true, request); }
    catch (Exception failure) { caught = failure; }
    Check(ReferenceEquals(caught, recoveryFailure) && checkedUi == 0, "Recovery failure is never converted: " + recoveryFailure.GetType().Name);
}
foreach (var cancellationStage in new[] { "entry", "recovery", "ui" })
{
    using var cancelled = new CancellationTokenSource();
    if (cancellationStage == "entry") cancelled.Cancel();
    await Throws<OperationCanceledException>(() => Task.Run(() => PointLootRecoveryScope.RecoverThenRetry(
        () => { if (cancellationStage == "recovery") cancelled.Cancel(); },
        () => { if (cancellationStage == "ui") cancelled.Cancel(); return true; }, cancelled.Token, true, request)));
}
try { PointLootRecoveryScope.RecoverThenRetry(() => { }, () => true, default, false, request); throw new Exception("Expected original non-defeat retry"); }
catch (RetryException retry) { Check(ReferenceEquals(retry, request), "Swimming/non-defeat remains original retry"); }
await Throws<InvalidOperationException>(() => Task.Run(() => PointLootRecoveryScope.RecoverThenRetry(
    () => { }, () => throw new InvalidOperationException("screenshot not initialized"), default, true, request)));

foreach (var cancellationStage in new[] { "recovery-throw", "ui-false", "ui-throw" })
{
    using var cancelled = new CancellationTokenSource();
    await Throws<OperationCanceledException>(() => Task.Run(() => PointLootRecoveryScope.RecoverThenRetry(
        () => { if (cancellationStage == "recovery-throw") { cancelled.Cancel(); throw new InvalidOperationException("recovery failed while stopping"); } },
        () => { cancelled.Cancel(); if (cancellationStage == "ui-throw") throw new InvalidOperationException("capture stopped"); return false; },
        cancelled.Token, true, request)));
}

// Real async-local lifetime: nested and concurrent calls cannot leak strict recovery into legacy runs.
Check(!PointLootRecoveryScope.IsActive, "No recovery scope before route");
using (var outer = PointLootRecoveryScope.Enter())
{
    Check(PointLootRecoveryScope.IsActive, "Recovery scope active");
    using (PointLootRecoveryScope.Enter()) { await Task.Yield(); Check(PointLootRecoveryScope.IsActive, "Nested scope flows across await"); }
    Check(PointLootRecoveryScope.IsActive, "Nested dispose restores outer scope");
}
Check(!PointLootRecoveryScope.IsActive, "Outer disposal restores empty scope");
var legacyBridge = Fresh();
PathExecutor.Execute = (executor, _, _) => { Check(!PointLootRecoveryScope.IsActive, "Legacy Run has no strict recovery scope"); return Task.CompletedTask; };
await legacyBridge.Run(route);

// Per-point skip result mapping (executor movement stubbed; skip bookkeeping injected).
{
    var bridge = Fresh();
    bool? enabledSeen = null;
    PathExecutor.Execute = (executor, _, _) => { enabledSeen = executor.PointSkipEnabled; executor.SuccessEnd = true; return Task.CompletedTask; };
    var plain = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement;
    Check(enabledSeen == true, "New result API enables per-point skip");
    Check(plain.GetProperty("code").GetString() == "completed", "No skip keeps the plain completed code");
    await bridge.RunWithLootPolicy(route, plan);
    Check(enabledSeen == false, "Old reject-on-failure API never enables per-point skip");

    var cleared = PathExecutor.Cleared;
    PathExecutor.Execute = (executor, _, _) => {
        executor.Completed.Add(0);
        executor.Skipped.Add((3, "revive-retry-exhausted"));
        executor.SuccessEnd = true;
        return Task.CompletedTask;
    };
    var partial = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement;
    Check(partial.EnumerateObject().Count() == 5, "Skip result keeps the exact five fields");
    Check(partial.GetProperty("success").GetBoolean() && partial.GetProperty("code").GetString() == "completed-with-skips", "Partial completion is a distinct success code");
    Check(partial.GetProperty("message").GetString()!.Contains("#3(revive-retry-exhausted)"), "Skip message names the original point and reason");
    Check(partial.GetProperty("routeSha256").GetString() == routeHash, "Skip result bound to route SHA");
    Clean(bridge, cleared);

    cleared = PathExecutor.Cleared;
    PathExecutor.Execute = (executor, _, _) => {
        executor.Skipped.Add((1, "route-stuck"));
        executor.Skipped.Add((4, "target-too-far"));
        executor.SuccessEnd = true;
        return Task.CompletedTask;
    };
    var none = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, plan)).RootElement;
    Check(!none.GetProperty("success").GetBoolean(), "All points skipped is still a local failure (no CD)");
    Check(none.GetProperty("code").GetString() == "target-too-far", "All-skipped failure uses the last skip reason");
    Clean(bridge, cleared);

    cleared = PathExecutor.Cleared;
    PathExecutor.Execute = (executor, _, _) => { executor.Skipped.Add((2, "route-stuck")); return Task.CompletedTask; };
    await Throws<InvalidOperationException>(() => bridge.RunWithLootPolicyResult(route, plan));
    Check(bridge.GetPointLootContext() == "null", "Unfinished route with skips is never reported as success");
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0, scope = "Linked production bridge/session/retry classifier/recovery scope; platform and executor movement are stubbed. No game API." }));
