using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoPick;
using Vanara.PInvoke;

var temporaryRoot = Path.Combine(Path.GetTempPath(), "bgi-global-guard-test-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(Path.Combine(temporaryRoot, "User"));
var rulesFile = Path.Combine(temporaryRoot, "User", "global-pick-rules.json");
var checks = 0;
void Check(bool valid, string name) { checks++; if (!valid) throw new Exception(name); }
void Rules(string white = "[\"兽肉\",\"禽肉\",\"教官\"]", string black = "[\"破损的面具\"]") =>
    File.WriteAllText(rulesFile, "{\"schema\":1,\"whiteList\":" + white + ",\"blackList\":" + black + "}");
void Reset(string text = "兽肉")
{
    Probe.ScreenText = text; Probe.InputEvents.Clear(); Probe.PromptExists = true; Probe.IsMainUi = true;
    Probe.ActionKey = KeyId.F; Probe.Captures = 0; Probe.AfterCapture = null; Probe.AfterKeyDown = null;
    Probe.RouteActive = true; Probe.Suspended = false; Probe.PointToken = "point-token";
    TaskContext.Instance().Config.AutoPickConfig.PickKey = "F";
    CancellationContext.Instance.Cts.Dispose(); CancellationContext.Instance.Cts = new();
    InputHub.Foreground = new Probe.Channel();
    Rules(); GlobalPickupPolicy.Rules = new GlobalPickupRules(temporaryRoot);
}
try
{
    var picker = new AutoPickTrigger(globalOnly: true);
    foreach (var text in new[] { "兽肉", "胡萝卜", "破损的面具" })
    {
        Reset(text);
        using var oldFrame = new CaptureContent("兽肉"); picker.OnCapture(oldFrame);
        Check(Probe.InputEvents.Count == (text == "兽肉" ? 2 : 0), "Native fallback binds old approval to live row: " + text);
    }
    Reset();
    Probe.AfterCapture = count => { if (count == 2) Rules(white: "[]"); };
    Check(!GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "兽肉", true) && Probe.InputEvents.Count == 0,
        "Global whitelist removed while capturing is vetoed at final input");
    foreach (var configured in new[] { "", " ", " F ", "not-a-key", null })
    {
        Reset(); TaskContext.Instance().Config.AutoPickConfig.PickKey = configured!; Probe.ActionKey = KeyId.None;
        InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_W);
        Check(Probe.InputEvents.Count == 1, "Invalid/normalized pickup key does not break movement: " + configured);
        InputHub.Foreground.Keyboard.KeyUp(User32.VK.VK_W);
    }
    foreach (var action in new[] { KeyId.None, KeyId.Unknown })
    {
        Reset(); Probe.ActionKey = action; InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_W);
        Check(Probe.InputEvents.Count == 2, "Unbound pickup action does not break unrelated keys: " + action);
    }
    foreach (var direct in new[] { false, true })
    {
        Reset("破损的面具"); Probe.ActionKey = KeyId.X1;
        if (direct) InputHub.Foreground.Mouse.XButtonClick(1); else InputHub.Foreground.Keyboard.KeyPress(User32.VK.VK_XBUTTON1);
        Check(Probe.InputEvents.Count == 0, "Mouse pickup binding cannot bypass blacklist: " + direct);
        InputHub.Foreground.Mouse.XButtonUp(1);
        Check(Probe.InputEvents.Count == 1, "Mouse release remains available for cleanup");
    }
    Reset(); Rules("[]", "[]"); GlobalPickupPolicy.Rules = new GlobalPickupRules(temporaryRoot, () => 0);
    Check(!GlobalPickupPolicy.Rules.GetSnapshot().HasRules, "Start from valid empty snapshot");
    Rules("[]", "[\"兽肉\"]"); InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_F);
    Check(Probe.InputEvents.Count == 0, "First blacklist update bypasses no empty-cache fast path");
    Rules("[]", "[]"); InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_F);
    Check(Probe.InputEvents.Count == 2 && !InputHub.Foreground.IsKeyDown(User32.VK.VK_F), "Empty policy cannot leave unchecked pickup hold");
    Reset(); Probe.IsMainUi = false; Probe.PromptExists = false; File.WriteAllText(rulesFile, "{");
    InputHub.Foreground.Keyboard.KeyDown(User32.VK.VK_F);
    Check(Probe.InputEvents.Count == 1 && InputHub.Foreground.IsKeyDown(User32.VK.VK_F), "Broken pickup rules do not block non-world held note");
    Check(Probe.Captures == 1, "Non-pickup UI exits before delayed second pickup capture");
    InputHub.Foreground.ReleaseAll(); Check(Probe.InputEvents.Count == 2, "Non-world note cleanup releases key");
    Reset("教官的胸花");
    Check(GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "教官", true, true), "Explicit artifact-family target accepts full piece name");
    Reset("教官的胸花"); Rules(black: "[\"教官的胸花\"]");
    Check(!GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "教官", true, true), "Exact denied piece vetoes artifact family");
    Reset("兽肉干"); Check(!GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "兽肉", true), "Food target remains exact");
    foreach (var change in new[] { "window", "route", "cancel", "pause" })
    {
        Reset("不祥的面具"); var api = new AutoPathingScript();
        Probe.AfterCapture = count =>
        {
            if (count != 2) return;
            if (change == "window") Probe.PointToken = null;
            if (change == "route") Probe.RouteActive = false;
            if (change == "cancel") CancellationContext.Instance.Cts.Cancel();
            if (change == "pause") Probe.Suspended = true;
        };
        Check(!api.TryPickup("不祥的面具", "point", "point-token") && Probe.InputEvents.Count == 0,
            "Final API rechecks lifecycle after capture: " + change);
    }
    Reset(); var liveApi = new AutoPathingScript();
    Check(liveApi.TryPickup("兽肉", "global", Probe.RouteHash) && Probe.InputEvents.Count == 2, "Live global route permits exact food");
    Reset("不祥的面具"); Check(new AutoPathingScript().TryPickup("不祥的面具", "point", "point-token"), "Live point token permits approved material");
    Reset(); Check(!new AutoPathingScript().TryPickup("兽肉", "global", "other-route"), "Global SDK does not reuse another route identity");
    Reset(); Probe.AfterKeyDown = () => Probe.ScreenText = "胡萝卜";
    var concurrent = new[] { Task.Run(() => GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "兽肉", true)),
        Task.Run(() => GlobalPickupInputGuard.TryPickup(User32.VK.VK_F, "兽肉", true)) };
    Task.WaitAll(concurrent);
    Check(concurrent.Count(task => task.Result) == 1 && Probe.InputEvents.Count(item => item.StartsWith("down:")) == 1,
        "Concurrent same-row approvals cannot both send pickup after selected row changes");
    Console.WriteLine($"PASS: {checks} linked native trigger/input/guard/API assertions (capture and backend stubbed)");
}
finally
{
    if (!temporaryRoot.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup");
    Directory.Delete(temporaryRoot, true);
}
