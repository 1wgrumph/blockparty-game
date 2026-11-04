#!/usr/bin/env python3
"""R03: drive the native player's controls with real X11 mouse events.

Run under an isolated xvfb-run display. Resource group: isolated-display;
the player is reused for all seven flows. Food completion takes 15 game
minutes (15 real seconds), so the load-sized deadline is intentionally 180s.
"""
import argparse
import ctypes
import ctypes.util
import hashlib
import json
import os
from pathlib import Path
import signal
import subprocess
import time


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--player', type=Path, required=True)
    parser.add_argument('--manifest', type=Path, required=True)
    parser.add_argument('--tree', required=True)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--frame-prefix', default='evidence/production/GATE-M1/ui')
    args = parser.parse_args()
    args.output.mkdir(parents=True, exist_ok=False)
    manifest = json.loads(args.manifest.read_text())
    assert manifest['tree_hash'] == args.tree
    digest = hashlib.sha256(args.manifest.read_bytes()).hexdigest()

    def payload_unchanged():
        for row in manifest['files']:
            path = args.player.parent / row['relative_path']
            assert hashlib.sha256(path.read_bytes()).hexdigest() == row['sha256'], row['relative_path']

    payload_unchanged()
    x = ctypes.CDLL(ctypes.util.find_library('X11'))
    xt = ctypes.CDLL(ctypes.util.find_library('Xtst'))
    x.XOpenDisplay.restype = ctypes.c_void_p
    x.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
    x.XDefaultRootWindow.restype = ctypes.c_ulong
    x.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
                            ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)), ctypes.POINTER(ctypes.c_uint)]
    x.XFetchName.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.POINTER(ctypes.c_char_p)]
    x.XSetInputFocus.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int, ctypes.c_ulong]
    x.XFlush.argtypes = [ctypes.c_void_p]
    x.XFree.argtypes = [ctypes.c_void_p]
    x.XCloseDisplay.argtypes = [ctypes.c_void_p]
    xt.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    xt.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    display = x.XOpenDisplay(None)
    assert display, 'X11 display unavailable'
    command = [str(args.player.resolve()), '-screen-width', '1280', '-screen-height', '720',
               '-screen-fullscreen', '0', '-logFile', str((args.output / 'player.log').resolve()),
               '--m1-mode=ui', '--m1-outdir=' + str(args.output.resolve()),
               '--m1-tree=' + args.tree, '--m1-binary-sha=' + digest,
               '--m1-frame-prefix=' + args.frame_prefix]
    child = subprocess.Popen(command, start_new_session=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    clicked = set()
    deadline = time.monotonic() + 180
    try:
        while child.poll() is None and time.monotonic() < deadline:
            ready = args.output / 'ui-ready.json'
            try:
                state = json.loads(ready.read_text())
            except (FileNotFoundError, json.JSONDecodeError):
                time.sleep(.05)
                continue
            step = state['step']
            if step < 9 and step not in clicked and not state['capturing']:
                root, parent = ctypes.c_ulong(), ctypes.c_ulong()
                children, count = ctypes.POINTER(ctypes.c_ulong)(), ctypes.c_uint()
                x.XQueryTree(display, x.XDefaultRootWindow(display), ctypes.byref(root), ctypes.byref(parent),
                             ctypes.byref(children), ctypes.byref(count))
                try:
                    for i in range(count.value):
                        name = ctypes.c_char_p()
                        if x.XFetchName(display, children[i], ctypes.byref(name)) and name.value:
                            title = name.value.decode(errors='replace').lower()
                            x.XFree(name)
                            if 'block' in title:
                                x.XSetInputFocus(display, children[i], 2, 0)
                finally:
                    if children:
                        x.XFree(children)
                xt.XTestFakeMotionEvent(display, 0, 214, 121 + 48 * step, 0)
                xt.XTestFakeButtonEvent(display, 1, 1, 0)
                xt.XTestFakeButtonEvent(display, 1, 0, 0)
                x.XFlush(display)
                clicked.add(step)
            time.sleep(.05)
        assert child.poll() is not None, 'player timed out; inspect player.log and ui-ready.json'
        assert child.returncode == 0, f'player exit {child.returncode}'
        expected = {'title', 'new', 'load', 'inspect', 'map', 'follow', 'takeover'}
        events = json.loads((args.output / 'input.json').read_text())['events']
        assert {event['flow'] for event in events} == expected
        assert clicked == set(range(9)), clicked
        frames = {flow: hashlib.sha256((args.output / (flow + '.png')).read_bytes()).hexdigest()
                  for flow in expected}
        assert len(set(frames.values())) == 7, 'R03: flows render identical frames'
        assert all(event['device'] == 'mouse' for event in events)
        assert events[-1]['flow'] == 'takeover' and events[-1]['game_seconds'] > events[-2]['game_seconds']
        payload_unchanged()
        (args.output / 'native-result.json').write_text(json.dumps({
            'exit': 0, 'input': 'XTest real mouse events', 'clicks': sorted(clicked),
            'frame_sha256': frames, 'unchanged_payload': True,
            'manifest_sha256': digest, 'tree_hash': args.tree}, indent=2) + '\n')
        print('PASS R03 native input, seven distinct frames, live takeover completion, unchanged payload')
    finally:
        if child.poll() is None:
            os.killpg(child.pid, signal.SIGTERM)
            try:
                child.wait(timeout=5)
            except subprocess.TimeoutExpired:
                os.killpg(child.pid, signal.SIGKILL)
                child.wait()
        x.XCloseDisplay(display)


if __name__ == '__main__':
    main()
