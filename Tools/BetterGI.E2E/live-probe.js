(async function () {
    "use strict";
    const expected = JSON.parse(file.readTextSync("expected.json"));
    const policy = eval(file.readTextSync("policy.js"));
    const catalog = JSON.parse(file.readTextSync("catalog.json"));
    const checks = [];
    const snapshot = value => JSON.stringify({ schema: value.schema, valid: value.valid,
        whiteList: value.whiteList.slice().sort(), blackList: value.blackList.slice().sort(), fuzzyBlackList: value.fuzzyBlackList.slice().sort() });
    function check(name, condition) { checks.push({ name, passed: !!condition }); if (!condition) throw new Error(name); }
    function write(name, value) { if (!file.writeTextSync(name, JSON.stringify(value))) throw new Error("Result write failed: " + name); }
    let baseline = null;
    try {
        check("installed version", getVersion().includes(expected.version));
        check("native global SDK exists", typeof genshin.getGlobalPickupRules === "function");
        check("native target-bound input SDK exists", typeof pathingScript.tryPickup === "function");
        check("safe-continuation SDK exists", typeof pathingScript.getPointLootCapabilities === "function");
        const capabilities = JSON.parse(pathingScript.getPointLootCapabilities());
        check("actual route/statue budgets", capabilities.schema === 1 && capabilities.routeTeleportTimeoutMs === 60000 && capabilities.statueTeleportTimeoutMs === 120000);
        check("actual closed local failure codes", JSON.stringify(capabilities.routeLocalFailureCodes.slice().sort()) ===
            JSON.stringify(["position-unrecognized", "target-too-far", "route-stuck", "revive-retry-exhausted", "teleport-failed", "teleport-timeout"].sort()));
        let rejectedPolicy = false;
        try {
            await pathingScript.runWithLootPolicyResult('{"positions":[{"type":"target","x":0,"y":0,"action":"fight"}]}',
                JSON.stringify({ schema: 1, routeSha256: "0".repeat(64), points: [] }));
        } catch (error) { rejectedPolicy = /routeSha256/.test(String(error)); }
        check("actual bridge rejects mismatched policy before route/game work", rejectedPolicy);
        baseline = JSON.parse(genshin.getGlobalPickupRules(true));
        policy.validateGlobalRules(baseline);
        for (const key of ["whiteList", "blackList", "fuzzyBlackList"])
            check("actual native merged " + key, JSON.stringify(baseline[key].slice().sort()) === JSON.stringify(expected[key].slice().sort()));
        const globalDecision = policy.decideGlobal(baseline, catalog, []);
        for (const food of ["兽肉", "禽肉"]) check("global exact food allowance " + food, globalDecision.allowedItemIds.includes(food));
        check("mask denied", !globalDecision.allowedItemIds.includes("破损的面具") && baseline.blackList.includes("破损的面具"));
        check("no route/window active", JSON.parse(pathingScript.getPointLootContext()) === null);
        write("result.json", { schema: 1, runId: expected.runId, phase: expected.phase, passed: true,
            version: getVersion(), checks, snapshot: snapshot(baseline), desktopAutomation: false, gameInput: false,
            worldPickupReceipt: "not exercised" });
        log.info("CLI_E2E_PASS " + expected.runId + " " + expected.phase + " checks=" + checks.length);
    } catch (error) {
        write("result.json", { schema: 1, runId: expected.runId, phase: expected.phase, passed: false, checks, error: String(error) });
        log.error("CLI_E2E_FAIL " + expected.runId + " " + expected.phase + ": " + error);
        throw error;
    }
})();
