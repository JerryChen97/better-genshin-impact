'use strict';
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const modules = path.resolve(__dirname, '../../Integrations/AutoHoeingOneDragon');
const load = file => vm.runInNewContext(fs.readFileSync(path.join(modules, file), 'utf8'), {}, { filename: file, timeout: 2000 });
const plain = value => JSON.parse(JSON.stringify(value));
const policy = load('point-loot-policy.js');
const runtimeModule = load('point-loot-runtime.js');
const picker = load('point-loot-picker.js');
const catalog = { schema: 1, items: [
  ['兽肉', 'other'], ['禽肉', 'other'], ['教官', 'artifact'], ['破损的面具', 'material'],
  ['不祥的面具', 'material'], ['幽雾片甲', 'material'], ['胡萝卜', 'other']
].map(([id, category]) => ({ id, category, aliases: id === '幽雾片甲' ? [id, id + '(1)'] : [id], templatePaths: ['assets/' + id + '.png'] })) };
const config = { masterEnabled: true, artifactEnabled: true, materialEnabled: true, windowMs: 5000 };
const global = { schema: 1, valid: true, whiteList: ['兽肉', '禽肉', '教官'], blackList: ['破损的面具', '幽雾片甲(1)'], fuzzyBlackList: [] };
const raw = JSON.stringify({ positions: [{ action: 'fight' }] });
const route = { path: 'pathing/offline.json', fileName: 'offline.json', rawSourceSignature: runtimeModule.fingerprint(raw),
  textSha256: crypto.createHash('sha256').update(raw, 'utf8').digest('hex'), points: [
    { pointIndex: 0, classification: 'elite_only', confidence: 'confirmed', artifacts: true,
      materialConfidence: 'confirmed', materialReason: 'offline fixture', materialItemIds: ['破损的面具', '不祥的面具', '幽雾片甲'] }
  ] };
const results = [];
async function test(name, action) {
  try { await action(); results.push({ name, passed: true }); }
  catch (error) { results.push({ name, passed: false, error: error.stack }); }
}
function fixture() {
  const state = { settings: plain(config), rules: plain(global), blacklist: [], time: 0, active: true,
    f: [], scroll: [], remembered: [], enabled: [], context: null, completion: null, forcedReads: 0 };
  const host = { setPointLootEnabled: flag => state.enabled.push(flag), completePointLootWindow() {},
    getPointLootContext: () => JSON.stringify(state.context),
    runWithLootPolicyResult() { return new Promise((resolve, reject) => { state.completion = { resolve, reject }; }); } };
  const deps = { host, policy, catalog, metadata: { schema: 1, routes: [route] },
    readText: file => file === 'point-loot-settings.json' ? JSON.stringify(state.settings) : raw,
    getBlacklist: () => state.blacklist.slice(),
    getGlobalRules: force => { if (force) state.forcedReads++; return plain(state.rules); }, now: () => state.time };
  const runtime = runtimeModule.create(deps);
  let pending;
  return { state, runtime, deps,
    start() { pending = runtime.run(route.path); return pending; },
    async end() {
      state.completion.resolve(JSON.stringify({ schema: 1, success: true, code: 'completed', routeSha256: route.textSha256, message: 'offline' }));
      await pending;
    },
    async fail() { state.completion.reject(new Error('cancelled')); await assert.rejects(pending, /cancelled/); },
    async pick(item, afterCapture) {
      await picker.run({ runtime, running: () => state.active, now: () => state.time,
        findItem: async names => { state.offered = names; if (afterCapture) await afterCapture(); return item; },
        pressF: () => state.f.push(item), remember: name => state.remembered.push(name),
        scroll: async direction => state.scroll.push(direction), sleep: async () => { state.active = false; } });
    }
  };
}
(async () => {
  await test('application whitelist has no dependency on hoeing point evidence', () => {
    const decision = policy.decideGlobal(global, catalog, []);
    assert.deepEqual(plain(decision.allowedItemIds).sort(), ['兽肉', '禽肉', '教官'].sort());
    assert.equal(decision.gather, false); assert.equal(decision.artifacts, false); assert.equal(decision.materials, false);
  });
  await test('canonical alias blacklist overrides exact whitelist', () => {
    const rules = plain(global); rules.whiteList.push('幽雾片甲');
    assert(!policy.decideGlobal(rules, catalog, []).allowedItemIds.includes('幽雾片甲'));
  });
  await test('fuzzy blacklist overrides exact food whitelist', () => {
    const rules = plain(global); rules.fuzzyBlackList.push('兽');
    assert(!policy.decideGlobal(rules, catalog, []).allowedItemIds.includes('兽肉'));
  });
  await test('account denial is retained', () => assert(!policy.decideGlobal(global, catalog, ['禽肉']).allowedItemIds.includes('禽肉')));
  for (const item of ['兽肉', '禽肉', '教官']) await test('master/category OFF retains global ' + item, async () => {
    const f = fixture(); Object.assign(f.state.settings, { masterEnabled: false, artifactEnabled: false, materialEnabled: false });
    f.start(); assert.equal(f.runtime.context(), null); await f.pick(item);
    assert.deepEqual(f.state.f, [item]); assert.deepEqual(f.state.scroll, []); await f.end();
  });
  for (const item of ['破损的面具', '幽雾片甲', '胡萝卜']) await test('travel phase does not permit ' + item, async () => {
    const f = fixture(); f.start(); await f.pick(item); assert.deepEqual(f.state.f, []); assert(!f.state.offered.includes(item)); await f.end();
  });
  await test('blacklist update after capture vetoes pending pickup', async () => {
    const f = fixture(); f.start(); await f.pick('兽肉', () => { f.state.rules.blackList.push('兽肉'); });
    assert.deepEqual(f.state.f, []); assert(f.state.forcedReads > 1); await f.end();
  });
  await test('whitelist removal after capture vetoes pending pickup', async () => {
    const f = fixture(); f.start(); await f.pick('禽肉', () => { f.state.rules.whiteList = ['兽肉']; }); assert.deepEqual(f.state.f, []); await f.end();
  });
  await test('master OFF after capture does not revoke application whitelist', async () => {
    const f = fixture(); f.start(); await f.pick('兽肉', () => { f.state.settings.masterEnabled = false; f.state.time = 101; });
    assert.deepEqual(f.state.f, ['兽肉']); await f.end();
  });
  await test('invalid global snapshot after capture fails closed', async () => {
    const f = fixture(); f.start(); await assert.rejects(f.pick('兽肉', () => { f.state.rules.valid = false; }), /global/);
    assert.deepEqual(f.state.f, []); assert.equal(f.state.enabled.at(-1), false); await f.end();
  });
  for (const action of ['finish', 'suspend', 'stop']) await test(action + ' during capture suppresses stale input', async () => {
    const f = fixture(); f.start(); await f.pick('兽肉', async () => {
      if (action === 'finish') await f.end(); else if (action === 'suspend') f.runtime.suspend(); else f.state.active = false;
    }); assert.deepEqual(f.state.f, []); if (action !== 'finish') await f.end();
  });
  await test('cancellation clears route token', async () => {
    const f = fixture(); f.start(); const token = f.runtime.globalContext().token; await f.fail(); assert(!f.runtime.permitsGlobal(token, '兽肉'));
  });
  await test('new route does not reuse previous route global token', async () => {
    const f = fixture(); f.start(); const token = f.runtime.globalContext().token; await f.end(); f.start();
    assert(!f.runtime.permitsGlobal(token, '兽肉')); await f.end();
  });
  await test('postbattle point permissions also obey global exact/alias denial', async () => {
    const f = fixture(); f.start(); f.state.context = { phase: 'postBattle', token: 'point', pointIndex: 0 };
    assert(!f.runtime.permits('point', '破损的面具')); assert(!f.runtime.permits('point', '幽雾片甲'));
    assert(f.runtime.permits('point', '不祥的面具')); await f.end();
  });
  await test('blacklist change after point recognition vetoes pending material', async () => {
    const f = fixture(); f.start(); f.state.context = { phase: 'postBattle', token: 'point', pointIndex: 0 };
    await f.pick('不祥的面具', () => { f.state.rules.blackList.push('不祥的面具'); }); assert.deepEqual(f.state.f, []); await f.end();
  });
  await test('point token change after capture suppresses stale material', async () => {
    const f = fixture(); f.start(); f.state.context = { phase: 'postBattle', token: 'old', pointIndex: 0 };
    await f.pick('不祥的面具', () => { f.state.context.token = 'new'; }); assert.deepEqual(f.state.f, []); await f.end();
  });
  await test('mandatory sandstorm interaction remains globally blacklisted', async () => {
    const f = fixture(); f.start(); assert(f.runtime.mandatoryAllowed('镇压沙暴'));
    f.state.rules.blackList.push('镇压沙暴'); assert(!f.runtime.mandatoryAllowed('镇压沙暴')); await f.end();
  });
  await test('SDK absence and invalid rules are rejected before route start', () => {
    const f = fixture(); assert.throws(() => runtimeModule.create({ ...f.deps, getGlobalRules: undefined }), /全局/);
    f.state.rules.valid = false; assert.throws(() => runtimeModule.create(f.deps), /global/);
  });
  const failed = results.filter(x => !x.passed);
  console.log(JSON.stringify({ passed: results.length - failed.length, failed: failed.length, failures: failed }, null, 2));
  if (failed.length) process.exitCode = 1;
})();
