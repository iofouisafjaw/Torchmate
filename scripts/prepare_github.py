"""Review and export the current source snapshot; never push or copy Git history.

--check validates the upload inventory, not legal clearance or native acceptance.
--prepare writes a private preparation ZIP plus a fresh, history-free source folder.
"""
import argparse
from datetime import datetime, timezone
import json
from pathlib import Path
import re
from urllib.parse import unquote
import zipfile

from check_provenance import verify as verify_provenance
from release_alpha import ROOT, inputs, prepare, release_gates

PRIVATE_PARTS = {'.git', '.runtime', '.models', 'artifacts', 'benchmarks', 'refine-logs',
                 'bin', 'obj', '__pycache__', 'v0.1.0', 'v0.2.0', 'v0.3.0'}
PRIVATE_SUFFIXES = {'.gguf', '.db', '.sqlite', '.sqlite3', '.plr', '.tplr', '.wld', '.twld',
                    '.log', '.jsonl', '.mkv', '.mp4', '.dll', '.exe', '.tmod', '.zip'}
SECRET_PATTERNS = {
    'github-token': re.compile(r'\b(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})\b'),
    'api-token': re.compile(r'\bsk-(?:proj-|ant-api\d+-)?[A-Za-z0-9_-]{24,}\b'),
    'aws-access-key': re.compile(r'\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'),
    'private-key': re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),
    'authenticated-url': re.compile(r'https?://[^\s/@:]+:[^\s/@]+@'),
}
LINK = re.compile(r'(?<!!)\[[^\]\n]+\]\(([^)\n]+)\)')


def review_files(root, names):
    """Read only selected files; diagnostics never include matched secret values."""
    root = Path(root).resolve()
    names = set(names)
    findings = []
    for name in sorted(names):
        rel = Path(name)
        path = root / name
        if rel.is_absolute() or '..' in rel.parts or any(p in PRIVATE_PARTS for p in rel.parts) \
                or (rel.name.startswith('.env') and rel.name != '.env.example') or rel.suffix.lower() in PRIVATE_SUFFIXES:
            findings.append({'file': name, 'rule': 'private-input'})
            continue
        # Reject symlinks/junctions even when their target is inside this checkout.
        if not path.is_file() or path.resolve() != path or not path.resolve().is_relative_to(root):
            findings.append({'file': name, 'rule': 'missing-or-linked-input'})
            continue
        raw = path.read_bytes()
        if len(raw) > 2_000_000:
            findings.append({'file': name, 'rule': 'unexpected-large-source-file'})
        if rel.suffix.lower() == '.gif':
            if name != 'assets/demo-preview.gif' or raw[:6] not in (b'GIF87a', b'GIF89a'):
                findings.append({'file': name, 'rule': 'unexpected-demo-image'})
            continue
        if rel.suffix.lower() == '.png':
            continue
        try:
            text = raw.decode('utf-8-sig')
        except UnicodeDecodeError:
            findings.append({'file': name, 'rule': 'unexpected-binary-input'})
            continue
        for rule, pattern in SECRET_PATTERNS.items():
            for match in pattern.finditer(text):
                findings.append({'file': name, 'line': text.count('\n', 0, match.start()) + 1, 'rule': rule})
        if rel.suffix.lower() != '.md':
            continue
        for match in LINK.finditer(text):
            target = match.group(1).strip().strip('<>')
            if target.startswith(('#', 'https://', 'http://', 'mailto:')):
                continue
            target = unquote(target.split('#', 1)[0])
            resolved = (path.parent / target).resolve()
            if not resolved.is_relative_to(root) or resolved.relative_to(root).as_posix() not in names:
                findings.append({'file': name, 'line': text.count('\n', 0, match.start()) + 1,
                                 'rule': 'missing-exported-link', 'target': target})
    return findings


def check(root=ROOT):
    root = Path(root)
    names = inputs(root, 'source')
    provenance = verify_provenance(root)
    findings = review_files(root, names)
    return {'schemaVersion': 1, 'version': json.loads((root / 'release/acceptance.json').read_text(encoding='utf-8'))['version'],
            'technicalChecksPassed': not findings, 'files': len(names), 'findings': findings,
            'provenance': provenance, 'publicationBlockers': release_gates(root, 'source'),
            'publicDistributionApproved': False, 'containsOldGitHistory': False,
            'scope': 'Allowlisted current files only; pattern scan is not a comprehensive secret audit.'}


def export(root=ROOT):
    root = Path(root)
    report = check(root)
    if not report['technicalChecksPassed']:
        raise ValueError(json.dumps(report['findings'], ensure_ascii=False))
    archive = prepare(root, kind='source', prepare_only=True)
    version = report['version']
    stamp = datetime.now(timezone.utc).strftime('%Y%m%dT%H%M%SZ')
    destination = root / 'dist/github-preparation' / f'TerrarAI-{version}-source-{stamp}'
    destination.mkdir(parents=True, exist_ok=False)
    source_zip = root / 'dist/alpha-preparation' / archive['archive']
    with zipfile.ZipFile(source_zip) as bundle:
        for item in bundle.infolist():
            target = (destination / item.filename).resolve()
            if not target.is_relative_to(destination.resolve()):
                raise ValueError('Unsafe archive member')
        bundle.extractall(destination)
    # Verify the exported bytes, not merely the source selection.
    from verify_alpha import verify
    verify(destination)
    exported = check(destination)
    if not exported['technicalChecksPassed']:
        raise ValueError('Exported snapshot failed validation')
    # Keep a versioned copy beside this snapshot: later preparations must not
    # make an earlier report point at a same-name ZIP with different bytes.
    preserved_zip = destination.with_name(destination.name + '-PRIVATE-PREP.zip')
    preserved_zip.write_bytes(source_zip.read_bytes())
    preserved_zip.with_suffix('.zip.sha256').write_text(
        f"{archive['sha256']}  {preserved_zip.name}\n", encoding='ascii')
    archive = dict(archive, archive=preserved_zip.name)
    report.update(sourceDirectory=str(destination.resolve()), archive=archive,
                  sourceArchive=str(preserved_zip.resolve()), extractedSnapshotVerified=True)
    report_path = destination.with_suffix('.preparation.json')
    report_path.write_text(json.dumps(report, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
    return report


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    mode = parser.add_mutually_exclusive_group(required=True)
    mode.add_argument('--check', action='store_true')
    mode.add_argument('--prepare', action='store_true')
    args = parser.parse_args()
    report = check() if args.check else export()
    print(json.dumps(report, ensure_ascii=False, indent=2))
    return 0 if report['technicalChecksPassed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
