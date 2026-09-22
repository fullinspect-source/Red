#!/usr/bin/env python3
"""Local-only RED verification. Never invokes the release publisher or live INS saves.

Logs, temporary synthetic fixtures and build output stay in the worktree. Release
publish uses the real production guard: a missing private provider is a failure,
not permission to fabricate a key or bypass the packaging requirement.
"""
import argparse
import json
import os
from pathlib import Path
import re
import subprocess
import sys

ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--build', action='store_true', help='Also Debug build and Release self-contained publish')
    parser.add_argument('--output', default='work/verification')
    args = parser.parse_args()
    output = (ROOT / args.output).resolve()
    if not output.is_relative_to(ROOT):
        parser.error('Output must be inside this worktree')
    output.mkdir(parents=True, exist_ok=True)
    runtime = output / 'runtime'
    runtime.mkdir(exist_ok=True)
    env = dict(os.environ, TMPDIR=str(runtime), TEMP=str(runtime), TMP=str(runtime),
               XDG_DATA_HOME=str(runtime / 'appdata'), DOTNET_ROLL_FORWARD='Major',
               PYTHONDONTWRITEBYTECODE='1')
    commands = [
        ('lifecycle-generator', [sys.executable, 'tests/generate_save_lifecycle_probe.py']),
        ('orientation-generator', [sys.executable, 'tests/generate_orientation_lifecycle_probe.py']),
        ('attachment-manager-generator', [sys.executable, 'tests/generate_attachment_manager_probe.py']),
        ('python', [sys.executable, '-m', 'unittest', 'discover', '-s', 'tests', '-p', '*test.py']),
    ]
    for name in ('OrientationPdfHarness', 'PdfCleanupHarness', 'SaveLossHarness', 'FailedSaveRecoveryHarness',
                 'ChecklistVisibilityHarness', 'PhotoProcessingHarness',
                 'LastEditHarness', 'AppUpdateHarness',
                 'UpdateUiHarness', 'DataUpdateHarness', 'FramingDesignParserHarness',
                 'EquipmentAirflowHarness', 'EnergySemanticHarness', 'HetMeasuredHarness'):
        commands.append((name, ['dotnet', 'run', '--project', f'tests/{name}']))
    for script in ('ec_parser_regression.py', 'testing_targets_regression.py', 'run_official_autofill_reuse_probe.py'):
        commands.append((script[:-3], [sys.executable, f'tests/{script}']))
    if args.build:
        commands.extend([
            ('debug-build', ['dotnet', 'build', 'InspectionEditor.csproj', '-c', 'Debug', '-r', 'win-x64']),
            ('release-publish', ['dotnet', 'publish', 'InspectionEditor.csproj', '-c', 'Release', '-r',
                                 'win-x64', '--self-contained', 'true', '-o', str(output / 'publish')]),
        ])
    result = {'commit': subprocess.check_output(['git', 'rev-parse', 'HEAD'], cwd=ROOT, text=True).strip(),
              'commands': [],
              'not_run': ['Windows WPF/editor smoke tests require Windows',
                          'WalkTemplateEvidenceHarness and live Orientation/inventory modes require real INS inputs',
                          'ItemRequirementHarness requires a real SCI file and at least eight archive SCI files']}
    for name, command in commands:
        log = output / (name + '.log')
        try:
            run = subprocess.run(command, cwd=ROOT, env=env, text=True, stdout=subprocess.PIPE,
                                 stderr=subprocess.STDOUT, timeout=300)
            text, code = run.stdout, run.returncode
        except subprocess.TimeoutExpired as exc:
            text = exc.stdout or ''
            if isinstance(text, bytes):
                text = text.decode(errors='replace')
            text += '\nVERIFICATION TIMEOUT (300 seconds)\n'
            code = 124
        log.write_text(text)
        summaries = [line for line in text.splitlines() if
                     re.search(r'Ran \d+ tests|checks passed|assertions|behavioral checks|^PASS \d+|Build succeeded|Build FAILED| Warning\(s\)| Error\(s\)|^OK$|^FAILED', line)]
        record = {'name': name, 'command': command, 'exit_code': code, 'log': str(log),
                  'summary': summaries}
        result['commands'].append(record)
        (output / 'results.json').write_text(json.dumps(result, indent=2) + '\n')
        print(f'{name}: exit {code}', flush=True)
        for line in summaries:
            print('  ' + line, flush=True)
        if code:
            print('\n'.join(text.splitlines()[-20:]), flush=True)
    failures = [c['name'] for c in result['commands'] if c['exit_code']]
    print(f'Verified {len(commands)} commands; failures: {failures}; evidence: {output / "results.json"}')
    return bool(failures)


if __name__ == '__main__':
    sys.exit(main())
