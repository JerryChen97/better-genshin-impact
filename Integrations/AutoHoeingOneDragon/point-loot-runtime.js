(function () {
    "use strict";
    const CONFIG = "point-loot-settings.json";
    function fingerprint(text) {
        let hash = 2166136261;
        text = String(text).replace(/^\uFEFF/, "");
        for (let i = 0; i < text.length; i++) hash = Math.imul(hash ^ text.charCodeAt(i), 16777619);
        return "fnv1a32:" + (hash >>> 0).toString(16).padStart(8, "0");
    }
    function relativePath(value) {
        const normalized = String(value).replace(/\\/g, "/");
        if (normalized.split("/").some(x => x === "..")) throw new Error("Route path traversal is not allowed");
        const marker = normalized.lastIndexOf("/pathing/");
        const result = marker >= 0 ? normalized.slice(marker + 1) : normalized;
        if (!result.startsWith("pathing/") || result.split("/").some(x => x === "..")) throw new Error("Unrecognized route path: " + value);
        return result;
    }
    function create(deps) {
        const { host, policy, catalog, metadata, readText, getBlacklist, getGlobalRules } = deps;
        if (typeof getGlobalRules !== "function") throw new Error("缺少全局拾取名单读取接口");
        function globalRules(force) { return policy.validateGlobalRules(getGlobalRules(!!force)); }
        function effectiveBlacklist(force) {
            return Array.from(new Set(getBlacklist().concat(policy.globalBlacklist(globalRules(force), catalog))));
        }
        globalRules(true);
        for (const name of ["runWithLootPolicyResult", "getPointLootContext", "completePointLootWindow", "setPointLootEnabled", "tryPickup"]) {
            if (typeof host[name] !== "function") throw new Error("当前 BetterGI 缺少按点拾取接口；需要候选主程序，不能使用旧版整路线拾取代替。");
        }
        if (typeof host.getPointLootCapabilities !== "function") throw new Error("当前 BetterGI 缺少安全续组协议；需要配套 .6 主程序");
        const capabilities = JSON.parse(host.getPointLootCapabilities());
        if (!capabilities || capabilities.schema !== 1 || capabilities.routeTeleportTimeoutMs !== 60000 ||
            capabilities.statueTeleportTimeoutMs !== 120000 || !Array.isArray(capabilities.routeLocalFailureCodes) ||
            !["teleport-failed", "teleport-timeout"].every(code => capabilities.routeLocalFailureCodes.includes(code)))
            throw new Error("不兼容的安全续组协议，拒绝执行路线");
        if (metadata.schema !== 1 || catalog.schema !== 1) throw new Error("Unsupported point loot metadata schema");
        const routes = new Map();
        for (const route of metadata.routes) {
            const key = relativePath(route.path);
            if (routes.has(key)) throw new Error("Duplicate route metadata: " + key);
            routes.set(key, route);
        }
        let current = null, liveSettings = null, lastRead = -Infinity, suspended = false, routeSequence = 0;
        const now = deps.now || Date.now;
        function refresh(force) {
            if (force || now() - lastRead >= 100) {
                try {
                    liveSettings = policy.validateSettings(JSON.parse(readText(CONFIG)));
                    lastRead = now();
                } catch (error) {
                    host.setPointLootEnabled(false);
                    suspended = true;
                    throw error;
                }
            }
            host.setPointLootEnabled(!suspended && liveSettings.masterEnabled);
            return liveSettings;
        }
        refresh(true); // Reject absent/malformed shared configuration before game actions.
        function context(forceGlobal = false) {
            refresh(false);
            if (!current || suspended || !liveSettings.masterEnabled) return null;
            const value = JSON.parse(host.getPointLootContext());
            if (!value || value.phase !== "postBattle" || typeof value.token !== "string") return null;
            const point = current.points.get(value.pointIndex);
            if (!point) return null;
            // Category changes apply on the next route. The master switch is live.
            const settings = Object.assign({}, current.settings, { masterEnabled: liveSettings.masterEnabled,
                deniedItemIds: liveSettings.deniedItemIds || [] });
            const decision = policy.decidePoint(point, settings, catalog, effectiveBlacklist(forceGlobal));
            return Object.assign({}, value, { decision: decision });
        }
        function permits(token, itemName) {
            const value = context(true);
            return !!value && value.token === token && value.decision.allowedTemplateNames.includes(itemName);
        }
        function globalContext(force = false) {
            if (!current || suspended) return null;
            const decision = policy.decideGlobal(globalRules(force), catalog, getBlacklist());
            return { token: current.globalToken, decision: decision };
        }
        function permitsGlobal(token, itemName) {
            const value = globalContext(true);
            return !!value && value.token === token && value.decision.allowedTemplateNames.includes(itemName);
        }
        function permitsScroll(token) {
            const value = context();
            return !!value && value.token === token && value.decision.allowedItemIds.length > 0;
        }
        function tryPickup(itemName, source, token) {
            if (source === "global" ? !permitsGlobal(token, itemName) :
                source === "point" ? !permits(token, itemName) :
                source !== "mandatory" || !mandatoryAllowed(itemName)) return false;
            if (!current || suspended) return false;
            const target = policy.pickupTarget(catalog, itemName);
            return host.tryPickup(target.name, source, source === "point" ? token : current.route.textSha256, target.artifactPrefix);
        }
        async function run(routePath) {
            if (current) throw new Error("A point loot route is already active");
            const raw = readText(routePath).replace(/^\uFEFF/, ""), key = relativePath(routePath), route = routes.get(key);
            if (!route || fingerprint(raw) !== route.rawSourceSignature) throw new Error("路线已变化或缺少按点数据，请先重建元数据：" + key);
            if (!/^[a-f0-9]{64}$/.test(route.textSha256)) throw new Error("Missing strict route SHA256: " + key);
            const settings = refresh(true);
            // Compile potential windows even when master is off, so enabling it
            // affects later battles in this same route. The host master gates them.
            const potential = Object.assign({}, settings, { masterEnabled: true });
            const rules = policy.compileRoute(route, potential, catalog, effectiveBlacklist(true));
            current = { settings: potential, points: new Map(route.points.map(p => [p.pointIndex, p])),
                route: route, globalToken: "global:" + (++routeSequence) };
            try {
                const rawResult = await host.runWithLootPolicyResult(raw, JSON.stringify({ schema: 1, routeSha256: route.textSha256, points: rules }));
                if (typeof rawResult !== "string") throw new Error("Invalid point loot route result");
                const result = JSON.parse(rawResult);
                const fields = ["schema", "success", "code", "routeSha256", "message"];
                if (!result || typeof result !== "object" || Array.isArray(result) ||
                    Object.keys(result).length !== fields.length || !fields.every(name => Object.prototype.hasOwnProperty.call(result, name)) ||
                    result.schema !== 1 || typeof result.success !== "boolean" || result.routeSha256 !== route.textSha256 ||
                    typeof result.message !== "string" ||
                    (result.success ? !["completed", "completed-with-skips"].includes(result.code) : !["position-unrecognized", "target-too-far", "route-stuck", "revive-retry-exhausted", "teleport-failed", "teleport-timeout"].includes(result.code))) {
                    throw new Error("Invalid or mismatched point loot route result");
                }
                return { success: result.success, code: result.code, message: result.message };
            } finally {
                current = null;
                host.setPointLootEnabled(false);
            }
        }
        function suspend() { suspended = true; host.setPointLootEnabled(false); }
        function complete(token) {
            const value = context();
            if (value && value.token === token) host.completePointLootWindow(token);
        }
        function mandatoryAllowed(itemName) {
            // Only the exact authored sandstorm interaction is independent of
            // loot. Retained supplies use their own route-bound exact-item check;
            // neither permits scrolling or a general F outside a loot window.
            if (!current || itemName !== "镇压沙暴") return false;
            const settings = refresh(false);
            return !effectiveBlacklist(true).includes(itemName)
                && !(settings.deniedItemIds || []).includes(itemName);
        }
        return Object.freeze({ run: run, context: context, permits: permits, permitsScroll: permitsScroll,
            globalContext: globalContext, permitsGlobal: permitsGlobal, tryPickup: tryPickup,
            complete: complete, suspend: suspend, mandatoryAllowed: mandatoryAllowed });
    }
    return Object.freeze({ create: create, fingerprint: fingerprint, relativePath: relativePath });
})()
