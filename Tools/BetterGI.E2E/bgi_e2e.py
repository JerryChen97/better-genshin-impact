"""BetterGI CLI verification. No desktop driver, UI Automation or synthetic input."""
from __future__ import annotations
import argparse
import datetime as dt
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import sys
import time
import uuid

ROOT = Path(os.environ.get('BGI_E2E_ROOT', r'C:\Program Files\BetterGI')).resolve()
SOURCE = Path(__file__).resolve().parents[2]
DEPLOY = ROOT / 'artifacts/pointloot-v6-deploy-20261109'
COMMIT = None
VERSION = '0.66.3-local.pointloot.6-globalpick.2'


def configure(package, root):
    global ROOT, SOURCE, DEPLOY, COMMIT, VERSION
    ROOT = Path(root).resolve()
    DEPLOY = Path(package).resolve()
    if not DEPLOY.is_relative_to((ROOT / 'artifacts').resolve()):
        raise RuntimeError('Package must be inside the workspace artifacts directory')
    manifest = read_json(DEPLOY / 'package-manifest.json')
    SOURCE = Path(manifest['sourcePath']).resolve()
    if not SOURCE.is_relative_to((ROOT / 'Repos').resolve()):
        raise RuntimeError('Source must be a workspace checkout')
    COMMIT = manifest['sourceCommit']
    VERSION = manifest['version']

def read_json(file):
    return json.loads(Path(file).read_text(encoding='utf-8-sig'))

def write_json(file, value):
    Path(file).parent.mkdir(parents=True, exist_ok=True)
    atomic_write(Path(file), json.dumps(value, ensure_ascii=False, indent=2).encode('utf-8'))

def sha(file):
    return hashlib.sha256(Path(file).read_bytes()).hexdigest()

def run(args, cwd=ROOT, timeout=180):
    completed = subprocess.run(args, cwd=cwd, capture_output=True, encoding='utf-8', errors='replace', timeout=timeout)
    if completed.returncode:
        raise RuntimeError(f'Command exit {completed.returncode}: {args[0]}\n{completed.stdout[-2000:]}\n{completed.stderr[-1000:]}')
    return completed.stdout

def resume_proof():
    spec = importlib.util.spec_from_file_location('bgi_status', ROOT / '.agents/tools/hoeing_status.py')
    status = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(status)
    records = []
    for date in ('20261108', '20261109'):
        log = ROOT / 'log' / f'better-genshin-impact{date}.log'
        if log.exists():
            records.extend(r for r in status.load(log, date) if ':S2:' in r['src'])
    plan = read_json(ROOT / 'artifacts/hoeing-resume-20261108/exact-remaining-plan.json')
    begin = f'配置组 "{plan["groupName"]}" 加载完成'
    finish = f'配置组 "{plan["groupName"]}" 执行结束'
    starts = [i for i, record in enumerate(records) if record['first'].startswith(begin)]
    if not starts:
        raise RuntimeError('Exact resume start is absent')
    chunk = records[starts[-1]:]
    ends = [i for i, record in enumerate(chunk) if record['first'] == finish]
    if not ends:
        raise RuntimeError('Remaining-route group is still active; deployment forbidden')
    chunk = chunk[:ends[0] + 1]
    successes = sum(r['first'] == '路线运行成功' for r in chunk)
    failures = [r['first'] for r in chunk if r['first'].startswith('路线运行失败')]
    completed = []
    for record in chunk:
        match = re.search(r'当前进度：第 1 组第 \d+/68 个\s*(.+?)已完成', record['first'])
        if match:
            completed.append(match.group(1).strip())
    expected = set(plan['exactFileNames'])
    if successes != 68 or failures or len(completed) != 68 or set(completed) != expected:
        raise RuntimeError('Resume result does not cover the exact successful 68-route set')
    return {'group': plan['groupName'], 'success': successes, 'failed': 0, 'coveragePercent': 100,
            'completedAt': chunk[-1]['d'] + ' ' + chunk[-1]['t'], 'exactSetMatched': True}

def plan_check(out):
    manifest = read_json(DEPLOY / 'package-manifest.json')
    head = run(['git', '--no-optional-locks', '-c', 'safe.directory=*', 'rev-parse', 'HEAD'], SOURCE).strip()
    if head != COMMIT:
        raise RuntimeError('Source HEAD differs from the pinned base/commit')
    index = Path(manifest['sourceIndex']).resolve()
    if not index.is_relative_to(DEPLOY):
        raise RuntimeError('Source review index must stay in the package directory')
    env = os.environ.copy(); env['GIT_INDEX_FILE'] = str(index)
    git = ['git', '--no-optional-locks', '-c', 'safe.directory=*']
    for args in (['diff', '--exit-code'], ['diff', '--cached', '--exit-code', manifest['sourceTree']]):
        checked = subprocess.run(git + args, cwd=SOURCE, env=env, capture_output=True, timeout=60)
        if checked.returncode:
            raise RuntimeError('Source differs from the independently reviewed tree')
    extra = subprocess.run(git + ['ls-files', '--others', '--exclude-standard'], cwd=SOURCE, env=env,
                           capture_output=True, timeout=60)
    if extra.returncode or extra.stdout.strip():
        raise RuntimeError('Unreviewed source files appeared after the frozen snapshot')
    for item in manifest['files']:
        target = (ROOT / item['file']).resolve()
        if not target.is_relative_to(ROOT.resolve()):
            raise RuntimeError('Manifest target escapes workspace')
        if sha(DEPLOY / 'package' / item['file']) != item['candidateSha256']:
            raise RuntimeError('Candidate changed: ' + item['file'])
        if item['existed'] and sha(DEPLOY / 'backup-before-install' / item['file']) != item['beforeSha256']:
            raise RuntimeError('Rollback backup changed: ' + item['file'])
    blocked = ['A005蒙德雪山-2.json', 'A007蒙德龙脊雪山寒天之钉下方.json']
    active = ROOT / 'User/JsScript/AutoHoeingOneDragon/pathing'
    if any(list(active.rglob(name)) for name in blocked):
        raise RuntimeError('Blocked snow route reappeared in active scanner')
    result = {'schema': 1, 'mode': 'plan', 'passed': True, 'at': dt.datetime.now().astimezone().isoformat(),
              'sourceCommit': head, 'sourceTree': manifest['sourceTree'], 'resume': resume_proof(), 'installTargets': len(manifest['files']),
              'desktopAutomation': False, 'gameInput': False, 'snowRoutesBlocked': True}
    write_json(out / 'plan.json', result)
    return result

def contracts(out):
    receipt = out / 'contracts.json'
    if receipt.exists():
        receipt.rename(out / ('contracts-previous-' + uuid.uuid4().hex + '.json'))
    before = plan_check(out)
    manifest = read_json(DEPLOY / 'package-manifest.json')
    groups = {}
    for name, expected in manifest['nativeContracts'].items():
        text = run(['dotnet', 'run', '--project', f'Test/{name}.Regression/{name}.Regression.csproj', '--no-restore'], SOURCE)
        (out / f'{name}.log').write_text(text, encoding='utf-8')
        if not (re.search(rf'PASS:?\s+(?:[^\n]*?\s)?{expected}\b', text) or f'"passed":{expected}' in text.replace(' ', '')):
            raise RuntimeError('Regression did not report expected coverage: ' + name)
        groups[name] = expected
    for name, entry in manifest['jsContracts'].items():
        args = [value.replace('{package}', str(DEPLOY)) for value in entry['arguments']]
        text = run(['node'] + args, SOURCE)
        (out / (name + '.log')).write_text(text, encoding='utf-8')
        report = json.loads(text)
        if report['failed'] or report['passed'] != entry['expected']:
            raise RuntimeError('JS regression failed: ' + name)
        groups[name] = report['passed']
    after = plan_check(out)
    if before['sourceTree'] != after['sourceTree']:
        raise RuntimeError('Source tree changed during contracts')
    result = {'schema': 1, 'mode': 'contracts', 'passed': True, 'sourceVerifiedBeforeAndAfter': True, 'checks': sum(groups.values()), 'groups': groups, 'sourceTree': manifest['sourceTree'],
              'desktopAutomation': False, 'gameInput': False,
              'scope': 'Production-linked native/JS contracts; capture/input boundaries use deterministic adapters. Not game receipt evidence.'}
    write_json(out / 'contracts.json', result)
    return result

def prepare_live(out):
    run_id = dt.datetime.now().strftime('%Y%m%d_%H%M%S') + '_' + uuid.uuid4().hex[:6]
    expected = {'schema': 1, 'whiteList': [], 'blackList': [], 'fuzzyBlackList': []}
    rules = read_json(DEPLOY / 'package/User/global-pick-rules.json')
    expected['whiteList'].extend(rules['whiteList'])
    expected['blackList'].extend(rules['blackList'])
    for name, key in [('pick_white_lists.txt', 'whiteList'), ('pick_black_lists.txt', 'blackList'), ('pick_fuzzy_black_lists.txt', 'fuzzyBlackList')]:
        file = ROOT / 'User' / name
        if file.exists():
            expected[key].extend(x.strip() for x in file.read_text(encoding='utf-8-sig').splitlines() if x.strip())
    for file in (ROOT / 'User/JsScript/AutoHoeingOneDragon/blacklists').glob('*.json'):
        if file.name != '示例.json':
            expected['blackList'].extend(read_json(file))
    for key in ('whiteList', 'blackList', 'fuzzyBlackList'):
        expected[key] = sorted(set(expected[key]))
    groups, scripts = [], []
    for phase in ('before', 'after'):
        name = f'_E2E_GlobalPickup_{run_id}_{phase}'
        script = ROOT / 'User/JsScript' / name
        script.mkdir(parents=True, exist_ok=False)
        shutil.copyfile(Path(__file__).with_name('live-probe.js'), script / 'main.js')
        shutil.copyfile(SOURCE / 'Integrations/AutoHoeingOneDragon/point-loot-policy.js', script / 'policy.js')
        shutil.copyfile(ROOT / 'User/JsScript/AutoHoeingOneDragon/item-catalog.json', script / 'catalog.json')
        write_json(script / 'expected.json', {**expected, 'version': VERSION, 'phase': phase, 'runId': run_id})
        write_json(script / 'manifest.json', {'manifest_version': 1, 'name': name, 'version': '1.0.0', 'description': 'CLI SDK E2E; no desktop input',
                                            'authors': [{'name': 'JerryChen97'}], 'main': 'main.js', 'saved_files': ['*.json']})
        group = {'name': name, 'index': 900, 'config': {'pathingConfig': {'enabled': False, 'autoPickEnabled': False,
                  'autoSkipEnabled': False, 'autoEatEnabled': False, 'taskCompletionSkipRuleConfig': {'enable': False}}},
                 'projects': [{'name': name, 'folderName': name, 'index': 1, 'type': 'Javascript', 'status': 'Enabled',
                               'schedule': 'Daily', 'runNum': 1, 'allowJsNotification': False}]}
        write_json(ROOT / 'User/ScriptGroup' / (name + '.json'), group)
        groups.append(name)
        scripts.append(str(script))
    result = {'schema': 1, 'runId': run_id, 'groups': groups, 'scripts': scripts,
              'probeName': '__BGI_E2E_NO_REAL_ITEM_' + run_id + '__', 'desktopAutomation': False, 'gameInput': False}
    write_json(out / 'live-run.json', result)
    return result

def atomic_write(file, content):
    temp = file.with_name(file.name + '.e2e-' + uuid.uuid4().hex)
    temp.write_bytes(content)
    try:
        for attempt in range(30):
            try:
                os.replace(temp, file)
                return
            except PermissionError:
                if attempt == 29:
                    raise
                time.sleep(0.1)
    finally:
        if temp.exists():
            temp.unlink()

def groups_finished(state):
    if not state.get('groups'):
        return True  # Protocol-only fixtures do not launch application groups.
    days = [dt.date.today(), dt.date.today() - dt.timedelta(days=1)]
    logs = ''
    for day in days:
        file = ROOT / 'log' / ('better-genshin-impact' + day.strftime('%Y%m%d') + '.log')
        if file.exists():
            logs += file.read_text(encoding='utf-8-sig', errors='replace')
    return all(f'配置组 "{name}" 执行结束' in logs for name in state['groups'])

def live_wait(out, timeout):
    state = read_json(out / 'live-run.json')
    scripts = [Path(value) for value in state['scripts']]
    global_file = ROOT / 'User/global-pick-rules.json'
    deadline = time.monotonic() + timeout
    try:
        original_hash = sha(global_file)
        while time.monotonic() < deadline:
            if sha(global_file) != original_hash:
                raise RuntimeError('Concurrent global rule edit preserved; read-only SDK test stopped')
            reports = []
            for folder in scripts:
                try:
                    report = read_json(folder / 'result.json')
                except (FileNotFoundError, json.JSONDecodeError, PermissionError):
                    continue
                if report.get('runId') != state['runId']:
                    raise RuntimeError('Result belongs to another E2E execution')
                reports.append(report)
            if reports and any(not report.get('passed') for report in reports):
                raise RuntimeError('Installed SDK E2E rejected: ' + json.dumps(reports, ensure_ascii=False))
            if len(reports) == 2:
                if reports[0]['snapshot'] != reports[1]['snapshot']:
                    raise RuntimeError('Global rules changed across task/group exit')
                if not groups_finished(state):
                    time.sleep(0.2)
                    continue
                result = {'schema': 1, 'mode': 'live-sdk', 'passed': True, 'runId': state['runId'], 'reports': reports,
                          'desktopAutomation': False, 'gameInput': False, 'globalRulesReadOnly': True,
                          'worldPickupReceipt': 'not exercised'}
                write_json(out / 'live-result.json', result)
                return result
            time.sleep(0.2)
        raise TimeoutError('Live SDK did not finish; inspect BGI log / login readiness. No UI fallback is permitted.')
    finally:
        # This controller never writes or restores the live global rules file.
        write_json(out / 'sdk-attempt-finished.json', {'runId': state['runId'], 'finished': True,
                   'globalRulesReadOnly': True, 'nativeGroupsFinished': groups_finished(state),
                   'at': dt.datetime.now().astimezone().isoformat()})


def archive_live(out):
    state = read_json(out / 'live-run.json')
    finished = read_json(out / 'sdk-attempt-finished.json')
    if finished.get('runId') != state['runId'] or not finished.get('finished') or not read_json(out / 'game-close.json').get('passed'):
        raise RuntimeError('SDK attempt and normal game shutdown must finish before archiving fixtures')
    if not groups_finished(state):
        raise RuntimeError('Native task groups have not finished')
    moves = []
    if len(state['scripts']) != 2 or len(state['groups']) != 2:
        raise RuntimeError('Unexpected fixture set')
    for script_path, group, phase in zip(state['scripts'], state['groups'], ('before', 'after')):
        name = '_E2E_GlobalPickup_' + state['runId'] + '_' + phase
        source = Path(script_path).resolve()
        if group != name or source != (ROOT / 'User/JsScript' / name).resolve():
            raise RuntimeError('Fixture ownership/path mismatch')
        if read_json(source / 'expected.json')['runId'] != state['runId'] or read_json(source / 'manifest.json')['name'] != name:
            raise RuntimeError('Fixture ownership marker changed')
        group_file = ROOT / 'User/ScriptGroup' / (name + '.json')
        if read_json(group_file)['name'] != name:
            raise RuntimeError('Group ownership marker changed')
        for item in (source, group_file):
            destination = (out / 'archived-fixtures' / item.relative_to(ROOT)).resolve()
            if not item.resolve().is_relative_to(ROOT.resolve()) or not destination.is_relative_to(ROOT.resolve()):
                raise RuntimeError('Archive paths must stay within the workspace')
            if destination.exists():
                raise RuntimeError('Archive already exists')
            moves.append((item, destination))
    for source, destination in moves:
        destination.parent.mkdir(parents=True, exist_ok=True)
        shutil.move(str(source), str(destination))
    result = {'passed': True, 'runId': state['runId'], 'scriptFolders': 2, 'groupFiles': 2,
              'retainedUnder': str(out / 'archived-fixtures'), 'desktopAutomation': False}
    write_json(out / 'archive.json', result)
    return result

def verify_install(out):
    manifest = read_json(DEPLOY / 'package-manifest.json')
    files = []
    for item in manifest['files']:
        actual = sha(ROOT / item['file'])
        if actual != item['candidateSha256']:
            raise RuntimeError('Installed hash mismatch: ' + item['file'])
        files.append({'file': item['file'], 'sha256': actual})
    result = {'schema': 1, 'mode': 'verify-install', 'passed': True, 'files': files, 'desktopAutomation': False}
    write_json(out / 'verify-install.json', result)
    return result

def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('mode', choices=['plan', 'contracts', 'prepare-live', 'live', 'verify-install', 'archive-live'])
    parser.add_argument('--out', default=str(DEPLOY / 'e2e'))
    parser.add_argument('--timeout', type=int, default=420)
    parser.add_argument('--package', required=True)
    parser.add_argument('--root', default=str(ROOT))
    args = parser.parse_args()
    configure(args.package, args.root)
    out = Path(args.out).resolve(); out.mkdir(parents=True, exist_ok=True)
    actions = {'plan': plan_check, 'contracts': contracts, 'prepare-live': prepare_live, 'verify-install': verify_install, 'archive-live': archive_live}
    try:
        result = live_wait(out, args.timeout) if args.mode == 'live' else actions[args.mode](out)
        print(json.dumps(result, ensure_ascii=False, indent=2))
    except Exception as error:
        write_json(out / (args.mode + '-error.json'), {'passed': False, 'error': str(error), 'desktopAutomation': False})
        print(str(error), file=sys.stderr)
        return 1
    return 0

if __name__ == '__main__':
    raise SystemExit(main())
