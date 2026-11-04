#!/usr/bin/env python3
"""Accepted M0 regressions; run --case M0-F1..M0-F6 or all. No live calls/keys."""
import argparse
import ctypes
import contextlib
import io
import select
import struct
import importlib.util
import json
import os
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parents[2]


def receipt_case(case):
    spec = importlib.util.spec_from_file_location("verification", ROOT / "tools/verify.py")
    verify = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(verify)
    with tempfile.TemporaryDirectory() as scratch:
        root = Path(scratch)
        for name in ["Assets", "Packages", "ProjectSettings", "tools", "Build"]:
            (root / name).symlink_to(ROOT / name, target_is_directory=True)
        ev = root / "evidence/production/TOOLCHAIN"
        shutil.copytree(ROOT / "evidence/production/TOOLCHAIN", ev)
        mutations = ["Skipped", "Failed", "Inconclusive", "duplicate", "missing"] if case == "M0-F3" else ["id", "isError", "success", "error", "request", "method"]
        original_xml = (ev / "test-results.xml").read_text()
        original_request = (ev / "mcp-request.json").read_text()
        original_response = (ev / "mcp-response.json").read_text()
        for mutation in mutations:
            (ev / "mcp-request.json").write_text(original_request)
            if case == "M0-F3":
                path = ev / "test-results.xml"
                tree = ET.ElementTree(ET.fromstring(original_xml))
                rows = tree.findall(".//test-case")
                if mutation == "duplicate":
                    rows[1].set("fullname", rows[0].get("fullname"))
                elif mutation == "missing":
                    for parent in tree.iter():
                        if rows[0] in list(parent):
                            parent.remove(rows[0]); break
                else:
                    rows[0].set("result", mutation)
                tree.write(path)
                check = "unity-tests"
            else:
                path = ev / "mcp-response.json"
                data = json.loads(original_response)
                if mutation == "id": data["id"] = "unrelated-operation"
                elif mutation == "isError": data["result"]["isError"] = True
                elif mutation == "success": data["result"]["structuredContent"]["success"] = False
                elif mutation == "error": data["error"] = {"code": -1, "message": "synthetic failure"}
                else:
                    request = json.loads(original_request)
                    if mutation == "method": request["method"] = "initialize"
                    else: request["params"]["arguments"]["action"] = "different_action"
                    (ev / "mcp-request.json").write_text(json.dumps(request))
                path.write_text(json.dumps(data))
                check = "mcp-operation"
            gate = verify.Gate()
            with contextlib.redirect_stdout(io.StringIO()):
                verify.toolchain_deep(gate, root, ROOT / "docs/plans/issue-3-specification/seit.json")
            assert any(cid == check and not ok for cid, ok, _ in gate.results), mutation + " evidence accepted"
            print("PASS", case, "rejects", mutation)


def key_case(case, unity):
    data = unity / "Editor/Data"
    with tempfile.TemporaryDirectory() as scratch:
        exe = Path(scratch) / "keys.exe"
        cmd = [str(data / "netcorerun/netcorerun"), str(data / "DotNetSdkRoslyn/csc.dll"),
               "-nologo", "-langversion:9", "-t:exe", "-out:" + str(exe),
               "-lib:" + str(data / "netcorerun")]
        cmd += ["-r:" + name + ".dll" for name in ["System.Private.CoreLib", "System.Runtime", "System.Console", "System.IO.FileSystem"]]
        cmd += [str(ROOT / p) for p in ["Assets/Blockparty/Services/KeySettings.cs", "Assets/Blockparty/Services/KeyProvider.cs", "tests/production/M0-REGRESSIONS.cs"]]
        env = os.environ.copy()
        env.pop("MODEL_API_KEY", None)
        subprocess.run(cmd, check=True, env=env)
        subprocess.run([cmd[0], str(exe), case], check=True, env=env)
        if case == "M0-F5":
            # Substitute the name during the large write: the fd must retain
            # its original inode, with no synthetic bytes reaching the victim.
            directory = Path(scratch) / "private"
            directory.mkdir(mode=0o777)
            directory.chmod(0o777)
            victim = Path(scratch) / "victim"
            victim.write_text("unchanged")
            libc = ctypes.CDLL(None, use_errno=True)
            fd = libc.inotify_init1(os.O_NONBLOCK | os.O_CLOEXEC)
            assert fd >= 0
            assert libc.inotify_add_watch(fd, os.fsencode(directory), 0x100) >= 0
            process = subprocess.Popen([cmd[0], str(exe), "race", str(directory / "settings.json")], env=env)
            substituted = False
            try:
                if select.select([fd], [], [], 10)[0]:
                    events = os.read(fd, 8192)
                    offset = 0
                    while offset < len(events):
                        _, _, _, size = struct.unpack_from("iIII", events, offset)
                        name = events[offset + 16:offset + 16 + size].split(b"\0", 1)[0].decode()
                        offset += 16 + size
                        if name.endswith(".tmp"):
                            temp = directory / name
                            assert temp.stat().st_mode & 0o777 == 0o600
                            assert directory.stat().st_mode & 0o777 == 0o700
                            temp.unlink()
                            temp.symlink_to(victim)
                            substituted = True
                code = process.wait(timeout=30)
                assert substituted, "path substitution fixture did not run"
                assert code == 2 and victim.read_text() == "unchanged", "redirected key write"
                assert not (directory / "settings.json").exists()
                print("PASS M0-F5 substituted temporary path; fd isolated; directory 0700/file 0600")
            finally:
                os.close(fd)
                if process.poll() is None:
                    process.kill()
                    process.wait()



def run(case, unity):
    if case == "M0-F1":
        source = (ROOT / "Assets/Editor/ToolchainBuild.cs").read_text()
        assert '"Assets/Blockparty/Scenes/PhysicalProof.unity"' in source, "BuildLinux omits PhysicalProof"
        manager = ROOT / "Build/ToolchainProof/ToolchainProof_Data/globalgamemanagers"
        assert manager.is_file() and b"Assets/Blockparty/Scenes/PhysicalProof.unity" in manager.read_bytes(), "built player omits PhysicalProof"
        spec = importlib.util.spec_from_file_location("verification", ROOT / "tools/verify.py")
        verify = importlib.util.module_from_spec(spec); spec.loader.exec_module(verify)
        with tempfile.TemporaryDirectory() as scratch:
            root = Path(scratch)
            for name in ["Assets", "Packages", "ProjectSettings", "tools", "Build"]:
                (root / name).symlink_to(ROOT / name, target_is_directory=True)
            ev = root / "evidence/production/PROOF-SCENE"
            shutil.copytree(ROOT / "evidence/production/PROOF-SCENE", ev)
            receipt = json.loads((ev / "standalone.json").read_text())
            receipt["exit_code"] = 1
            (ev / "standalone.json").write_text(json.dumps(receipt))
            gate = verify.Gate()
            with contextlib.redirect_stdout(io.StringIO()):
                verify.proofscene_deep(gate, root, ROOT / "docs/plans/issue-3-specification/seit.json")
            assert any(cid == "standalone-exit" and not ok for cid, ok, _ in gate.results), "failed player accepted"
            print("PASS M0-F1 failed standalone receipt rejected")
    elif case == "M0-F2":
        text = (ROOT / "ProjectSettings/EditorSettings.asset").read_text()
        assert int(re.search(r"m_EnterPlayModeOptionsEnabled: (\d+)", text)[1]) == 1
        assert int(re.search(r"m_EnterPlayModeOptions: (\d+)", text)[1]) == 1, "domain reload on or scene reload off"
        text = (ROOT / "evidence/production/PROOF-SCENE/playmode-twice.log").read_text()
        assert text.count("PROOF_STATIC_RESET PASS") == 2, "static-reset fixture not run twice"
        assert text.count("PROOF_MODE domainReload=False sceneReload=True") == 2
    elif case in ["M0-F3", "M0-F4"]:
        receipt_case(case)
    else:
        key_case(case, unity)


if __name__ == "__main__":
    ap = argparse.ArgumentParser()
    ap.add_argument("--case", choices=["M0-F" + str(i) for i in range(1, 7)])
    ap.add_argument("--unity", type=Path, required=True, help="Pinned Unity installation root")
    args = ap.parse_args()
    failures = 0
    for case in [args.case] if args.case else ["M0-F" + str(i) for i in range(1, 7)]:
        try:
            run(case, args.unity)
            print("PASS", case, flush=True)
        except (AssertionError, OSError, subprocess.CalledProcessError) as error:
            failures += 1
            print("FAIL", case, str(error), flush=True)
    raise SystemExit(bool(failures))
