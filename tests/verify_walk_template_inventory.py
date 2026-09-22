#!/usr/bin/env python3
"""Read-only live fixture audit. Evidence is private and must never be committed.

Run with a Python providing pypdf (or PyPDF2). Only writes artifacts/work/,
which is ignored by the repository's existing work/ rule. No fixture copies.
Historical counts are a lower-bound regression gate, not an inferred live total.
"""
import argparse
from collections import Counter
from datetime import datetime, timezone
import hashlib
import importlib
import io
import json
import os
from pathlib import Path
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]
DEFAULT_SOURCE = Path('/Users/trentfuller/Library/CloudStorage/Dropbox/Inspections')
EXPECTED = {
    'New Home Orientation Beaumont': ('DR Horton - New Home Orientation Form Beaumont.pdf', 6),
    'New Home Orientation Corpus Christi': ('DR Horton - New Home Orientation Form Corpus Christi.pdf', 6),
    'New Home Orientation North Central Texas': ('DR Horton - New Home Orientation Form NCT.pdf', 6),
    'New Home Orientation Louisiana West': ('DR Horton Louisiana West - Form and Handbook.pdf', 18),
}
SAMPLES = ['Review/2215651-BWT-2-TF.ins', 'Archive/2537336-BWT-2-TL.ins',
           'Archive/2518268-BWT-1-VR.ins', 'Archive/2555725-BWT-1-AC.ins']


def digest(data):
    return hashlib.sha256(data).hexdigest()


def buttons(value, location='$'):
    if isinstance(value, dict):
        if str(value.get('ControlName', '')).casefold() == 'documentbutton':
            yield location, value
        for key, child in value.items():
            yield from buttons(child, location + '.' + key)
    elif isinstance(value, list):
        for index, child in enumerate(value):
            yield from buttons(child, f'{location}[{index}]')


def enumerate_ins(source, errors):
    paths = []
    for folder in ('Archive', 'MyList', 'Review'):
        directory = source / folder
        if not directory.is_dir():
            errors.append(f'Missing/inaccessible source directory: {directory}')
            continue
        def onerror(error):
            errors.append(f'Inventory enumeration failure: {error}')
        for base, directories, files in os.walk(directory, onerror=onerror, followlinks=False):
            for name in files:
                if name.lower().endswith('.ins'):
                    paths.append(Path(base) / name)
    return sorted(set(paths))


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, default=DEFAULT_SOURCE)
    parser.add_argument('--minimum-declarations', type=int, default=58)
    args = parser.parse_args()
    errors = []
    evidence = {'started_utc': datetime.now(timezone.utc).isoformat(),
                'source': str(args.source), 'historical_observation': {'ins_files': 1850, 'declarations': 58},
                'files': [], 'declarations': [], 'templates': [], 'errors': errors}
    output = ROOT / 'artifacts' / 'work' / 'walk-template-inventory.json'
    ignored = subprocess.run(['git', 'check-ignore', '-q', str(output)], cwd=ROOT).returncode == 0
    if not ignored:
        raise SystemExit('Refusing private evidence output because git does not ignore it.')
    reader = None
    for name in ('pypdf', 'PyPDF2'):
        try:
            module = importlib.import_module(name)
            reader = module.PdfReader
            evidence['pdf_library'] = name + ' ' + getattr(module, '__version__', 'unknown')
            break
        except ImportError:
            pass
    if reader is None:
        errors.append('No installed pypdf/PyPDF2 reader; page counts unverified.')
    paths = enumerate_ins(args.source, errors)
    evidence['ins_enumerated'] = len(paths)
    evidence['folder_counts'] = dict(Counter(p.relative_to(args.source).parts[0] for p in paths))
    for path in paths:
        relative = str(path.relative_to(args.source))
        entry: dict = {'path': relative, 'kind': 'ins'}
        evidence['files'].append(entry)
        try:
            raw = path.read_bytes()
            entry.update(sha256_before=digest(raw), size_bytes=len(raw))
            document = json.loads(raw)
            if not isinstance(document, dict):
                raise ValueError('INS root is not an object')
            entry['parsed'] = True
            found = list(buttons(document))
            if found and len(found) != 1:
                errors.append(f'{relative}: expected one DocumentButton, found {len(found)}')
            for location, item in found:
                declaration = {'path': relative, 'json_path': location,
                               'inspection_code': document.get('InspectionCode'),
                               'inspection_name': document.get('InspectionName'),
                               'item_name': item.get('Name'), 'template': item.get('Template')}
                evidence['declarations'].append(declaration)
                if declaration['inspection_code'] != 'BWT':
                    errors.append(f'{relative}: DocumentButton is not exactly BWT')
                expected = EXPECTED.get(declaration['inspection_name'])
                if expected is None or declaration['template'] != expected[0]:
                    errors.append(f'{relative}: unexpected exact inspection-name/template mapping: {declaration}')
        except Exception as exc:
            entry['error'] = f'{type(exc).__name__}: {exc}'
            errors.append(f'{relative}: {entry["error"]}')
    declarations = evidence['declarations']
    evidence['ins_parsed'] = sum(x.get('parsed', False) for x in evidence['files'])
    evidence['declaration_count'] = len(declarations)
    evidence['reports_with_declarations'] = len({d['path'] for d in declarations})
    evidence['template_counts'] = dict(Counter(d['template'] for d in declarations if isinstance(d['template'], str)))
    if len(declarations) < args.minimum_declarations:
        errors.append(f'Only {len(declarations)} declarations observed; minimum is {args.minimum_declarations}')
    if set(evidence['template_counts']) != {v[0] for v in EXPECTED.values()}:
        errors.append('Live template set does not exactly equal the four expected filenames')
    declared_paths = {d['path'] for d in declarations}
    evidence['required_samples'] = {name: name in declared_paths for name in SAMPLES}
    if not all(evidence['required_samples'].values()):
        errors.append('One or more task-named real samples is missing its verified declaration')
    for name, (filename, expected_pages) in EXPECTED.items():
        entry = {'path': 'Documents/' + filename, 'kind': 'pdf', 'expected_pages': expected_pages}
        evidence['files'].append(entry)
        evidence['templates'].append(entry)
        try:
            raw = (args.source / entry['path']).read_bytes()
            entry.update(sha256_before=digest(raw), size_bytes=len(raw))
            if not raw.startswith(b'%PDF-') or b'%%EOF' not in raw[-2048:]:
                raise ValueError('Invalid PDF header/EOF')
            if reader:
                pdf = reader(io.BytesIO(raw))
                entry['page_count'] = len(pdf.pages)
                entry['page_dimensions'] = [[float(page.mediabox.width), float(page.mediabox.height)] for page in pdf.pages]
                if entry['page_count'] != expected_pages:
                    errors.append(f'{filename}: expected {expected_pages} pages, got {entry["page_count"]}')
        except Exception as exc:
            entry['error'] = f'{type(exc).__name__}: {exc}'
            errors.append(f'{entry["path"]}: {entry["error"]}')
    # Re-read every input, not just the four representative reports.
    for entry in evidence['files']:
        try:
            entry['sha256_after'] = digest((args.source / entry['path']).read_bytes())
            entry['unchanged'] = entry.get('sha256_before') == entry['sha256_after']
            if not entry['unchanged']:
                errors.append(f'Source changed during audit: {entry["path"]}')
        except Exception as exc:
            errors.append(f'After-hash failed for {entry["path"]}: {type(exc).__name__}: {exc}')
    final_paths = enumerate_ins(args.source, errors)
    evidence['arrived_during_scan'] = sorted(str(p.relative_to(args.source)) for p in set(final_paths) - set(paths))
    evidence['removed_during_scan'] = sorted(str(p.relative_to(args.source)) for p in set(paths) - set(final_paths))
    if evidence['arrived_during_scan'] or evidence['removed_during_scan']:
        errors.append('Inventory changed during scan; rerun to verify a stable complete inventory')
    evidence['sources_hashed_unchanged'] = sum(x.get('unchanged', False) for x in evidence['files'])
    manifest = [(x['path'], x.get('sha256_before')) for x in evidence['files']]
    evidence['before_manifest_sha256'] = digest(json.dumps(manifest, separators=(',', ':')).encode())
    after = [(x['path'], x.get('sha256_after')) for x in evidence['files']]
    evidence['after_manifest_sha256'] = digest(json.dumps(after, separators=(',', ':')).encode())
    evidence['finished_utc'] = datetime.now(timezone.utc).isoformat()
    evidence['passed'] = not errors
    evidence['production_roundtrip'] = 'Not exercised by this inventory script; see WalkTemplateEvidenceHarness evidence.'
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(json.dumps(evidence, indent=2) + '\n')
    print(json.dumps({k: v for k, v in evidence.items() if k not in ('files', 'declarations')}, indent=2))
    print(f'Evidence: {output}')
    return 0 if evidence['passed'] else 1


if __name__ == '__main__':
    sys.exit(main())
