"""Read-only SDK protocol tests. All fixtures stay in a temporary directory."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile
import threading
import time

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('bgi_e2e', HERE / 'bgi_e2e.py')
api = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api)
WORKSPACE = api.ROOT


def fixture(root):
    api.ROOT = root
    out = root / 'report'
    rules = root / 'User/global-pick-rules.json'
    baseline = {'schema': 1, 'whiteList': ['兽肉', '禽肉', '教官'],
                'blackList': ['破损的面具'], 'fuzzyBlackList': ['测试模糊禁捡']}
    api.write_json(rules, baseline)
    scripts = [root / 'before', root / 'after']
    state = {'runId': root.name, 'scripts': [str(p) for p in scripts]}
    for phase, folder in zip(('before', 'after'), scripts):
        folder.mkdir()
        for src, target in [(HERE / 'live-probe.js', 'main.js'),
                            (api.SOURCE / 'Integrations/AutoHoeingOneDragon/point-loot-policy.js', 'policy.js'),
                            (WORKSPACE / 'User/JsScript/AutoHoeingOneDragon/item-catalog.json', 'catalog.json')]:
            shutil.copyfile(src, folder / target)
        api.write_json(folder / 'expected.json', {**baseline, 'phase': phase, 'version': api.VERSION, 'runId': state['runId']})
    api.write_json(out / 'live-run.json', state)
    return out, rules, rules.read_bytes(), scripts, state


def expect_error(run, message):
    try:
        run()
    except Exception as error:
        assert message in str(error), str(error)
        return
    raise AssertionError('Expected failure: ' + message)


def main():
    passed = []
    with tempfile.TemporaryDirectory(prefix='bgi-readonly-sdk-') as directory:
        base = Path(directory)
        out, rules, original, scripts, state = fixture(base / 'success')
        child = subprocess.Popen(['node', str(HERE / 'protocol-host.cjs'), str(out / 'live-run.json'), str(rules)],
                                 stdout=subprocess.PIPE, stderr=subprocess.PIPE, encoding='utf-8', errors='replace')
        result = api.live_wait(out, 10)
        _, stderr = child.communicate(timeout=5)
        assert child.returncode == 0, stderr
        assert result['passed'] and result['globalRulesReadOnly'] and rules.read_bytes() == original
        assert sum(len(r['checks']) for r in result['reports']) == 28
        passed.append({'name': 'actual read-only probe/controller, both groups', 'mockSdkChecks': 28})

        out, rules, original, scripts, state = fixture(base / 'timeout')
        expect_error(lambda: api.live_wait(out, 0.02), 'Live SDK did not finish')
        assert rules.read_bytes() == original and api.read_json(out / 'sdk-attempt-finished.json')['finished']
        passed.append({'name': 'timeout leaves rules unchanged and marks attempt finished'})

        out, rules, original, scripts, state = fixture(base / 'rejected')
        api.write_json(scripts[0] / 'result.json', {'runId': state['runId'], 'passed': False})
        expect_error(lambda: api.live_wait(out, 1), 'Installed SDK E2E rejected')
        assert rules.read_bytes() == original and api.read_json(out / 'sdk-attempt-finished.json')['finished']
        passed.append({'name': 'SDK rejection leaves rules unchanged and permits lifecycle cleanup'})

        out, rules, original, scripts, state = fixture(base / 'concurrent')
        edit = b'{"schema":1,"whiteList":["user edit"],"blackList":[]}'
        actor = threading.Thread(target=lambda: (time.sleep(0.03), api.atomic_write(rules, edit)))
        actor.start()
        expect_error(lambda: api.live_wait(out, 2), 'Concurrent global rule edit preserved')
        actor.join(timeout=3)
        assert not actor.is_alive() and rules.read_bytes() == edit
        assert api.read_json(out / 'sdk-attempt-finished.json')['globalRulesReadOnly']
        passed.append({'name': 'concurrent user edit is never overwritten or restored'})
    api.ROOT = WORKSPACE
    report = {'passed': True, 'scenarioCount': len(passed), 'scenarios': passed,
              'scope': 'Actual probe/controller with mock native SDK and temporary files; no live process or game.',
              'desktopAutomation': False, 'gameInput': False}
    api.write_json(api.DEPLOY / 'e2e/protocol-selftest.json', report)
    print(json.dumps(report, ensure_ascii=False, indent=2))


if __name__ == '__main__':
    main()
