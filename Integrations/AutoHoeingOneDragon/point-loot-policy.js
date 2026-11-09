(function () {
    "use strict";

    const MAX_WINDOW_MS = 30000;
    const classifications = new Set(["elite_only", "mixed", "ordinary", "unknown"]);
    const confidences = new Set(["confirmed", "derived", "unknown"]);
    const categories = new Set(["artifact", "material", "interaction", "other"]);

    function fail(message) { throw new Error("Point loot policy: " + message); }
    function object(value, label) {
        if (!value || typeof value !== "object" || Array.isArray(value)) fail(label + " must be an object");
    }
    function name(value, label) {
        if (typeof value !== "string" || !value || value.trim() !== value) fail(label + " must be an exact nonempty name");
        return value;
    }
    function boolean(value, label) {
        if (typeof value !== "boolean") fail(label + " must be boolean");
        return value;
    }

    function validateSettings(settings) {
        object(settings, "settings");
        const validated = {
            masterEnabled: boolean(settings.masterEnabled, "masterEnabled"),
            artifactEnabled: boolean(settings.artifactEnabled, "artifactEnabled"),
            materialEnabled: boolean(settings.materialEnabled, "materialEnabled")
        };
        if (!Number.isInteger(settings.windowMs) || settings.windowMs < 1 || settings.windowMs > MAX_WINDOW_MS) {
            fail("windowMs must be an integer from 1 to " + MAX_WINDOW_MS);
        }
        validated.windowMs = settings.windowMs;
        for (const key of ["alwaysAllowedItemIds", "deniedItemIds"]) {
            if (settings[key] === undefined) continue;
            if (!Array.isArray(settings[key])) fail(key + " must be an array");
            validated[key] = Object.freeze(Array.from(new Set(settings[key].map(value => name(value, key)))));
        }
        return Object.freeze(validated);
    }

    function indexCatalog(catalog) {
        object(catalog, "catalog");
        if (!Array.isArray(catalog.items)) fail("catalog.items must be an array");
        const byId = new Map(), byAlias = new Map();
        for (const source of catalog.items) {
            object(source, "catalog item");
            const id = name(source.id, "catalog item id");
            if (byId.has(id)) fail("duplicate catalog id: " + id);
            if (!categories.has(source.category)) fail("unknown catalog category: " + id);
            if (!Array.isArray(source.aliases) || !Array.isArray(source.templatePaths)) {
                fail("catalog aliases and templatePaths must be arrays: " + id);
            }
            const aliases = new Set([id]);
            for (const alias of source.aliases) aliases.add(name(alias, "catalog alias"));
            const templateNames = new Set();
            for (const templatePath of source.templatePaths) {
                name(templatePath, "template path");
                const normalized = templatePath.replace(/\\/g, "/");
                if (normalized.startsWith("/") || /^[A-Za-z]:/.test(normalized) || normalized.split("/").some(part => part === "..")) {
                    fail("template path must be relative and cannot traverse: " + templatePath);
                }
                const base = normalized.split("/").pop();
                if (!base || !/\.png$/i.test(base)) fail("template path must name a PNG: " + templatePath);
                const templateName = name(base.slice(0, -4), "template name");
                aliases.add(templateName);
                templateNames.add(templateName);
            }
            for (const alias of aliases) {
                if (byAlias.has(alias) && byAlias.get(alias) !== id) fail("ambiguous catalog alias: " + alias);
                byAlias.set(alias, id);
            }
            byId.set(id, { id: id, category: source.category, templateNames: Array.from(templateNames).sort() });
        }
        return { byId: byId, byAlias: byAlias };
    }

    function context(settings, catalog, blacklist) {
        const validated = validateSettings(settings), indexed = indexCatalog(catalog);
        if (!Array.isArray(blacklist)) fail("blacklist must be an array");
        const denied = new Set();
        for (const entry of blacklist.concat(validated.deniedItemIds || [])) {
            name(entry, "blacklist entry");
            // Unknown legacy entries stay untouched at the caller. Unknown items
            // never enter our allow list; no requirement can remove a denial.
            if (indexed.byAlias.has(entry)) denied.add(indexed.byAlias.get(entry));
        }
        const retainedIds = new Set();
        for (const id of validated.alwaysAllowedItemIds || []) {
            const item = indexed.byId.get(id);
            if (!item || item.category !== "other") fail("always-allowed item must be a canonical other item: " + id);
            retainedIds.add(id);
        }
        return { settings: validated, catalog: indexed, denied: denied, retainedIds: retainedIds };
    }

    function validatePoint(point, indexed) {
        object(point, "point");
        if (!Number.isInteger(point.pointIndex) || point.pointIndex < 0 || point.pointIndex > 2147483647) {
            fail("pointIndex must be a nonnegative Int32");
        }
        if (!classifications.has(point.classification)) fail("unknown point classification");
        if (!confidences.has(point.confidence)) fail("unknown point confidence");
        boolean(point.artifacts, "point.artifacts");
        if (!Array.isArray(point.materialItemIds)) fail("point.materialItemIds must be an array");
        if (!confidences.has(point.materialConfidence)) fail("unknown material confidence");
        if (point.materialItemIds.length && point.materialConfidence !== "unknown") name(point.materialReason, "materialReason");
        const materialIds = new Set();
        for (const value of point.materialItemIds) {
            const id = name(value, "material item id");
            const item = indexed.byId.get(id);
            if (!item) fail("material item is absent from catalog: " + id);
            if (item.category !== "material") fail("non-material item in material list: " + id);
            materialIds.add(id);
        }
        return materialIds;
    }

    function empty(reason) {
        return { artifacts: false, materials: false, allowedItemIds: [], allowedTemplateNames: [], gather: false, reason: reason };
    }

    function evaluate(point, ctx) {
        if (point === null || point === undefined) return empty(ctx.settings.masterEnabled ? "no-current-point" : "master-disabled");
        const materialIds = validatePoint(point, ctx.catalog);
        if (!ctx.settings.masterEnabled) return empty("master-disabled");
        const artifactEligible = ctx.settings.artifactEnabled && point.artifacts && point.classification === "elite_only" && point.confidence !== "unknown";
        const materialEligible = ctx.settings.materialEnabled && point.materialConfidence !== "unknown";
        const allowedIds = [], templateNames = new Set();
        let artifacts = false, materials = false, candidates = 0;
        for (const item of ctx.catalog.byId.values()) {
            const wantedArtifact = artifactEligible && item.category === "artifact";
            const wantedMaterial = materialEligible && materialIds.has(item.id);
            if (!wantedArtifact && !wantedMaterial) continue;
            candidates++;
            if (ctx.denied.has(item.id) || item.templateNames.length === 0) continue;
            allowedIds.push(item.id);
            item.templateNames.forEach(value => templateNames.add(value));
            artifacts = artifacts || wantedArtifact;
            materials = materials || wantedMaterial;
        }
        allowedIds.sort();
        let reason = artifacts && materials ? "artifacts-and-materials" : artifacts ? "artifacts-only" : materials ? "materials-only" :
            candidates ? "all-targets-denied-or-unrecognizable" : "no-eligible-targets";
        return { artifacts: artifacts, materials: materials, allowedItemIds: allowedIds,
            allowedTemplateNames: Array.from(templateNames).sort(), gather: allowedIds.length > 0, reason: reason };
    }

    function decidePoint(point, settings, catalog, blacklist) {
        return evaluate(point, context(settings, catalog, blacklist));
    }

    // Retained supplies are recognized during an active route without creating
    // a post-battle window, gathering, scrolling, or enabling other materials.
    function decideRetained(settings, catalog, blacklist) {
        const ctx = context(settings, catalog, blacklist);
        if (!ctx.settings.masterEnabled) return empty("master-disabled");
        const ids = [], templates = new Set();
        for (const id of ctx.retainedIds) {
            const item = ctx.catalog.byId.get(id);
            if (ctx.denied.has(id) || item.templateNames.length === 0) continue;
            ids.push(id);
            item.templateNames.forEach(value => templates.add(value));
        }
        return { artifacts: false, materials: false, gather: false,
            allowedItemIds: ids.sort(), allowedTemplateNames: Array.from(templates).sort(),
            reason: ids.length ? "retained-items-only" : "no-retained-targets" };
    }

    function validateGlobalRules(rules) {
        object(rules, "global rules");
        if (rules.schema !== 1 || rules.valid !== true) fail("global pickup rules are unavailable");
        for (const key of ["whiteList", "blackList", "fuzzyBlackList"]) {
            if (!Array.isArray(rules[key])) fail("global " + key + " must be an array");
            rules[key].forEach(value => name(value, "global " + key));
        }
        return rules;
    }

    function globalBlacklist(rules, catalog) {
        validateGlobalRules(rules);
        const indexed = indexCatalog(catalog), denied = new Set(rules.blackList);
        for (const value of rules.blackList) {
            const id = indexed.byAlias.get(value);
            if (id) denied.add(id);
        }
        for (const [alias, id] of indexed.byAlias) {
            if (rules.fuzzyBlackList.some(part => alias.includes(part))) denied.add(id);
        }
        return Array.from(denied);
    }

    // Global user rules are independent of hoeing switches and point evidence.
    function decideGlobal(rules, catalog, accountBlacklist) {
        const denied = globalBlacklist(rules, catalog).concat(accountBlacklist);
        const ctx = context({ masterEnabled: true, artifactEnabled: false, materialEnabled: false, windowMs: 1 }, catalog, denied);
        const ids = new Set(), templates = new Set();
        for (const value of rules.whiteList) {
            const id = ctx.catalog.byAlias.get(value), item = ctx.catalog.byId.get(id);
            if (!item || ctx.denied.has(id) || item.templateNames.length === 0) continue;
            ids.add(id); item.templateNames.forEach(value => templates.add(value));
        }
        return { artifacts: false, materials: false, gather: false,
            allowedItemIds: Array.from(ids).sort(), allowedTemplateNames: Array.from(templates).sort(),
            reason: ids.size ? "global-whitelist" : "no-global-targets" };
    }

    function compileRoute(metadataRoute, settings, catalog, blacklist) {
        object(metadataRoute, "metadata route");
        if (!Array.isArray(metadataRoute.points)) fail("metadata route points must be an array");
        const ctx = context(settings, catalog, blacklist), seen = new Set(), rules = [];
        // Source-hash binding belongs to the caller, which has the actual route
        // bytes. This module never edits route identities, combat, CD or inputs.
        for (const point of metadataRoute.points) {
            const decision = evaluate(point, ctx);
            if (!point) fail("metadata route cannot contain a null point");
            if (seen.has(point.pointIndex)) fail("duplicate pointIndex: " + point.pointIndex);
            seen.add(point.pointIndex);
            rules.push({ pointIndex: point.pointIndex, gather: decision.gather,
                windowMs: decision.allowedItemIds.length ? ctx.settings.windowMs : 0 });
        }
        return rules.sort((a, b) => a.pointIndex - b.pointIndex);
    }

    return Object.freeze({ validateSettings: validateSettings, decidePoint: decidePoint,
        decideRetained: decideRetained, validateGlobalRules: validateGlobalRules,
        globalBlacklist: globalBlacklist, decideGlobal: decideGlobal, compileRoute: compileRoute });
})()
