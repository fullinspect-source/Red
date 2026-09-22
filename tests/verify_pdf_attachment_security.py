#!/usr/bin/env python3
"""Local source-diff and publish audit. Never reads the private provider or live INS.

Run after staging intended files so new sources are included in the Git manifest.
Only filenames/rule names, never matching secret bytes, are reported.
"""
import argparse
import hashlib
import json
from pathlib import Path
import re
import subprocess

ROOT = Path(__file__).resolve().parents[1]
BASE = '5281d6d8c82cceef74f1e0b0fbe0b15189c92d67'
RULES = {
    'google': rb'AIza[0-9A-Za-z_-]{35}',
    'openai': rb'(?<![A-Za-z0-9_])sk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{32,}',
    'github': rb'(?:gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{40,})',
    'aws': rb'(?:AKIA|ASIA)[A-Z0-9]{16}',
    'private-key': rb'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----',
}
FORBIDDEN = {'settings.txt', 'license.lic', 'embeddedapikeyprovider.generated.cs'}


def git(*args):
    return subprocess.check_output(['git', *args], cwd=ROOT)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--publish', default='work/followup-verification/publish')
    parser.add_argument('--output', default='work/followup-verification/security-audit.json')
    args = parser.parse_args()
    publish, output = (ROOT / args.publish).resolve(), (ROOT / args.output).resolve()
    if not publish.is_relative_to(ROOT) or not output.is_relative_to(ROOT):
        parser.error('Evidence must remain inside the worktree')
    files = sorted(git('diff', '--name-only', BASE).decode().splitlines())
    findings = []
    manifests = {}

    def scan(path, label):
        if path.name.lower() in FORBIDDEN:
            findings.append({'file': label, 'rule': 'forbidden-file'})
            return  # Do not read settings, licenses or generated provider sources.
        data = path.read_bytes()
        manifests[label] = hashlib.sha256(data).hexdigest()
        # Preserve UTF-16 string terminators rather than deleting all NUL bytes:
        # deleting them joins unrelated binary strings into spurious token matches.
        variants = [data, data.decode('utf-16-le', errors='ignore').encode('utf-8'),
                    data.decode('utf-16-be', errors='ignore').encode('utf-8')]
        for name, pattern in RULES.items():
            if any(re.search(pattern, variant) for variant in variants):
                findings.append({'file': label, 'rule': name})

    for name in files:
        path = ROOT / name
        if path.is_file():
            scan(path, name)
    published = sorted(p for p in publish.rglob('*') if p.is_file())
    if not (publish / 'Red.exe').is_file() or not (publish / 'Red.dll').is_file():
        findings.append({'rule': 'missing-publish'})
    for path in published:
        scan(path, 'publish/' + str(path.relative_to(publish)))
    diff = git('diff', '--no-ext-diff', '--no-color', BASE)
    whitespace = subprocess.run(['git', '-c', 'core.whitespace=blank-at-eol,blank-at-eof,space-before-tab,cr-at-eol',
                                 'diff', '--check', BASE], cwd=ROOT, capture_output=True, text=True)
    # Independently inspect actual spaces/tabs, allowing only the CR of existing CRLF.
    added_trailing = [i for i, line in enumerate(diff.split(b'\n'), 1)
                      if line.startswith(b'+') and not line.startswith(b'+++')
                      and line.rstrip(b'\r').endswith((b' ', b'\t'))]
    result = {
        'commit': git('rev-parse', 'HEAD').decode().strip(),
        'base': BASE, 'source_files': files, 'source_count': len(files),
        'publish_files_scanned': len(published), 'findings': findings,
        'whitespace_exit': whitespace.returncode, 'whitespace_output': whitespace.stdout,
        'added_lines_with_actual_trailing_whitespace': added_trailing,
        'diff_sha256': hashlib.sha256(diff).hexdigest(), 'sha256': manifests,
        'provider_inspected': False,
        'limitation': 'Pattern scans do not prove absence of secrets in every possible encoding.',
    }
    result['passed'] = not findings and not whitespace.returncode and not added_trailing
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(result, indent=2) + '\n')
    print(json.dumps({k: v for k, v in result.items() if k not in ('sha256', 'source_files')}, indent=2))
    return 0 if result['passed'] else 1


if __name__ == '__main__':
    raise SystemExit(main())
