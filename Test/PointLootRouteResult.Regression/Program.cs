using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoTrackPath;
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
async Task<Exception?> CatchAsync(Func<Task> run)
{
    try { await run(); return null; } catch (Exception e) { return e; }
}
Exception? CatchSync(Action run)
{
    try { run(); return null; } catch (Exception e) { return e; }
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
foreach (var reason in Enum.GetValues<PointLootRouteFailureReason>())
    Check(PointLootRouteFailureException.ReasonFor(PointLootRouteFailureException.CodeFor(reason)) == reason,
        "Skip codes map back to their closed reason: " + reason);
try { PointLootRouteFailureException.ReasonFor("unclassified"); throw new Exception("Expected the closed code set"); }
catch (ArgumentOutOfRangeException) { Check(true, "Unknown skip code is rejected"); }

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

// TpTask.Tp / TpToStatueOfTheSeven run every teleport through the production TeleportBudget helper. Expiry is
// synchronised on the budget token itself, so a loaded host cannot reorder the timer and a fixed delay.
Check(await TeleportBudget.RunAsync(_ => Task.FromResult(7), 1000, default) == 7, "Teleport within its budget returns its result");
{
    var expired = await CatchAsync(() => TeleportBudget.RunAsync<int>(async t => { await Task.Delay(10_000, t); return -1; }, 1000, default));
    Check(expired is TeleportTimeoutException { InnerException: OperationCanceledException } && expired is TimeoutException
          && expired.Message == "单次传送超过 1 秒", "Own budget expiry inside an await is a typed teleport timeout");
    // 11/08 C055 shape: the timer fires during synchronous work and the next check is TaskControl.Delay's entry.
    expired = await CatchAsync(() => TeleportBudget.RunAsync<int>(async t =>
    {
        if (!t.WaitHandle.WaitOne(10_000)) return -1;
        await TaskControl.Delay(10, t);
        return -2;
    }, 30, default));
    Check(expired is TeleportTimeoutException { InnerException: NormalEndException }, "Own budget expiry seen as NormalEnd at a Delay entry is a typed teleport timeout");
}
foreach (var stopKind in new[] { "oce", "normal-end" })
{
    using var outer = new CancellationTokenSource();
    var stopped = await CatchAsync(() => TeleportBudget.RunAsync<int>(async t =>
    {
        outer.Cancel();
        if (stopKind == "oce") await Task.Delay(10_000, t);
        else await TaskControl.Delay(10, t);
        return -1;
    }, 60_000, outer.Token));
    Check(stopped is not TimeoutException && (stopKind == "oce" ? stopped is OperationCanceledException : stopped is NormalEndException),
        "A real stop keeps its own exception: " + stopKind);
}
{
    using var outer = new CancellationTokenSource();
    var stopped = await CatchAsync(() => TeleportBudget.RunAsync<int>(async t =>
    {
        if (!t.WaitHandle.WaitOne(10_000) || outer.IsCancellationRequested) return -1; // the budget expired on its own
        outer.Cancel(); // then a real stop arrives before classification
        await TaskControl.Delay(10, t);
        return -2;
    }, 30, outer.Token));
    Check(stopped is NormalEndException, "A stop after budget expiry stays a stop");
    var failure = new InvalidOperationException("传送失败");
    Check(ReferenceEquals(await CatchAsync(() => TeleportBudget.RunAsync<int>(t => t.WaitHandle.WaitOne(10_000) ? throw failure : Task.FromResult(-1), 30, default)), failure),
        "A non-stop teleport failure after expiry is not relabelled");
    var early = new NormalEndException("other stop");
    Check(ReferenceEquals(await CatchAsync(() => TeleportBudget.RunAsync<int>(_ => throw early, 60_000, default)), early),
        "A NormalEnd before budget expiry is not relabelled");
}

// Defeat recovery whose statue teleport exceeded its budget (11/08 C055), and the shared persistent-fault guard.
var statueTimeout = new TeleportTimeoutException("单次传送超过 120 秒", new NormalEndException("取消自动任务"));
var guardWarning = "七天神像传送再次超时（上次超时后还没有完成过神像传送），视为持续故障，按原规则停止";
{
    StatueTimeoutGuard.TeleportCompleted();
    var restores = 0;
    TeleportTimeoutException? received = null;
    checkedUi = 0;
    var caught = CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout,
        () => { checkedUi++; return true; }, default, true, request, t => { restores++; received = t; return true; }));
    Check(caught is PointLootRouteRetryException typed && typed.ToTerminalFailure(default).Code == "revive-retry-exhausted"
          && typed.Message == request.Message + PointLootRecoveryScope.StatueTimeoutSuffix, "Restored statue timeout is the verified revive retry");
    Check(restores == 1 && ReferenceEquals(received, statueTimeout) && checkedUi == 0,
        "A statue timeout is converted only through the restore check, which receives the timeout");
    var warningsBefore = Microsoft.Extensions.Logging.LoggingExtensions.Warnings.Count;
    caught = CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout, () => true, default, true, request,
        _ => { restores++; return true; }));
    Check(ReferenceEquals(caught, statueTimeout) && restores == 1, "A repeated statue timeout before any completed statue teleport propagates unchanged");
    Check(Microsoft.Extensions.Logging.LoggingExtensions.Warnings.Skip(warningsBefore).Contains(guardWarning), "The persistent-fault stop is logged");
    StatueTimeoutGuard.TeleportCompleted();
    caught = CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout, () => true, default, true, request,
        _ => { restores++; return true; }));
    Check(caught is PointLootRouteRetryException && restores == 2, "A completed statue teleport re-arms the conversion");
}
foreach (var (label, restoreCase, defeatedCase) in new (string, Func<TeleportTimeoutException, bool>?, bool)[]
             { ("restore false", _ => false, true), ("no restore", null, true), ("not defeated", _ => true, false) })
{
    StatueTimeoutGuard.TeleportCompleted();
    Check(ReferenceEquals(CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout, () => true,
        default, defeatedCase, request, restoreCase)), statueTimeout), "Statue timeout propagates unchanged: " + label);
    Check(StatueTimeoutGuard.MayConvert, "An unconverted timeout does not arm the persistent-fault guard: " + label);
}
{
    StatueTimeoutGuard.TeleportCompleted();
    var restoreFailure = new InvalidOperationException("capture failed during restore");
    Check(ReferenceEquals(CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout, () => true,
        default, true, request, _ => throw restoreFailure)), restoreFailure) && StatueTimeoutGuard.MayConvert,
        "A failing restore propagates its own error");
    foreach (var other in new Exception[] { new NormalEndException("F11 stop"), new TimeoutException("other timeout"), new OperationCanceledException("stop") })
    {
        var restores = 0;
        Check(ReferenceEquals(CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw other, () => true,
            default, true, request, _ => { restores++; return true; })), other) && restores == 0,
            "Only a teleport timeout may be restored: " + other.GetType().Name);
    }
}
foreach (var stage in new[] { "before-classification", "restore-true", "restore-throws" })
{
    StatueTimeoutGuard.TeleportCompleted();
    using var cancelled = new CancellationTokenSource();
    var restores = 0;
    await Throws<OperationCanceledException>(() => Task.Run(() => PointLootRecoveryScope.RecoverThenRetry(
        () => { if (stage == "before-classification") cancelled.Cancel(); throw statueTimeout; }, () => true, cancelled.Token, true, request,
        _ => { restores++; cancelled.Cancel(); if (stage == "restore-throws") throw new NormalEndException("stop during restore"); return true; })));
    Check(stage == "before-classification" ? restores == 0 : restores == 1, "Cancellation wins at stage " + stage);
    Check(StatueTimeoutGuard.MayConvert, "A cancelled recovery does not arm the persistent-fault guard: " + stage);
}

// Low-HP / revive-modal statue trip on a policy route (PathExecutor.RecoverAtStatueThenRetry).
{
    const string lowHp = "回血完成后重试路线";
    const string lowHpTimeout = "去七天神像回血时传送超时，已返回主界面，重试路线";
    var trips = 0;
    var confirms = 0;
    var restores = 0;
    Func<Task> completedTrip = () => { trips++; return Task.CompletedTask; };
    Func<Task> timedOutTrip = () => { trips++; return Task.FromException(statueTimeout); };
    Func<Task<bool>> ConfirmWith(bool result) => () => { confirms++; return Task.FromResult(result); };
    Func<TeleportTimeoutException, Task<bool>> RestoreWith(bool result) => _ => { restores++; return Task.FromResult(result); };
    void Reset() { trips = confirms = restores = 0; StatueTimeoutGuard.TeleportCompleted(); }

    Reset();
    var retry = await PointLootRecoveryScope.RecoverAtStatueAsync(completedTrip, ConfirmWith(true), RestoreWith(true), default, lowHp, lowHpTimeout);
    Check(retry is PointLootRouteRetryException typed && typed.ToTerminalFailure(default).Code == "revive-retry-exhausted" && retry.Message == lowHp
          && (trips, confirms, restores) == (1, 1, 0), "Confirmed healed low-HP trip is the verified retry");
    Reset();
    retry = await PointLootRecoveryScope.RecoverAtStatueAsync(completedTrip, ConfirmWith(false), RestoreWith(true), default, lowHp, lowHpTimeout);
    Check(retry.GetType() == typeof(RetryException) && retry.Message == lowHp && restores == 0, "Unconfirmed low-HP trip keeps the plain retry");
    Reset();
    retry = await PointLootRecoveryScope.RecoverAtStatueAsync(timedOutTrip, ConfirmWith(true), RestoreWith(true), default, lowHp, lowHpTimeout);
    Check(retry is PointLootRouteRetryException && retry.Message == lowHpTimeout && (confirms, restores) == (0, 1),
        "Restored low-HP statue timeout is the verified retry with its own message");
    // Shared guard: the next timeout before any completed statue teleport propagates, here as a defeat recovery.
    Check(ReferenceEquals(CatchSync(() => PointLootRecoveryScope.RecoverThenRetry(() => throw statueTimeout, () => true, default, true, request,
        _ => throw new Exception("restore must not run"))), statueTimeout), "Low-HP and defeat conversions share the persistent-fault guard");
    Check(ReferenceEquals(await CatchAsync(() => PointLootRecoveryScope.RecoverAtStatueAsync(timedOutTrip, ConfirmWith(true), RestoreWith(true), default, lowHp, lowHpTimeout)), statueTimeout)
          && restores == 1, "A repeated low-HP statue timeout propagates without another restore");
    Reset();
    Check(ReferenceEquals(await CatchAsync(() => PointLootRecoveryScope.RecoverAtStatueAsync(timedOutTrip, ConfirmWith(true), RestoreWith(false), default, lowHp, lowHpTimeout)), statueTimeout)
          && confirms == 0 && StatueTimeoutGuard.MayConvert, "Unrestored low-HP statue timeout propagates unchanged");
    foreach (var other in new Exception[] { new NormalEndException("F11"), new TimeoutException("other"), new InvalidOperationException("传送失败"), new RetryException("movement retry") })
    {
        Reset();
        Check(ReferenceEquals(await CatchAsync(() => PointLootRecoveryScope.RecoverAtStatueAsync(() => Task.FromException(other), ConfirmWith(true), RestoreWith(true), default, lowHp, lowHpTimeout)), other)
              && (confirms, restores) == (0, 0), "Low-HP recovery failure propagates unchanged: " + other.GetType().Name);
    }
    foreach (var failingCheck in new[] { "confirm", "restore" })
    {
        Reset();
        var checkFailure = new InvalidOperationException("capture failed in " + failingCheck);
        Func<Task<bool>> confirmCheck = failingCheck == "confirm" ? () => Task.FromException<bool>(checkFailure) : ConfirmWith(true);
        Func<TeleportTimeoutException, Task<bool>> restoreCheck = failingCheck == "restore" ? _ => Task.FromException<bool>(checkFailure) : RestoreWith(true);
        var caught = await CatchAsync(() => PointLootRecoveryScope.RecoverAtStatueAsync(failingCheck == "confirm" ? completedTrip : timedOutTrip,
            confirmCheck, restoreCheck, default, lowHp, lowHpTimeout));
        Check(ReferenceEquals(caught, checkFailure) && StatueTimeoutGuard.MayConvert, "A failing " + failingCheck + " check propagates its own error");
    }
    foreach (var stage in new[] { "entry", "recovery", "timeout", "restore", "confirm" })
    {
        Reset();
        using var cancelled = new CancellationTokenSource();
        if (stage == "entry") cancelled.Cancel();
        Func<Task> recover = stage switch
        {
            "recovery" => () => { trips++; cancelled.Cancel(); return Task.CompletedTask; },
            "timeout" => () => { trips++; cancelled.Cancel(); return Task.FromException(statueTimeout); },
            "restore" => timedOutTrip,
            _ => completedTrip
        };
        await Throws<OperationCanceledException>(() => PointLootRecoveryScope.RecoverAtStatueAsync(recover,
            () => { confirms++; cancelled.Cancel(); return Task.FromResult(true); },
            _ => { restores++; cancelled.Cancel(); return Task.FromResult(true); }, cancelled.Token, lowHp, lowHpTimeout));
        var expected = stage switch
        {
            "entry" => (0, 0, 0),
            "recovery" or "timeout" => (1, 0, 0),
            "restore" => (1, 0, 1),
            _ => (1, 1, 0)
        };
        Check((trips, confirms, restores) == expected, "Cancellation wins at stage " + stage + " without further UI work");
        Check(StatueTimeoutGuard.MayConvert, "A cancelled low-HP recovery does not arm the persistent-fault guard: " + stage);
    }
    StatueTimeoutGuard.TeleportCompleted();
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
