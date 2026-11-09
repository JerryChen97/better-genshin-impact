'use strict';
// Test adapter only: no BGI process, game, desktop, keyboard or mouse backend.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const state = JSON.parse(fs.readFileSync(process.argv[2], 'utf8'));
const ruleFile = process.argv[3];
async function main() {
  for (const folder of state.scripts) {
    const expected = JSON.parse(fs.readFileSync(path.join(folder, 'expected.json'), 'utf8'));
    const sandbox = {
      file: {
        readTextSync: name => fs.readFileSync(path.join(folder, name), 'utf8'),
        isFile: name => fs.existsSync(path.join(folder, name)),
        writeTextSync: (name, value) => { fs.writeFileSync(path.join(folder, name), value); return true; }
      },
      getVersion: () => expected.version,
      genshin: { getGlobalPickupRules() {
        try { return JSON.stringify({ ...JSON.parse(fs.readFileSync(ruleFile, 'utf8')), valid: true }); }
        catch (_) { return JSON.stringify({ schema: 1, valid: false, whiteList: [], blackList: [], fuzzyBlackList: [] }); }
      } },
      pathingScript: {
        getPointLootCapabilities: () => JSON.stringify({ schema: 1, routeTeleportTimeoutMs: 60000, statueTeleportTimeoutMs: 120000,
          routeLocalFailureCodes: ['position-unrecognized', 'target-too-far', 'route-stuck', 'revive-retry-exhausted', 'teleport-failed', 'teleport-timeout'] }),
        runWithLootPolicyResult: async () => { throw new Error('routeSha256 mismatch (protocol adapter)'); },
        getPointLootContext: () => 'null',
        tryPickup: () => { throw new Error('Protocol probe must never invoke input'); }
      },
      sleep: ms => new Promise(resolve => setTimeout(resolve, ms)),
      log: { info: value => process.stdout.write(value + '\n'), error: value => process.stderr.write(value + '\n') }
    };
    await vm.runInNewContext(fs.readFileSync(path.join(folder, 'main.js'), 'utf8'), sandbox);
  }
}
main().catch(error => { process.stderr.write(String(error)); process.exitCode = 1; });
