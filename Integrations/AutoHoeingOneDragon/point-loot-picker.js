(function () {
    "use strict";
    // Inputs are injected to allow race/cleanup verification without game input.
    async function run(deps) {
        const { runtime, running, sleep, findItem, pressF, scroll, remember } = deps;
        let lastToken = null, lastName = null, lastPress = -Infinity, tokenObservedAt = 0;
        // Use the first half of each observed window to scan down, then scan up.
        // Only the host ends the window; a missed template does not prove empty.
        const now = deps.now || Date.now;
        try {
            while (running()) {
                const context = runtime.context();
                if (context && context.token !== lastToken) {
                    lastToken = context.token; lastName = null; tokenObservedAt = now();
                }
                const global = runtime.globalContext();
                const allowed = context ? context.decision.allowedTemplateNames : [];
                const globalNames = global ? global.decision.allowedTemplateNames : [];
                const names = Array.from(new Set(allowed.concat(globalNames, "镇压沙暴")));
                const item = await findItem(names);
                if (!running()) break;
                if (item === "镇压沙暴" && runtime.mandatoryAllowed(item)) {
                    // Re-read blacklist immediately before the mandatory input.
                    if (now() - lastPress >= 250 && runtime.mandatoryAllowed(item)) {
                        pressF(); lastPress = now(); lastName = item;
                    }
                } else if (item && global && runtime.permitsGlobal(global.token, item)) {
                    if (lastName !== item || now() - lastPress >= 160) {
                        // Revalidate the global blacklist and route token after capture.
                        if (runtime.permitsGlobal(global.token, item)) {
                            pressF(); remember(item); lastName = item; lastPress = now();
                        }
                    }
                } else if (item && context && runtime.permits(context.token, item)) {
                    if (lastName !== item || now() - lastPress >= 160) {
                        // No await between this final token/deny check and input.
                        if (runtime.permits(context.token, item)) {
                            pressF(); remember(item); lastName = item; lastPress = now();
                        }
                    }
                }
                if (context && runtime.permitsScroll(context.token)) {
                    await scroll(now() - tokenObservedAt >= context.windowMs / 2);
                }
                await sleep(100);
            }
        } catch (error) {
            runtime.suspend();
            throw error;
        }
    }
    return Object.freeze({ run: run });
})()
