using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Exceptions;

var passed = 0;
void Check(bool ok, string text) { if (!ok) throw new Exception(text); passed++; }
async Task<Exception?> Catch(Func<Task> work) { try { await work(); return null; } catch (Exception e) { return e; } }
Task<int> Fail(Exception error) => Task.FromException<int>(error);
Task<bool> Restored(Exception _) => Task.FromResult(true);
var known = new TeleportFailureException("F001 map localization failed");
var timeout = new TeleportTimeoutException("60 second route budget", new OperationCanceledException());

foreach (var failure in new Exception[] { known, new MapPositionNotRecognizedException("no map match"), new TpPointNotActivate("locked") })
{
    var attempts = 0;
    var tp = new TpTask(default) { Attempt = () => { attempts++; throw failure; } };
    var result = await Catch(() => tp.Run());
    Check(result is TeleportFailureException && ReferenceEquals(result.InnerException, failure), "Known exhaustion keeps typed reason and cause");
    Check(attempts == 3, "Keep existing three-attempt bound");
}
{
    var attempts = 0;
    var tp = new TpTask(default) { Attempt = () => { if (++attempts < 3) throw known; return Task.FromResult((3d, 4d)); } };
    Check(await tp.Run() == (3d, 4d) && attempts == 3, "Transient map failure can succeed inside existing retries");
}
foreach (var failure in new Exception[] { new ArgumentException("bad map configuration"), new NullReferenceException("engine fault"),
    new RetryException("map hotkey mismatch"), new MapPositionNotRecognizedException("wrapped engine fault", new InvalidOperationException()) })
{
    var attempts = 0;
    var tp = new TpTask(default) { Attempt = () => { attempts++; throw failure; } };
    Check(ReferenceEquals(await Catch(() => tp.Run()), failure) && attempts == 1, "Unknown/configuration error immediately propagates by identity");
}
{
    var attempts = 0;
    var tp = new TpTask(default) { Attempt = () => { attempts++; throw new ArgumentException("legacy"); } };
    Check(await Catch(() => tp.Run(classify: false)) is InvalidOperationException and not TeleportFailureException && attempts == 3,
        "Ordinary callers retain their old retry/exhaustion behavior");
}
foreach (var strict in new[] { false, true })
{
    var panel = TpTask.MissingPanel(); var attempts = 0; var presses = InputHub.Foreground.Keyboard.Presses.Count;
    var tp = new TpTask(default) { Attempt = () => { attempts++; throw panel; } };
    var caught = await Catch(() => tp.Run(strict));
    Check(attempts == 1 && InputHub.Foreground.Keyboard.Presses.Count == presses + 1, "Missing panel retains one-attempt cleanup");
    Check(strict ? caught is TeleportFailureException && ReferenceEquals(caught.InnerException, panel) : ReferenceEquals(caught, panel),
        "Only strict caller classifies missing panel");
}
foreach (var failure in new Exception[] { known, TpTask.MissingPanel(), new TpPointNotActivate("locked") })
{
    using var stop = new CancellationTokenSource(); var presses = InputHub.Foreground.Keyboard.Presses.Count;
    var tp = new TpTask(stop.Token) { Attempt = () => { stop.Cancel(); throw failure; } };
    Check(await Catch(() => tp.Run()) is OperationCanceledException, "Stop wins over an attempt's failure");
    Check(InputHub.Foreground.Keyboard.Presses.Count == presses, "No cleanup ESC after stop");
}
foreach (var unknown in new Exception[] { new ArgumentException("engine failure racing F11"), new RetryException("unrelated retry racing F11") })
{
    using var stop = new CancellationTokenSource();
    var tp = new TpTask(stop.Token) { Attempt = () => { stop.Cancel(); throw unknown; } };
    var recovery = new PointLootTeleportRecovery();
    Check(await Catch(() => recovery.RunAsync(() => tp.Run(), Restored, stop.Token)) is OperationCanceledException,
        "Original caller boundary gives F11 priority over unknown error without confusing budget cancellation");
    using var mapStop = new CancellationTokenSource();
    var mapTp = new TpTask(mapStop.Token) { RecognizeRect = () => { mapStop.Cancel(); throw unknown; } };
    mapTp.Attempt = () => { mapTp.GetBigMapRect("Teyvat"); return Task.FromResult((1d, 2d)); };
    Check(await Catch(() => recovery.RunAsync(() => mapTp.Run(), Restored, mapStop.Token)) is OperationCanceledException,
        "Original F11 also wins through real rectangle/NewRetry chain");
}
foreach (var unknown in new Exception[] { new ArgumentException("engine failure after internal deadline"), new RetryException("unrelated retry after internal deadline") })
{
    var error = await Catch(() => TeleportBudget.RunAsync(token => new TpTask(token)
    {
        Attempt = () => { if (!token.WaitHandle.WaitOne(10000)) throw new Exception("budget did not expire"); throw unknown; }
    }.Run(), 20, default));
    Check(ReferenceEquals(error, unknown), "Internal budget must not relabel an unknown engine/configuration exception");
    error = await Catch(() => TeleportBudget.RunAsync(token =>
    {
        var tp = new TpTask(token) { RecognizeRect = () =>
        {
            if (!token.WaitHandle.WaitOne(10000)) throw new Exception("budget did not expire");
            throw unknown;
        } };
        tp.Attempt = () => { tp.GetBigMapRect("Teyvat"); return Task.FromResult((1d, 2d)); };
        return tp.Run();
    }, 20, default));
    Check(ReferenceEquals(error, unknown), "Real rectangle/NewRetry chain must not relabel unknown exceptions after internal deadline");
}
{
    var scans = 0;
    var tp = new TpTask(default) { RecognizeRect = () => { scans++; return (false, default); } };
    tp.Attempt = () => { tp.GetBigMapRect("Teyvat"); return Task.FromResult((1d, 2d)); };
    Check(await Catch(() => tp.Run()) is TeleportFailureException && scans == 15,
        "Real GetBigMapRect/NewRetry.Do exhaust five scans per each of three teleport attempts, classified as known");
    scans = 0;
    tp.RecognizeRect = () => { scans++; return (scans >= 5, new OpenCvSharp.Rect(1, 2, 3, 4)); };
    Check(await tp.Run() == (1d, 2d) && scans == 5, "Real rectangle helper accepts fifth recognition and avoids extra teleport attempt");
    var unknown = new ArgumentException("map config failure"); scans = 0;
    tp.RecognizeRect = () => { scans++; throw unknown; };
    Check(ReferenceEquals(await Catch(() => tp.Run()), unknown) && scans == 1,
        "Real rectangle helper does not swallow a strict unknown configuration failure");
    var unrelated = new RetryException("unrelated retry, not an authored recognition miss"); scans = 0;
    tp.RecognizeRect = () => { scans++; throw unrelated; };
    Check(ReferenceEquals(await Catch(() => tp.Run()), unrelated) && scans == 1,
        "Strict rectangle helper never retries or classifies an unrelated RetryException");
    scans = 0; tp.RecognizeRect = () => { scans++; return (false, default); };
    Check(await Catch(() => tp.Run(classify: false)) is InvalidOperationException and not TeleportFailureException && scans == 15,
        "Legacy rectangle helper retains retries and fatal exhaustion");
}
{
    var attempts = 0; var retry = new RetryException("legacy retry");
    NewRetry.Do(() => { if (++attempts < 3) throw retry; }, TimeSpan.Zero, 3);
    Check(attempts == 3, "Existing public Action overload keeps default retry semantics");
    attempts = 0;
    Check(NewRetry.Do(() => { if (++attempts < 3) throw retry; return 9; }, TimeSpan.Zero, 3) == 9 && attempts == 3,
        "Existing public generic overload keeps return value and retry semantics");
    Check(ReferenceEquals(await Catch(() => { NewRetry.Do(() => throw retry, TimeSpan.Zero, 2); return Task.CompletedTask; }), retry),
        "Existing public overload retains final exception identity");
}
foreach (var stop in new Exception[] { new OperationCanceledException(), new NormalEndException("F11") })
{
    var tp = new TpTask(default) { Attempt = () => throw stop };
    Check(ReferenceEquals(await Catch(() => tp.Run()), stop), "Stop exception never becomes local even with a live token");
}
{
    using var stop = new CancellationTokenSource();
    TaskControl.BeforeDelay = stop.Cancel;
    try
    {
        Check(await Catch(() => new TpTask(stop.Token) { Attempt = () => throw known }.Run()) is NormalEndException,
            "Stop at retry delay propagates");
    }
    finally { TaskControl.BeforeDelay = null; }
}

foreach (var failure in new Exception[] { known, timeout })
{
    var recovery = new PointLootTeleportRecovery(); var restores = 0;
    Task<bool> Restore(Exception e) { Check(ReferenceEquals(e, failure), "UI recovery gets original failure"); restores++; return Restored(e); }
    var first = await Catch(() => recovery.RunAsync(() => Fail(failure), Restore, default));
    Check(first is PointLootRouteFailureException local && local.Code == (failure is TeleportTimeoutException ? "teleport-timeout" : "teleport-failed"),
        "Confirmed failure becomes a closed local result");
    var second = await Catch(() => recovery.RunAsync(() => Fail(failure), Restore, default));
    Check(second is InvalidOperationException && ReferenceEquals(second.InnerException, failure),
        "Second failure without successful teleport is group-fatal");
    Check(restores == 2, "Persistent fault also releases/restores before stopping");
    Check(await recovery.RunAsync(() => Task.FromResult(7), Restored, default) == 7, "Successful teleport accepted");
    Check(await Catch(() => recovery.RunAsync(() => Fail(failure), Restored, default)) is PointLootRouteFailureException,
        "Only a successful teleport clears persistent-fault guard");
    Check(await Catch(() => new PointLootTeleportRecovery().RunAsync(() => Fail(failure), Restored, default)) is PointLootRouteFailureException,
        "Separate bridge has independent guard");
}
{
    var recovery = new PointLootTeleportRecovery();
    var caught = await Catch(() => recovery.RunAsync(() => TeleportBudget.RunAsync(async token =>
    {
        await Task.Delay(Timeout.Infinite, token);
        return 1;
    }, 20, default), Restored, default));
    Check(caught is PointLootRouteFailureException local && local.Code == "teleport-timeout",
        "Real budget expiry becomes local only through verified recovery, not user cancellation");
}
{
    var recovery = new PointLootTeleportRecovery();
    Check(ReferenceEquals(await Catch(() => recovery.RunAsync(() => Fail(timeout), _ => Task.FromResult(false), default)), timeout),
        "Unconfirmed recovery retains fatal timeout");
    var unknown = new InvalidOperationException("not a classified teleport failure"); var restores = 0;
    Check(ReferenceEquals(await Catch(() => recovery.RunAsync(() => Fail(unknown), _ => { restores++; return Restored(unknown); }, default)), unknown)
        && restores == 0, "Unknown exceptions never enter UI recovery or local results");
    var recoveryError = new ArgumentException("capture failure");
    Check(ReferenceEquals(await Catch(() => recovery.RunAsync(() => Fail(known), _ => throw recoveryError, default)), recoveryError),
        "UI recovery errors propagate unchanged");
}
foreach (var stage in new[] { "before", "attempt", "restore", "restore-throw", "success" })
{
    using var stop = new CancellationTokenSource(); var recovery = new PointLootTeleportRecovery(); var restores = 0; var attempts = 0;
    if (stage == "before") stop.Cancel();
    Task<int> Attempt() { attempts++; if (stage is "attempt" or "success") stop.Cancel(); return stage == "success" ? Task.FromResult(1) : Fail(known); }
    Task<bool> Restore(Exception _) { restores++; if (stage.StartsWith("restore")) stop.Cancel(); if (stage == "restore-throw") throw new Exception("cleanup race"); return Task.FromResult(true); }
    Check(await Catch(() => recovery.RunAsync(Attempt, Restore, stop.Token)) is OperationCanceledException, "Cancellation priority: " + stage);
    Check(stage != "before" || attempts == 0, "Pre-cancelled route sends no attempt");
    Check(stage != "attempt" || restores == 0, "Cancelled attempt sends no recovery input");
}

// Real frame gate; screenshot/input/delay boundaries are stubs. Unknown screens receive no keys.
{
    var presses = InputHub.Foreground.Keyboard.Presses.Count; var releases = InputHub.ReleaseCalls;
    Bv.MainUi = true; Bv.BigMapUi = true; Bv.RevivePrompt = false; Bv.MainUiReads = 0;
    Check(await PointLootMainUi.RestoreAfterRouteTeleportFailureAsync(known, default), "Map failure recovers to stable main UI");
    Check(InputHub.ReleaseCalls == releases + 1 && Bv.MainUiReads == 3, "Release inputs and require all three fresh main-UI frames");
    Check(InputHub.Foreground.Keyboard.Presses.Count == presses + 1, "Only recognized map receives ESC");
    Bv.BigMapUi = false; Bv.MainUi = false; presses = InputHub.Foreground.Keyboard.Presses.Count;
    Check(!await PointLootMainUi.RestoreAfterRouteTeleportFailureAsync(timeout, default), "Loading/unknown UI cannot be declared safe");
    Check(InputHub.Foreground.Keyboard.Presses.Count == presses, "Unknown/loading screens receive no Enter, ESC or clicks");
    Bv.MainUi = true; Bv.RevivePrompt = true;
    Check(!await PointLootMainUi.RestoreAfterRouteTeleportFailureAsync(known, default), "Revive prompt is unsafe");
    Bv.RevivePrompt = false;
    using var stop = new CancellationTokenSource();
    Bv.OnMapRead = () => { stop.Cancel(); return true; };
    try
    {
        Check(await Catch(() => PointLootMainUi.RestoreAfterRouteTeleportFailureAsync(known, stop.Token)) is OperationCanceledException,
            "Stop while recognizing map prevents keypress");
        Check(InputHub.Foreground.Keyboard.Presses.Count == presses, "No ESC after map recognition cancellation");
    }
    finally { Bv.OnMapRead = null; }
    using var lateStop = new CancellationTokenSource(); Bv.MainUiReads = 0;
    Bv.OnRead = count => { if (count == 3) lateStop.Cancel(); };
    try
    {
        Check(await Catch(() => PointLootMainUi.RestoreAfterRouteTeleportFailureAsync(known, lateStop.Token)) is OperationCanceledException,
            "Stop on last confirmation frame wins over safe result");
    }
    finally { Bv.OnRead = null; }
}

// Actual bridge owns the guard across routes, sets it only for the structured API, and cleans route leases.
{
    const string route = "{\"positions\":[{\"action\":\"fight\"}]}";
    var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(route))).ToLowerInvariant();
    var policy = JsonSerializer.Serialize(new { schema = 1, routeSha256 = hash, points = new[] { new { pointIndex = 0, gather = false, windowMs = 1000 } } });
    CancellationContext.Instance.Cts = new(); var bridge = new AutoPathingScript("test", null);
    PathExecutor.Execute = async (executor, _, token) =>
    {
        Check(executor.TeleportRecovery is not null, "Structured route receives bridge guard");
        await executor.TeleportRecovery!.RunAsync(() => Fail(known), Restored, token);
    };
    var result = JsonDocument.Parse(await bridge.RunWithLootPolicyResult(route, policy)).RootElement;
    Check(!result.GetProperty("success").GetBoolean() && result.GetProperty("code").GetString() == "teleport-failed"
        && result.GetProperty("routeSha256").GetString() == hash, "Identity-bound route failure, never success/CD");
    Check(bridge.GetPointLootContext() == "null" && !PointLootRecoveryScope.IsActive, "Failed route clears permit and recovery scope");
    PathExecutor.Execute = (executor, _, _) => { executor.SuccessEnd = true; return Task.CompletedTask; };
    await bridge.RunWithLootPolicyResult(route, policy);
    PathExecutor.Execute = async (executor, _, token) =>
        await executor.TeleportRecovery!.RunAsync(() => Fail(known), Restored, token);
    Check(await Catch(() => bridge.RunWithLootPolicyResult(route, policy)) is InvalidOperationException, "Same bridge stops persistent fault on next route");
    PathExecutor.Execute = (executor, _, _) => { Check(executor.TeleportRecovery is null, "Legacy route gets no local recovery guard"); executor.SuccessEnd = true; return Task.CompletedTask; };
    await bridge.RunWithLootPolicy(route, policy); await bridge.Run(route);
}

Console.WriteLine(JsonSerializer.Serialize(new { passed, failed = 0,
    scope = "Linked production GetBigMapRect/NewRetry.Do, teleport retry/budget, error classification, recovery/frame gate and script bridge; game/input boundaries stubbed." }));
