'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs'), path = require('node:path'), vm = require('node:vm'), crypto = require('node:crypto');
if (!process.argv[2] || !process.argv[3]) throw new Error('Usage: node route-group-tests.cjs <candidate JS folder> <policy folder>');
const candidate = path.resolve(process.argv[2]), dependencies = path.resolve(process.argv[3]);
const main = fs.readFileSync(path.join(candidate, 'main.js'), 'utf8');
const moduleFrom = (folder, name) => vm.runInNewContext(fs.readFileSync(path.join(folder, name), 'utf8'), {}, { filename: name, timeout: 2000 });
const runtimeModule = moduleFrom(candidate, 'point-loot-runtime.js');
const policy = moduleFrom(dependencies, 'point-loot-policy.js');
const sha = text => crypto.createHash('sha256').update(text).digest('hex');
const plain = value => JSON.parse(JSON.stringify(value));
const results = [];
async function test(name, run) {
    try { await run(); results.push({ name, passed: true }); }
    catch (error) { results.push({ name, passed: false, error: String(error.stack || error) }); }
}
function extract(name) {
    const found = new RegExp('(?:^|\\n)(?:async )?function ' + name + '\\(').exec(main);
    assert(found, 'Missing actual main function: ' + name);
    const start = found.index + (main[found.index] === '\n' ? 1 : 0), rest = main.slice(start);
    const next = /\n(?:async )?function /.exec(rest);
    return next ? rest.slice(0, next.index) : rest;
}
function fixture(outcomes) {
    const seen = { calls: [], enabled: [], writes: [], logs: [], fake: [] };
    const settings = { masterEnabled: true, artifactEnabled: true, materialEnabled: true, windowMs: 5000,
        alwaysAllowedItemIds: ['兽肉', '禽肉'], deniedItemIds: ['破损的面具'] };
    const catalog = { schema: 1, items: [
        { id: '教官', category: 'artifact', aliases: ['教官'], templatePaths: ['a/教官.png'] },
        { id: '破损的面具', category: 'material', aliases: ['破损的面具'], templatePaths: ['a/破损的面具.png'] },
        ...['兽肉', '禽肉'].map(id => ({ id, category: 'other', aliases: [id], templatePaths: ['a/' + id + '.png'] }))
    ] };
    const sources = new Map();
    const metadata = { schema: 1, routes: outcomes.map((_, i) => {
        const key = 'pathing/test' + i + '.json', raw = JSON.stringify({ info: { name: 'test' + i }, positions: [{ action: 'fight' }] });
        sources.set(key, raw);
        return { path: key, textSha256: sha(raw), rawSourceSignature: runtimeModule.fingerprint(raw),
            points: [{ pointIndex: 0, classification: 'elite_only', confidence: 'derived', artifacts: true,
                materialItemIds: ['破损的面具'], materialConfidence: 'derived', materialReason: 'route-declared-material' }] };
    }) };
    const host = {
        getPointLootCapabilities: () => JSON.stringify({ schema: 1, routeTeleportTimeoutMs: 60000, statueTeleportTimeoutMs: 120000,
            routeLocalFailureCodes: ['position-unrecognized', 'target-too-far', 'route-stuck', 'revive-retry-exhausted', 'teleport-failed', 'teleport-timeout'] }),
        tryPickup: () => true,
        setPointLootEnabled(value) { seen.enabled.push(value); }, getPointLootContext() { return 'null'; }, completePointLootWindow() {},
        async runWithLootPolicyResult(raw, json) {
            const envelope = JSON.parse(json), i = seen.calls.length;
            seen.calls.push({ raw, envelope });
            let outcome = outcomes[i];
            if (typeof outcome === 'function') outcome = await outcome();
            if (outcome instanceof Error) throw outcome;
            if (typeof outcome === 'object') return typeof outcome.raw === 'string' ? outcome.raw : JSON.stringify(outcome);
            return JSON.stringify({ schema: 1, success: outcome.startsWith('completed'), code: outcome,
                routeSha256: envelope.routeSha256, message: outcome });
        }
    };
    const runtime = runtimeModule.create({ host, policy, catalog, metadata, getBlacklist: () => [], getGlobalRules: () => ({ schema: 1, valid: true, whiteList: ['兽肉', '禽肉'], blackList: ['破损的面具'], fuzzyBlackList: [] }),
        readText: name => name === 'point-loot-settings.json' ? JSON.stringify(settings) : sources.get(name), now: () => 0 });
    const pathings = metadata.routes.map((r, i) => ({ fileName: 'test' + i + '.json', fullPath: r.path,
        tags: [], items: [], m: 1, e: 1, mora_m: 10, mora_e: 20, map_name: 'Teyvat', group: 1, t: 120,
        cdTime: '1970-01-01T00:00:00Z', records: [999, 999, 999] }));
    const ctx = {
        settings: { groupIndex: '路径组一', logMonsterCount: true }, shouldSwitchFurina: false,
        pathings, targetItems: [], dumpers: [], pickup_Mode: '模板匹配拾取，拾取狗粮和怪物材料', pointLoot: runtime,
        state: null, needRefreshCoord: false, localeTimeSupported: false, accountName: 'default',
        sleep: async () => {}, findAndClick: async () => false, recognizeAndInteract: async () => {},
        fakeLog: async (name, start) => seen.fake.push({ name, start }),
        log: Object.fromEntries(['info', 'error', 'warn', 'debug'].map(key => [key, text => seen.logs.push(String(text))])),
        frozenRo: {}, whiteFurinaRo: {}, revivalRo: {}, cookingRo: {}, itemFullRo: {},
        ERROR_CHECK_INTERVAL_LOOPS: 5, COOKING_CHECK_INTERVAL_LOOPS: 100, ERROR_LOOP_DELAY_MS: 50,
        FAST_MATCH_TIMEOUT_MS: 2, FAST_MATCH_INTERVAL_MS: 3, BACKPACK_CHECK_INTERVAL_CYCLES: 30,
        RUN_RECORD_LIMIT: 7, PICKUP_HISTORY_LIMIT: 20, ROUTE_COMPLETION_UI_TIMEOUT_MS: 2000, RECONNECT_CHECK_TIMEOUT_MS: 300,
        isTimeRestricted: async () => false, isMainUI: async () => true,
        genshin: { returnMainUi: async () => {} }, notification: { send() {} },
        updateRecords: async list => seen.writes.push(plain(list))
    };
    vm.createContext(ctx);
    vm.runInContext(['runPath', 'processPathingsByGroup'].map(extract).join('\n'), ctx);
    return { ctx, seen, runtime, pathings, group: () => ctx.processPathingsByGroup(pathings, 'default') };
}

(async () => {
    await test('candidate main parses without executing its game entrypoint', () => new vm.Script(main));
    for (const code of ['teleport-failed', 'teleport-timeout']) {
        await test(code + ': actual runtime and group continue after local failure without its CD/history', async () => {
            const f = fixture([code, 'completed', 'completed-with-skips']), first = plain(f.pathings[0]);
            await f.group();
            assert.equal(f.seen.calls.length, 3); assert.deepEqual(plain(f.pathings[0]), first);
            assert.equal(f.seen.writes.length, 2); assert.deepEqual(f.seen.writes[1][0], first);
            assert(f.pathings.slice(1).every(r => r.cdTime !== first.cdTime));
            assert(!f.seen.fake.some(r => r.name === first.fileName && r.start === false));
            assert.equal(f.ctx.state.running, false); assert.equal(f.seen.enabled.at(-1), false);
            assert.equal(f.runtime.globalContext(), null);
        });
    }
    await test('F001-style failure still reaches all 68 following routes (69 total)', async () => {
        const f = fixture(['teleport-failed', ...Array(68).fill('completed')]);
        const first = plain(f.pathings[0]); await f.group();
        assert.equal(f.seen.calls.length, 69); assert.equal(f.seen.writes.length, 68);
        assert.deepEqual(plain(f.pathings[0]), first);
        assert(f.pathings.slice(1).every(r => r.cdTime !== first.cdTime));
    });
    await test('A018-style failure at route 8/258 preserves failed record and reaches all 250 following routes', async () => {
        const f = fixture([...Array(7).fill('completed'), 'teleport-failed', ...Array(250).fill('completed')]);
        const failedBefore = plain(f.pathings[7]);
        await f.group();
        assert.equal(f.seen.calls.length, 258);
        assert.equal(f.seen.writes.length, 257);
        assert.deepEqual(plain(f.pathings[7]), failedBefore);
        assert(f.pathings.filter((_, index) => index !== 7).every(route => route.cdTime !== failedBefore.cdTime));
        assert(f.seen.writes.every(snapshot => JSON.stringify(snapshot[7]) === JSON.stringify(failedBefore)));
        assert.equal(f.runtime.globalContext(), null);
        assert.equal(f.ctx.state.running, false);
    });
    for (const error of ['F11 cancelled', 'unconfirmed main UI', 'unknown engine failure', 'persistent teleport failure']) {
        await test(error + ': fatal native exception stops before next route and cannot write CD', async () => {
            const f = fixture([new Error(error), 'completed']), before = plain(f.pathings);
            await assert.rejects(f.group(), new RegExp(error));
            assert.equal(f.seen.calls.length, 1); assert.equal(f.seen.writes.length, 0);
            assert.deepEqual(plain(f.pathings), before); assert.equal(f.ctx.state.running, false);
            assert.equal(f.runtime.globalContext(), null); assert.equal(f.seen.enabled.at(-1), false);
        });
    }
    await test('persistent failure after a local failure still stops remaining routes', async () => {
        const f = fixture(['teleport-timeout', new Error('persistent teleport failure'), 'completed']);
        await assert.rejects(f.group(), /persistent teleport failure/);
        assert.equal(f.seen.calls.length, 2); assert.equal(f.seen.writes.length, 0);
    });
    for (const raw of [JSON.stringify({ schema: 1, success: false, code: 'unclassified', routeSha256: '0'.repeat(64), message: '' }),
        JSON.stringify({ schema: 1, success: true, code: 'teleport-failed', routeSha256: '0'.repeat(64), message: '' }), 'null', 'not json']) {
        await test('malformed/unknown/mismatched native result is fatal: ' + raw.slice(0, 45), async () => {
            const f = fixture([{ raw }, 'completed']); await assert.rejects(f.group());
            assert.equal(f.seen.calls.length, 1); assert.equal(f.seen.writes.length, 0);
        });
    }
    await test('group validator independently rejects invalid result even if runtime is bypassed', async () => {
        const f = fixture(['completed', 'completed']);
        f.ctx.pointLoot = { run: async () => ({ success: false, code: 'unclassified', message: '' }) };
        await assert.rejects(f.group(), /可信的执行结果/); assert.equal(f.seen.writes.length, 0);
    });
    await test('cancellation between safe route failure and next route wins', async () => {
        const f = fixture(['teleport-failed', 'completed']);
        f.ctx.sleep = async () => { if (f.seen.calls.length && f.ctx.state && !f.ctx.state.running) throw new Error('F11 between routes'); };
        await assert.rejects(f.group(), /F11 between routes/);
        assert.equal(f.seen.calls.length, 1); assert.equal(f.seen.writes.length, 0);
    });
    await test('group waits for companion pickup to settle before next route', async () => {
        const f = fixture(['teleport-failed', 'completed']); let release;
        const gate = new Promise(resolve => { release = resolve; }); let pickups = 0;
        f.ctx.recognizeAndInteract = async () => { if (++pickups === 1) await gate; };
        const running = f.group(); await new Promise(setImmediate);
        assert.equal(f.seen.calls.length, 1); assert.equal(f.seen.writes.length, 0); assert.equal(f.ctx.state.running, false);
        release(); await running; assert.equal(f.seen.calls.length, 2); assert.equal(f.seen.writes.length, 1);
    });
    await test('pickup error remains fatal even if pathing returns safe local failure', async () => {
        const f = fixture(['teleport-timeout', 'completed']);
        f.ctx.recognizeAndInteract = async () => { throw new Error('picker failed'); };
        await assert.rejects(f.group(), /picker failed/); assert.equal(f.seen.calls.length, 1); assert.equal(f.seen.writes.length, 0);
    });
    await test('food permit and mask denial survive .6 and revoke after teleport failure', async () => {
        let release; const gate = new Promise(resolve => { release = resolve; });
        const f = fixture([async () => { await gate; return 'teleport-failed'; }]);
        const running = f.runtime.run('pathing/test0.json');
        const retained = f.runtime.globalContext();
        assert(retained); assert(f.runtime.permitsGlobal(retained.token, '兽肉'));
        assert(f.runtime.permitsGlobal(retained.token, '禽肉')); assert(!f.runtime.permitsGlobal(retained.token, '破损的面具'));
        release(); await running; assert.equal(f.runtime.globalContext(), null);
        assert.equal(f.runtime.permitsGlobal(retained.token, '兽肉'), false);
    });
    const failed = results.filter(r => !r.passed);
    console.log(JSON.stringify({ passed: results.length - failed.length, failed: failed.length, results,
        scope: 'Actual candidate JS runtime/runPath/group loop; synthetic routes and native/game/record boundaries. No game API or disk writes.' }, null, 2));
    if (failed.length) process.exitCode = 1;
})();
