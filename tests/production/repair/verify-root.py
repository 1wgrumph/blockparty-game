#!/usr/bin/env python3
"""R29: the real verifier accepts a relative root from its parent directory."""
import argparse
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--output', type=Path, required=True)
args = parser.parse_args()
root = args.root.resolve()
args.output.mkdir(parents=True, exist_ok=False)
for package in ('BOT', 'SEIT-AMEND'):
    command = ['python3', str(Path(root.name) / 'tools/verify.py'), '--root', root.name,
               '--package', package, '--seit', 'docs/plans/issue-3-specification/seit.json']
    result = subprocess.run(command, cwd=root.parent, stdin=subprocess.DEVNULL,
                            stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True)
    (args.output / (package + '.log')).write_text(result.stdout)
    assert result.returncode == 0, f'{package} relative-root verifier exit {result.returncode}: {result.stdout[-1200:]}'
    print('PASS R29 relative-root', package)
