#!/usr/bin/env python3
"""R15: output failure exits cleanly; a read-only installation remains playable.

Resource group: isolated-display. Run only against a disposable task build.
Permissions are restored in finally; each invocation owns its output/profile.
"""
import argparse
import json
import os
from pathlib import Path
import signal
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--player', type=Path, required=True)
parser.add_argument('--tree', required=True)
parser.add_argument('--output', type=Path, required=True)
parser.add_argument('--readonly-install', action='store_true')
args = parser.parse_args()
args.output.mkdir(parents=True, exist_ok=False)
player = args.player.resolve()
out = args.output.resolve()
env = os.environ.copy()
profile = out / 'profile'
profile.mkdir()
env['HOME'] = str(profile)
env['XDG_CONFIG_HOME'] = str(profile / '.config')
env.pop('GREYBOX_LOG_DIR', None)
env.pop('GREYBOX_SHOT_DIR', None)
command = ['xvfb-run', '-a', str(player), '-batchmode', '-screen-width', '1280',
           '-screen-height', '720', '-logFile', str(out / 'player.log')]
modes = {}
if args.readonly_install:
    for path in [player.parent, *player.parent.rglob('*')]:
        modes[path] = path.stat().st_mode & 0o777
else:
    blocked = out / 'blocked-output'
    blocked.write_text('owned regular file, not a directory')
    command += ['--m1-mode=ui', '--m1-outdir=' + str(blocked), '--m1-tree=' + args.tree,
                '--m1-binary-sha=' + '0' * 64]
child = None
try:
    for path, mode in modes.items():
        path.chmod(mode & ~0o222)
    child = subprocess.Popen(command, env=env, start_new_session=True,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    try:
        code = child.wait(timeout=180)
    except subprocess.TimeoutExpired:
        os.killpg(child.pid, signal.SIGKILL)
        child.wait()
        raise AssertionError('R15 player hung after startup I/O; inspect player.log')
    expected = 0 if args.readonly_install else 1
    assert code == expected, f'R15 exit {code}, expected {expected}'
    (out / 'native-result.json').write_text(json.dumps({'exit': code, 'expected_exit': expected,
        'readonly_install': args.readonly_install, 'controlled_termination': True}, indent=2) + '\n')
    print('PASS R15', 'read-only install' if args.readonly_install else 'invalid output directory', code)
finally:
    if child is not None and child.poll() is None:
        os.killpg(child.pid, signal.SIGKILL)
        child.wait()
    for path, mode in modes.items():
        path.chmod(mode)
