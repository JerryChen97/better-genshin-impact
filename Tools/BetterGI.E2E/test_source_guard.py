"""Exercise the real source identity gate using an isolated temporary Git repository."""
import importlib.util
import json
from pathlib import Path
import shutil
import subprocess
import tempfile

HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('bgi_e2e', HERE / 'bgi_e2e.py')
api = importlib.util.module_from_spec(spec)
spec.loader.exec_module(api)
REPORT = api.DEPLOY


def main():
    with tempfile.TemporaryDirectory(prefix='bgi-source-guard-') as folder:
        root = Path(folder)
        source, package = root / 'Repos/fixture', root / 'artifacts/package'
        source.mkdir(parents=True); package.mkdir(parents=True)
        git = ['git', '--no-optional-locks', '-c', 'safe.directory=*']

        def command(*args):
            return subprocess.check_output(git + list(args), cwd=source).decode().strip()

        command('init', '--quiet')
        file = source / 'proof.txt'; file.write_text('reviewed\n', encoding='utf-8')
        command('add', 'proof.txt')
        command('-c', 'user.name=E2E fixture', '-c', 'user.email=e2e@example.invalid', 'commit', '--quiet', '-m', 'fixture')
        head, tree = command('rev-parse', 'HEAD'), command('rev-parse', 'HEAD^{tree}')
        index = package / 'review.index'; shutil.copyfile(source / '.git/index', index)
        api.ROOT, api.SOURCE, api.DEPLOY, api.COMMIT = root, source, package, head
        api.resume_proof = lambda: {'success': 68, 'failed': 0, 'coveragePercent': 100}
        api.write_json(package / 'package-manifest.json', {'sourceTree': tree, 'sourceIndex': str(index), 'files': [],
                                                       'nativeContracts': {'Fixture': 1}, 'jsContracts': {}})
        out = package / 'report'; out.mkdir()
        assert api.plan_check(out)['sourceTree'] == tree
        original_run, calls = api.run, []

        def reject_expected(operation):
            try:
                operation()
            except RuntimeError as error:
                assert 'Source differs' in str(error), str(error)
                return
            raise AssertionError('Dirty source produced a reusable contract receipt')

        file.write_text('unreviewed\n', encoding='utf-8')

        def run_before(*args, **kwargs):
            if args[0][0] == 'dotnet': calls.append('contract'); return '{"passed":1}'
            return original_run(*args, **kwargs)

        api.run = run_before
        api.write_json(out / 'contracts.json', {'passed': True, 'sourceTree': tree, 'stale': True})
        reject_expected(lambda: api.contracts(out))
        assert not calls and not (out / 'contracts.json').exists()
        assert len(list(out.glob('contracts-previous-*.json'))) == 1
        file.write_text('reviewed\n', encoding='utf-8')

        def run_during(*args, **kwargs):
            if args[0][0] == 'dotnet':
                calls.append('contract'); file.write_text('changed during tests\n', encoding='utf-8'); return '{"passed":1}'
            return original_run(*args, **kwargs)

        api.run = run_during
        reject_expected(lambda: api.contracts(out))
        assert calls == ['contract'] and not (out / 'contracts.json').exists()
        file.write_text('reviewed\n', encoding='utf-8')
        api.run = run_before
        result = api.contracts(out)
        assert result['passed'] and result['sourceVerifiedBeforeAndAfter'] and result['sourceTree'] == tree
    result = {'passed': True, 'scenarios': 4,
              'checks': ['clean frozen tree accepted', 'preexisting edit rejects before contract execution',
                         'edit during execution rejects before reusable receipt',
                         'clean checked execution writes verified receipt; stale receipts archived'],
              'scope': 'Real production plan/contracts gates with temporary Git; contract subprocess replaced by a deterministic adapter.',
              'installedFilesTouched': False}
    api.write_json(REPORT / 'e2e/source-guard-selftest.json', result)
    print(json.dumps(result, indent=2))


if __name__ == '__main__':
    main()
