#!/usr/bin/env python3
"""CONTROLS-PLAY i7 evidence driver.

Runs the built Linux player twice (settings prepare then restarted run) inside
one Xvfb display and injects real X11 key/button events with XTest. The player
owns the flow state machine; this driver only focuses its window, presses the
key or clicks the rectangle the player reports, and checks that the player
consumed exactly that input.

Run under: xvfb-run -a -s "-screen 0 1920x1080x24" python3 drive-player.py ...
"""
import argparse
import ctypes
import ctypes.util
import hashlib
import json
import os
from pathlib import Path
from cursor_pixels import validate_cursors
import signal
import subprocess
import sys
import time

REMAP_KEYSYMS = {
    "Alpha0": "0", "Alpha1": "1", "Alpha2": "2", "Alpha3": "3", "Alpha4": "4",
    "Alpha5": "5", "Alpha6": "6", "Alpha7": "7", "Alpha8": "8", "Alpha9": "9",
    "Comma": "comma", "Period": "period", "Minus": "minus", "Equals": "equal",
    "Return": "Return", "Space": "space", "Escape": "Escape",
    "Backspace": "BackSpace", "PageUp": "Page_Up", "PageDown": "Page_Down",
}


def keysym_name(key):
    if key in REMAP_KEYSYMS:
        return REMAP_KEYSYMS[key]
    if len(key) == 1:
        return key.lower()
    return key


class X11:
    def __init__(self):
        self.x = ctypes.CDLL(ctypes.util.find_library("X11"))
        self.xt = ctypes.CDLL(ctypes.util.find_library("Xtst"))
        self.x.XOpenDisplay.restype = ctypes.c_void_p
        self.x.XOpenDisplay.argtypes = [ctypes.c_char_p]
        self.x.XDefaultRootWindow.restype = ctypes.c_ulong
        self.x.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
        self.x.XQueryTree.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                                      ctypes.POINTER(ctypes.c_ulong),
                                      ctypes.POINTER(ctypes.c_ulong),
                                      ctypes.POINTER(ctypes.POINTER(ctypes.c_ulong)),
                                      ctypes.POINTER(ctypes.c_uint)]
        self.x.XFetchName.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                                      ctypes.POINTER(ctypes.c_char_p)]
        self.x.XFree.argtypes = [ctypes.c_void_p]
        self.x.XFlush.argtypes = [ctypes.c_void_p]
        self.x.XCloseDisplay.argtypes = [ctypes.c_void_p]
        self.x.XSetInputFocus.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                                          ctypes.c_int, ctypes.c_ulong]
        self.x.XGetGeometry.argtypes = [ctypes.c_void_p, ctypes.c_ulong,
                                        ctypes.POINTER(ctypes.c_ulong),
                                        ctypes.POINTER(ctypes.c_int),
                                        ctypes.POINTER(ctypes.c_int),
                                        ctypes.POINTER(ctypes.c_uint),
                                        ctypes.POINTER(ctypes.c_uint),
                                        ctypes.POINTER(ctypes.c_uint),
                                        ctypes.POINTER(ctypes.c_uint)]
        self.x.XStringToKeysym.restype = ctypes.c_ulong
        self.x.XStringToKeysym.argtypes = [ctypes.c_char_p]
        self.x.XKeysymToKeycode.restype = ctypes.c_ubyte
        self.x.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        self.xt.XTestFakeKeyEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                              ctypes.c_int, ctypes.c_ulong]
        self.xt.XTestFakeButtonEvent.argtypes = [ctypes.c_void_p, ctypes.c_uint,
                                                 ctypes.c_int, ctypes.c_ulong]
        self.xt.XTestFakeMotionEvent.argtypes = [ctypes.c_void_p, ctypes.c_int,
                                                 ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        self.error_handler = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)(
            lambda display, event: 0)
        self.x.XSetErrorHandler.argtypes = [ctypes.c_void_p]
        self.x.XSetErrorHandler(self.error_handler)
        self.display = self.x.XOpenDisplay(None)
        if not self.display:
            raise RuntimeError("X11 display unavailable")
        self.window = 0

    def close(self):
        if self.display:
            self.x.XCloseDisplay(self.display)
            self.display = None

    def find_window(self):
        root = ctypes.c_ulong()
        parent = ctypes.c_ulong()
        children = ctypes.POINTER(ctypes.c_ulong)()
        count = ctypes.c_uint()
        self.x.XQueryTree(self.display, self.x.XDefaultRootWindow(self.display),
                          ctypes.byref(root), ctypes.byref(parent),
                          ctypes.byref(children), ctypes.byref(count))
        try:
            for i in range(count.value):
                name = ctypes.c_char_p()
                if not self.x.XFetchName(self.display, children[i], ctypes.byref(name)):
                    continue
                if not name.value:
                    continue
                title = name.value.decode(errors="replace").lower()
                self.x.XFree(name)
                if "blockparty" in title or "controls" in title:
                    self.window = children[i]
                    return children[i]
        finally:
            if children:
                self.x.XFree(children)
        raise RuntimeError("player window not found")

    def geometry(self):
        root = ctypes.c_ulong()
        x = ctypes.c_int()
        y = ctypes.c_int()
        w = ctypes.c_uint()
        h = ctypes.c_uint()
        border = ctypes.c_uint()
        depth = ctypes.c_uint()
        self.x.XGetGeometry(self.display, self.window, ctypes.byref(root),
                            ctypes.byref(x), ctypes.byref(y), ctypes.byref(w),
                            ctypes.byref(h), ctypes.byref(border), ctypes.byref(depth))
        return x.value, y.value, w.value, h.value

    def focus(self):
        self.x.XSetInputFocus(self.display, self.window, 2, 0)
        self.x.XFlush(self.display)

    def key(self, key):
        self.find_window()
        name = keysym_name(key)
        keysym = self.x.XStringToKeysym(name.encode())
        code = self.x.XKeysymToKeycode(self.display, keysym)
        if not code:
            raise RuntimeError("no X keycode for " + key + " (" + name + ")")
        self.focus()
        time.sleep(0.06)
        self.xt.XTestFakeKeyEvent(self.display, code, 1, 0)
        self.x.XFlush(self.display)
        time.sleep(0.14)
        self.xt.XTestFakeKeyEvent(self.display, code, 0, 0)
        self.x.XFlush(self.display)
        time.sleep(0.05)

    def click(self, x, y):
        self.find_window()
        wx, wy, _, _ = self.geometry()
        self.xt.XTestFakeMotionEvent(self.display, 0, wx + int(x), wy + int(y), 0)
        self.x.XFlush(self.display)
        time.sleep(0.08)
        self.xt.XTestFakeButtonEvent(self.display, 1, 1, 0)
        self.x.XFlush(self.display)
        time.sleep(0.14)
        self.xt.XTestFakeButtonEvent(self.display, 1, 0, 0)
        self.x.XFlush(self.display)
        time.sleep(0.05)


def read_json(path, retries=0):
    for _ in range(retries + 1):
        try:
            return json.loads(path.read_text())
        except (FileNotFoundError, json.JSONDecodeError):
            if retries:
                time.sleep(0.03)
    return None


def wait_ready(path, after_seq, child, timeout):
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        state = read_json(path, 2)
        if state:
            if state.get("phase") == "failed":
                raise RuntimeError("player reported failure: " + str(state))
            if state.get("phase") == "done":
                return state
            if state.get("awaiting_input") and state.get("seq", -1) > after_seq:
                return state
        if child.poll() is not None:
            raise RuntimeError("player exited early rc=" + str(child.returncode))
        time.sleep(0.03)
    raise RuntimeError("timeout waiting for controls-ready.json")


def run_player(player, args, log_path):
    while True:
        editors = subprocess.run(["pgrep", "-af", "[E]ditor/Unity"], capture_output=True, text=True)
        if editors.returncode == 1:
            break
        if editors.returncode != 0:
            raise RuntimeError("cannot check Unity editor occupancy: " + editors.stderr)
        print("Waiting for Unity editor: " + editors.stdout.strip(), flush=True)
        time.sleep(5)
    command = [str(player), "-screen-width", "1920", "-screen-height", "1080",
               "-screen-fullscreen", "0", "-popupwindow", "-logFile", str(log_path)] + args
    child = subprocess.Popen(command, cwd=str(Path.cwd()), start_new_session=True,
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    return command, child


def terminate(child):
    if child.poll() is None:
        try:
            os.killpg(child.pid, signal.SIGTERM)
        except ProcessLookupError:
            pass
        try:
            child.wait(timeout=10)
        except subprocess.TimeoutExpired:
            try:
                os.killpg(child.pid, signal.SIGKILL)
            except ProcessLookupError:
                pass
            child.wait()


def sha256_file(path):
    digest = hashlib.sha256()
    with open(path, "rb") as stream:
        for chunk in iter(lambda: stream.read(1 << 20), b""):
            digest.update(chunk)
    return digest.hexdigest()


def bind_glyph_hashes(out_dir):
    measurements_path = out_dir / "measurements.json"
    measurements = read_json(measurements_path, 2)
    if not measurements:
        raise RuntimeError("measurements.json missing or unreadable")
    measured = validate_cursors(measurements, require_hash=False)
    for cursor, digest, ratio in measured:
        cursor["glyph_sha256"] = digest
    measurements_path.write_text(json.dumps(measurements, indent=2) + "\n")
    controls_path = out_dir / "controls.json"
    controls = read_json(controls_path, 2)
    if not controls:
        raise RuntimeError("controls.json missing or unreadable")
    controls["measurements"]["sha256"] = sha256_file(measurements_path)
    controls_path.write_text(json.dumps(controls, indent=2) + "\n")
    return {"cursors": len(measured), "sizes": 3}


def check_outputs(out_dir):
    controls = read_json(out_dir / "controls.json", 2)
    if not controls:
        raise RuntimeError("controls.json missing or unreadable")
    frames = set()
    flows = {"camera", "select-click", "select-snap", "select-keyboard",
             "action-menu-immediate", "action-menu-append", "hud-read",
             "cursor-states", "pause-anytime", "settings-persist-after-restart"}
    summary = {}
    for mode in ("keyboard_only", "mouse_only", "remapped"):
        run = read_json(out_dir / (mode + ".json"), 2)
        if not run:
            raise RuntimeError(mode + ".json missing")
        events = run["events"]
        devices = {e["device"] for e in events}
        if mode == "keyboard_only" and devices != {"keyboard"}:
            raise RuntimeError("keyboard_only carries " + str(devices))
        if mode == "mouse_only" and devices != {"mouse"}:
            raise RuntimeError("mouse_only carries " + str(devices))
        if mode == "remapped" and devices != {"keyboard"}:
            raise RuntimeError("remapped carries " + str(devices))
        covered = {(e["flow"], float(e["text_scale"])) for e in events}
        missing = {(flow, scale) for flow in flows for scale in (1.0, 1.5)} - covered
        if missing:
            raise RuntimeError(mode + " missing flow/scale " + str(sorted(missing)))
        for e in events:
            path = Path(e["frame"]["path"])
            if not path.is_file():
                raise RuntimeError("missing frame " + str(path))
            frames.add(str(path))
        summary[mode] = len(events)
    measurements = read_json(out_dir / "measurements.json", 2)
    narration = read_json(out_dir / "narration.json", 2)
    if not measurements or not narration:
        raise RuntimeError("measurements or narration missing")
    if len(measurements["elements"]) != 5 or len(measurements["samples"]) != 10:
        raise RuntimeError("measurement elements/samples " + str(len(measurements["elements"])) + "/"
                           + str(len(measurements["samples"])))
    if len(measurements["greyscale"]) != 4 or len(measurements["cursors"]) != 36:
        raise RuntimeError("greyscale/cursor counts " + str(len(measurements["greyscale"])) + "/"
                           + str(len(measurements["cursors"])))
    if len(narration["utterances"]) != 96:
        raise RuntimeError("narration utterances " + str(len(narration["utterances"])))
    return {"events": summary, "frames": len(frames),
            "samples": len(measurements["samples"]),
            "greyscale": len(measurements["greyscale"]),
            "cursors": len(measurements["cursors"]),
            "utterances": len(narration["utterances"])}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--player", type=Path, required=True)
    parser.add_argument("--out", type=Path, required=True)
    parser.add_argument("--settings", type=Path, required=True)
    parser.add_argument("--tree", required=True)
    parser.add_argument("--binary", required=True)
    parser.add_argument("--timeout", type=float, default=600.0)
    args = parser.parse_args()
    out_dir = args.out
    out_dir.mkdir(parents=True, exist_ok=True)
    commands = []

    prepare_args = ["--controls-play=prepare",
                    "--controls-out=" + str(out_dir),
                    "--controls-settings=" + str(args.settings),
                    "--controls-tree=" + args.tree,
                    "--controls-binary-sha=" + args.binary]
    command, child = run_player(args.player, prepare_args, (out_dir / "prepare-player.log").resolve())
    commands.append({"command": " ".join(command), "phase": "prepare"})
    try:
        child.wait(timeout=180)
    finally:
        terminate(child)
    commands[-1]["exit_code"] = child.returncode
    if child.returncode != 0:
        raise RuntimeError("prepare player exit " + str(child.returncode))
    before_pid = int((out_dir / "before-pid.txt").read_text().strip())

    for stale in ("controls-ready.json", "controls-fail.json"):
        path = out_dir / stale
        if path.exists():
            path.unlink()
    run_args = ["--controls-play=run",
                "--controls-out=" + str(out_dir),
                "--controls-settings=" + str(args.settings),
                "--controls-tree=" + args.tree,
                "--controls-binary-sha=" + args.binary]
    command, child = run_player(args.player, run_args, (out_dir / "run-player.log").resolve())
    commands.append({"command": " ".join(command), "phase": "run"})
    x11 = X11()
    sent = 0
    last_seq = 0
    try:
        deadline = time.monotonic() + args.timeout
        while True:
            if time.monotonic() > deadline:
                raise RuntimeError("overall timeout")
            state = wait_ready(out_dir / "controls-ready.json", last_seq, child, 30)
            if state.get("phase") == "done":
                break
            if not x11.window:
                x11.find_window()
            last_seq = int(state["seq"])
            if state["device"] == "keyboard":
                x11.key(state["key"])
                line = "send seq=%s flow=%s op=%s key=%s" % (state["seq"], state["flow"], state["op"], state["key"])
            else:
                x11.click(float(state["x"]), float(state["y"]))
                line = "send seq=%s flow=%s op=%s click=%s,%s" % (state["seq"], state["flow"], state["op"], state["x"], state["y"])
            with (out_dir / "driver.log").open("a") as log:
                log.write(line + "\n")
            sent += 1
            # Wait for the player to consume this input before reading again.
            consumed = time.monotonic() + 30
            while time.monotonic() < consumed:
                current = read_json(out_dir / "controls-ready.json", 2)
                if current and (current.get("seq", 0) > last_seq
                                or current.get("phase") in ("done", "failed")):
                    break
                if child.poll() is not None:
                    raise RuntimeError("player exited while consuming input rc=" + str(child.returncode))
                time.sleep(0.02)
            else:
                raise RuntimeError("player did not consume input seq=" + str(last_seq))
        child.wait(timeout=60)
    finally:
        terminate(child)
        x11.close()
    commands[-1]["exit_code"] = child.returncode
    if child.returncode != 0:
        raise RuntimeError("run player exit " + str(child.returncode))
    summary = check_outputs(out_dir)
    glyphs = bind_glyph_hashes(out_dir)
    controls = read_json(out_dir / "controls.json", 2)
    restart = controls.get("restart", {})
    if restart.get("before_pid") != before_pid:
        raise RuntimeError("restart.before_pid " + str(restart.get("before_pid")) + " != " + str(before_pid))
    if restart.get("after_pid") != child.pid:
        raise RuntimeError("restart.after_pid " + str(restart.get("after_pid")) + " != " + str(child.pid))
    result = {
        "status": "PASS",
        "driver": "XTest real X11 keyboard and mouse events",
        "tree_hash": args.tree,
        "binary_sha256": args.binary,
        "before_pid": before_pid,
        "after_pid": child.pid,
        "inputs_sent": sent,
        "commands": commands,
        "summary": summary,
        "glyphs": glyphs,
    }
    (out_dir / "driver.json").write_text(json.dumps(result, indent=2) + "\n")
    print("CONTROLS-PLAY DRIVER PASS inputs=" + str(sent) + " " + json.dumps(summary))
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as exc:  # noqa: BLE001 - evidence driver reports any failure
        print("CONTROLS-PLAY DRIVER FAIL " + str(exc), file=sys.stderr)
        sys.exit(1)
