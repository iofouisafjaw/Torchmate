"""Public-source preparation without reading private runtime/history or contacting GitHub."""
import json
from pathlib import Path
import sys
import tempfile
import unittest

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT / 'scripts'))
import prepare_github as github
import release_alpha


class GitHubPreparationTests(unittest.TestCase):
    def test_current_inventory_has_docs_fixtures_and_ci_but_no_local_evidence(self):
        names = release_alpha.inputs(ROOT, 'source')
        for name in ('LICENSE', 'THIRD_PARTY_NOTICES.md', 'licenses/ImproveGame-MIT.txt',
                     'licenses/llama.cpp-MIT.txt', 'licenses/Qwen3-Apache-2.0.txt',
                     'DESIGN.md', 'docs/playtest.md', 'docs/demo-fixes-0.10.34.md', 'docs/architecture.md',
                     'docs/github-preparation.md', 'docs/wiki-v010-provenance.json', 'scripts/prepare_github.py',
                     '.github/workflows/ci.yml', 'tests/fixtures/arena-native-projection.json'):
            self.assertIn(name, names)
        for name in ('.env', '.git/config', 'artifacts/install-0.10.34.json', 'evals/report.md',
                     'evals/history.csv', 'tests/verification-windows-alpha.json', '.runtime/demo-control.json'):
            self.assertNotIn(name, names)
        self.assertEqual(github.review_files(ROOT, names), [])

    def test_scanner_reports_only_location_not_secret_value(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            token = 'ghp_' + 'A' * 36
            (root / 'unsafe.py').write_text('value = "' + token + '"\n', encoding='utf-8')
            result = github.review_files(root, ['unsafe.py'])
            self.assertEqual(result, [{'file': 'unsafe.py', 'line': 1, 'rule': 'github-token'}])
            self.assertNotIn(token, json.dumps(result))

    def test_private_files_are_rejected_without_reading_them(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            # These files deliberately do not exist: the name alone must reject them.
            for name in ('.env', '.env.production', '.git/config', '.runtime/settings.json',
                         'artifacts/recording.mp4', 'notes.jsonl', 'wiki.db', 'weights.gguf', '../outside.py'):
                with self.subTest(name=name):
                    self.assertEqual(github.review_files(root, [name])[0]['rule'], 'private-input')

    def test_links_must_exist_in_the_export_not_just_in_the_worktree(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'README.md').write_text('[guide](guide.md)\n[private](local.md)\n[web](https://example.invalid)\n', encoding='utf-8')
            (root / 'guide.md').write_text('Guide\n', encoding='utf-8')
            (root / 'local.md').write_text('Private notes\n', encoding='utf-8')
            result = github.review_files(root, ['README.md', 'guide.md'])
            self.assertEqual(result, [{'file': 'README.md', 'line': 2, 'rule': 'missing-exported-link', 'target': 'local.md'}])

    def test_internal_symlink_cannot_smuggle_an_excluded_file(self):
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / '.runtime').mkdir()
            private = root / '.runtime/private.txt'
            private.write_text('not public', encoding='utf-8')
            try:
                (root / 'README.md').symlink_to(private)
            except OSError:
                self.skipTest('Host does not permit test symlinks')
            self.assertEqual(github.review_files(root, ['README.md'])[0]['rule'], 'missing-or-linked-input')

    def test_release_acceptance_is_current_but_still_not_complete(self):
        acceptance = json.loads((ROOT / 'release/acceptance.json').read_text(encoding='utf-8'))
        version = next(line.split('=', 1)[1].strip() for line in (ROOT / 'build.txt').read_text(encoding='utf-8').splitlines() if line.startswith('version'))
        self.assertEqual(acceptance['version'], version)
        self.assertFalse(acceptance['nativeAcceptancePassed'])
        self.assertFalse(acceptance['cleanWindowsInstallPassed'])

    def test_mit_selection_does_not_approve_publication_or_replace_dependency_terms(self):
        audit = json.loads((ROOT / 'release/license-audit.json').read_text(encoding='utf-8'))
        self.assertEqual(audit['projectLicense']['spdxId'], 'MIT')
        self.assertEqual(audit['projectLicense']['path'], 'LICENSE')
        self.assertFalse(audit['publicDistributionApproved'])
        self.assertEqual(next(x for x in audit['blockers'] if x['id'] == 'project-license-unassigned')['status'], 'resolved')
        self.assertIn('distribution-not-approved', release_alpha.release_gates(ROOT))
        self.assertNotIn('project-license-unassigned', release_alpha.release_gates(ROOT))
        names = release_alpha.inputs(ROOT, 'source')
        for name in ('LICENSE', *audit['licenseTextSha256']):
            self.assertIn(name, names)

    def test_missing_or_tampered_project_license_blocks_release(self):
        from unittest import mock
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            (root / 'release').mkdir()
            for name in ('build.txt', 'release/license-audit.json', 'release/acceptance.json'):
                (root / name).write_bytes((ROOT / name).read_bytes())
            # Isolate the existing license-integrity gate from source comparison.
            with mock.patch('check_provenance.verify', return_value={'passed': True}):
                for content in (None, 'MIT License truncated'):
                    if content is not None:
                        (root / 'LICENSE').write_text(content, encoding='utf-8')
                    self.assertIn('license-text-missing-or-modified:LICENSE', release_alpha.release_gates(root))

    def test_source_approval_keeps_windows_acceptance_closed(self):
        self.assertEqual(release_alpha.release_gates(ROOT, 'source'), [])
        binary = release_alpha.release_gates(ROOT, 'windows')
        self.assertIn('tracked-dotenv-review', binary)
        self.assertIn('distribution-not-approved', binary)
        self.assertIn('player-native-acceptance-pending', binary)
        self.assertIn('clean-windows-install-pending', binary)

    def test_source_approval_requires_explicit_history_free_scope(self):
        from unittest import mock
        audit = json.loads((ROOT / 'release/license-audit.json').read_text(encoding='utf-8'))
        for field, value in (('approved', False), ('historyExcluded', False), ('scope', 'original-checkout')):
            with self.subTest(field=field), tempfile.TemporaryDirectory() as folder:
                root = Path(folder)
                (root / 'release').mkdir()
                for name in ('build.txt', 'release/acceptance.json'):
                    (root / name).write_bytes((ROOT / name).read_bytes())
                changed = json.loads(json.dumps(audit))
                changed['sourcePublication'][field] = value
                (root / 'release/license-audit.json').write_text(json.dumps(changed), encoding='utf-8')
                with mock.patch('check_provenance.verify', return_value={'passed': True}):
                    gates = release_alpha.release_gates(root, 'source')
                self.assertIn('distribution-not-approved', gates)
                self.assertIn('tracked-dotenv-review', gates)

    def test_public_source_zip_retains_licenses_and_excludes_private_markers(self):
        import zipfile
        # Only redirect archive writes. Use the real inventory and real gate result.
        with tempfile.TemporaryDirectory() as folder:
            root = Path(folder)
            for name in release_alpha.inputs(ROOT, 'source'):
                target = root / name
                target.parent.mkdir(parents=True, exist_ok=True)
                target.write_bytes((ROOT / name).read_bytes())
            result = release_alpha.prepare(root, kind='source')
            self.assertTrue(result['publicDistributionApproved'])
            with zipfile.ZipFile(root / 'dist/alpha' / result['archive']) as archive:
                self.assertNotIn('NOT_FOR_DISTRIBUTION.txt', archive.namelist())
                self.assertNotIn('.git/config', archive.namelist())
                self.assertNotIn('.env', archive.namelist())
                for name in ('LICENSE', 'licenses/ImproveGame-MIT.txt', 'licenses/Qwen3-Apache-2.0.txt'):
                    self.assertEqual(archive.read(name), (ROOT / name).read_bytes())
                manifest = json.loads(archive.read('alpha-manifest.json'))
                self.assertEqual(manifest['blockers'], [])
                self.assertTrue(manifest['publicDistributionApproved'])


if __name__ == '__main__':
    unittest.main()
