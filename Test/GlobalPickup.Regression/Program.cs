using BetterGenshinImpact.GameTask.AutoPick;
using Newtonsoft.Json.Linq;

if (args is ["--snapshot", var installRoot])
{
    var snapshot = new GlobalPickupRules(installRoot).GetSnapshot(true);
    Console.WriteLine(snapshot.ToJson());
    return snapshot.IsValid ? 0 : 1;
}

var root = Path.Combine(Path.GetTempPath(), "bgi-global-pick-test-" + Guid.NewGuid().ToString("N"));
var user = Path.Combine(root, "User");
var accounts = Path.Combine(user, "JsScript", "AutoHoeingOneDragon", "blacklists");
Directory.CreateDirectory(accounts);
var checks = 0;
var errors = new List<Exception>();
long time = 0;
void Check(bool condition, string message)
{
    checks++;
    if (!condition) throw new Exception(message);
}
void Write(string relative, string text) => File.WriteAllText(Path.Combine(user, relative), text);
void Rules(string white = "[\"兽肉\",\"禽肉\"]", string black = "[\"破损的面具\"]") =>
    Write("global-pick-rules.json", "{\"schema\":1,\"whiteList\":" + white + ",\"blackList\":" + black + "}");
var loader = new GlobalPickupRules(root, () => time, errors.Add);
try
{
    Check(!loader.GetSnapshot().IsValid, "Missing global policy must fail closed");
    Check(loader.GetSnapshot().Decide("兽肉") == GlobalPickupDecision.Deny, "Invalid snapshot denies food");
    Rules();
    Write("pick_white_lists.txt", "教官\r\n兽肉\n");
    Write("pick_black_lists.txt", "沉重号角\r\n黑铜号角\n\n");
    Write("pick_fuzzy_black_lists.txt", "空之神殿\n");
    File.WriteAllText(Path.Combine(accounts, "默认账户.json"), "[\"幽雾化形\",\"幽雾片甲\"]");
    File.WriteAllText(Path.Combine(accounts, "其他账户.json"), "[\"磨损的执凭\"]");
    File.WriteAllText(Path.Combine(accounts, "示例.json"), "not valid JSON; must be excluded");
    var first = loader.GetSnapshot(true);
    Check(first.IsValid && first.HasRules && first.HasWhitelist, "Valid merged snapshot");
    foreach (var item in new[] { "兽肉", "禽肉", "教官" })
        Check(first.Decide(item) == GlobalPickupDecision.Allow, "Global whitelist: " + item);
    foreach (var item in new[] { "破损的面具", "沉重号角", "黑铜号角", "幽雾化形", "幽雾片甲", "磨损的执凭", "空之神殿·摹忆中枢" })
        Check(first.Decide(item) == GlobalPickupDecision.Deny, "Global blacklist: " + item);
    Check(first.Decide("不祥的面具") == GlobalPickupDecision.Unspecified, "No unrequested mask expansion");
    Check(first.Decide("胡萝卜") == GlobalPickupDecision.Unspecified, "No general food expansion");
    var json = JObject.Parse(first.ToJson());
    Check((int)json["schema"]! == 1 && (bool)json["valid"]!, "SDK snapshot schema");
    Check(json["whiteList"]!.Count() == 3 && json["blackList"]!.Count() == 6, "Deterministic deduplicated merge");
    Rules(black: "[\"破损的面具\",\"兽肉\",\"空之神殿\"]");
    Check(loader.GetSnapshot().Decide("兽肉") == GlobalPickupDecision.Allow, "Bounded cache before poll");
    var denied = loader.GetSnapshot(true);
    Check(denied.Decide("兽肉") == GlobalPickupDecision.Deny, "Forced input-time read overrides whitelist immediately");
    Check(first.Decide("兽肉") == GlobalPickupDecision.Allow, "Previous snapshot remains immutable");
    Rules(white: "[\"空之神殿·摹忆中枢\"]");
    Check(loader.GetSnapshot(true).Decide("空之神殿·摹忆中枢") == GlobalPickupDecision.Deny, "Fuzzy blacklist overrides exact whitelist");
    Rules();
    time = 101;
    Check(loader.GetSnapshot().Decide("兽肉") == GlobalPickupDecision.Allow, "Automatic reload at 100ms");
    var file = Path.Combine(user, "global-pick-rules.json");
    var stamp = File.GetLastWriteTimeUtc(file);
    Rules(black: "[\"测试的面具\"]");
    File.SetLastWriteTimeUtc(file, stamp);
    Check(loader.GetSnapshot(true).Decide("测试的面具") == GlobalPickupDecision.Deny, "Forced read does not trust timestamp/length identity");
    foreach (var bad in new[] { "{", "null", "{\"schema\":2,\"whiteList\":[],\"blackList\":[]}",
        "{\"schema\":1,\"whiteList\":null,\"blackList\":[]}",
        "{\"schema\":1,\"whiteList\":[\" 兽肉\"],\"blackList\":[]}",
        "{\"schema\":1,\"whiteList\":[],\"blackList\":[null]}" })
    {
        Write("global-pick-rules.json", bad);
        var broken = loader.GetSnapshot(true);
        Check(!broken.IsValid && broken.Decide("兽肉") == GlobalPickupDecision.Deny, "Bad policy fails closed: " + bad);
        Check(!broken.HasWhitelist, "Invalid rules never enable whitelist fallback");
        Rules();
        Check(loader.GetSnapshot(true).IsValid, "Recover after valid replacement");
    }
    File.WriteAllText(Path.Combine(accounts, "默认账户.json"), "{");
    Check(!loader.GetSnapshot(true).IsValid, "Unreadable legacy blacklist must not be silently cleared");
    File.WriteAllText(Path.Combine(accounts, "默认账户.json"), "[\"幽雾化形\"]");
    Check(loader.GetSnapshot(true).Decide("幽雾化形") == GlobalPickupDecision.Deny, "Account blacklist recovers and persists outside task");
    File.Delete(file);
    Check(!loader.GetSnapshot(true).IsValid, "Deleted global policy must not drop global denials");
    Check(errors.Count > 0, "Configuration failures are reported");
    var channel = new GlobalPickupRegression.ProbeChannel();
    GlobalPickupInputGuard.PickupResult = GlobalPickupInputGuard.Result.Block;
    channel.KeyPress(Vanara.PInvoke.User32.VK.VK_F);
    channel.KeyDown(Vanara.PInvoke.User32.VK.VK_F);
    Check(channel.Events.Count == 0, "Blocked macro KeyPress and KeyDown never reach backend");
    Check(!channel.IsKeyDown(Vanara.PInvoke.User32.VK.VK_F), "Blocked input creates no held-key state");
    channel.SimulateAction(BetterGenshinImpact.Core.Simulator.Extensions.GIActions.PickUpOrInteract);
    Check(channel.Events.Count == 0, "Mapped pickup action cannot bypass guard");
    channel.KeyDown(Vanara.PInvoke.User32.VK.VK_W);
    Check(channel.IsKeyDown(Vanara.PInvoke.User32.VK.VK_W), "Movement hold remains intact");
    channel.KeyUp(Vanara.PInvoke.User32.VK.VK_F);
    Check(channel.Events.Last() == "up:VK_F", "Blacklist never blocks key release");
    channel.ReleaseAll();
    Check(!channel.IsKeyDown(Vanara.PInvoke.User32.VK.VK_W) && channel.Events.Last() == "up:VK_W", "Cleanup releases unrelated held keys");
    channel.Events.Clear();
    GlobalPickupInputGuard.PickupResult = GlobalPickupInputGuard.Result.Pulse;
    channel.KeyDown(Vanara.PInvoke.User32.VK.VK_F);
    Check(channel.Events.SequenceEqual(new[] { "down:VK_F", "up:VK_F" }), "Allowed main-world pickup hold becomes one checked pulse");
    Check(!channel.IsKeyDown(Vanara.PInvoke.User32.VK.VK_F), "No unchecked repeated pickup key remains held");
    channel.Events.Clear();
    GlobalPickupInputGuard.PickupResult = GlobalPickupInputGuard.Result.Pass;
    channel.KeyDown(Vanara.PInvoke.User32.VK.VK_F);
    Check(channel.Events.SequenceEqual(new[] { "down:VK_F" }) && channel.IsKeyDown(Vanara.PInvoke.User32.VK.VK_F), "Non-pickup UI holds preserve original behavior");
    channel.ReleaseAll();
    Check(channel.Events.Last() == "up:VK_F", "Non-pickup UI cleanup still releases F");
    Console.WriteLine($"PASS: {checks} production-linked global pickup assertions");
}
finally
{
    // 本回归只清理自身创建的随机临时目录，不接触真实 User 配置。
    if (!root.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) throw new Exception("Unsafe test cleanup path");
    Directory.Delete(root, true);
}
return 0;
