#!/usr/bin/env python3
"""Run the accepted M1 reproductions through their owning suites.

The 2026-10-28 owner decisions require a selected travel target kind and a
child fast-food refusal; the same amended inputs fail before repair.
Plain pinned C# runtime, resource group isolated-csharp; no engine starts.
"""
import argparse
from pathlib import Path
import subprocess
import sys

parser = argparse.ArgumentParser()
parser.add_argument('--unity', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = Path(__file__).resolve().parents[3]
args.output.mkdir(parents=True, exist_ok=False)
failures = 0
for package in ['ECONOMY', 'BOT', 'RELATION', 'TIERS', 'FOOD', 'UI-TITLE', 'UI-NAV', 'SAVE']:
    command = [sys.executable, str(root / 'tests/production/run-standalone.py'),
               '--unity', str(args.unity), '--only', package,
               '--output', str(args.output / package)]
    run = subprocess.run(command, cwd=root, stdin=subprocess.DEVNULL,
                         stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    (args.output / (package + '-runner.log')).write_text(run.stdout)
    transcript = args.output / package / (package + '.log')
    if transcript.exists():
        text = transcript.read_text()
        print(text, end='')
        failures += sum(line.startswith('FAIL ') for line in text.splitlines())
        if run.returncode and not any(line.startswith('FAIL ') for line in text.splitlines()):
            failures += 1
    else:
        print(run.stdout, end='')
        failures += 1
print('REPRO FAILURES', failures)
sys.exit(bool(failures))
