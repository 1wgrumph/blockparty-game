"""Independent cursor readback oracle shared by the gate and XTest driver."""
import argparse
import hashlib
import json
from pathlib import Path

from PIL import Image

# ControlsPlayDirector.CaptureMeasurements parks the pointer here; no tag text.
GLYPH_CENTER = (1150, 620)


def luminance(rgb):
    channels = [v / 255.0 for v in rgb]
    linear = [v / 12.92 if v <= 0.03928 else ((v + 0.055) / 1.055) ** 2.4
              for v in channels]
    return sum(v * weight for v, weight in zip(linear, (0.2126, 0.7152, 0.0722)))


def contrast(a, b):
    a, b = sorted((luminance(a), luminance(b)))
    return (b + 0.05) / (a + 0.05)


def measure(frame_path, px, high):
    left, top = (v - px // 2 for v in GLYPH_CENTER)
    ink = (0, 0, 0) if high else (72, 72, 72)
    with Image.open(frame_path) as source:
        image = source.convert("RGBA")
        if image.size != (1920, 1080) or px not in (16, 32, 64):
            raise ValueError("cursor frame dimensions/size differ from capture contract")
        digest = hashlib.sha256(image.crop((left, top, left + px, top + px)).tobytes()).hexdigest()
        pixels = image.load()
        edges = []
        for y in range(top, top + px):
            for x in range(left, left + px):
                if pixels[x, y][:3] != ink:
                    continue
                for dx, dy in ((-1, 0), (1, 0), (0, -1), (0, 1)):
                    backdrop = pixels[x + dx, y + dy][:3]
                    if backdrop != ink:
                        edges.append(contrast(ink, backdrop))
        if not edges:
            raise ValueError("cursor has no rendered ink/backdrop edges")
        return digest, min(edges)


def validate_cursors(measurements, root=Path("."), require_hash=True):
    groups = {}
    measured = []
    for cursor in measurements["cursors"]:
        digest, ratio = measure(root / cursor["frame"]["path"], int(cursor["px"]), cursor["high_contrast"])
        key = (float(cursor["size"]), bool(cursor["high_contrast"]))
        states = groups.setdefault(key, {})
        if cursor["state"] in states:
            raise ValueError("duplicate cursor state/size/option")
        states[cursor["state"]] = digest
        measured.append((cursor, digest, ratio))
    expected = {"Default", "HoverInteractable", "HoverResident", "Blocked", "Busy", "Placement"}
    if set(groups) != {(size, high) for size in (0.5, 1.0, 2.0) for high in (False, True)}:
        raise ValueError("cursor size/contrast coverage incomplete")
    for (size, high), states in groups.items():
        if set(states) != expected or len(set(states.values())) != len(expected):
            raise ValueError("cursor glyphs are not distinct across states at size %s high_contrast=%s" % (size, high))
        if not high:
            for state, digest in states.items():
                if digest == groups[(size, True)][state]:
                    raise ValueError("cursor has no distinct high-contrast glyph: " + state)
    for cursor, digest, ratio in measured:
        label = "%s/%s/%s" % (cursor["state"], cursor["size"], cursor["high_contrast"])
        if abs(float(cursor["contrast"]) - ratio) > 0.0001:
            raise ValueError("cursor contrast receipt differs from rendered pixels %s: recorded=%s measured=%.6f" %
                             (label, cursor["contrast"], ratio))
        if ratio < 4.5:
            raise ValueError("cursor rendered contrast below 4.5:1 %s: %.6f" % (label, ratio))
        if require_hash and cursor.get("glyph_sha256") != digest:
            raise ValueError("cursor glyph hash differs from rendered pixels " + label)
    return measured


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, default=Path("."))
    parser.add_argument("--measurements", default="evidence/production/CONTROLS-PLAY/measurements.json")
    args = parser.parse_args()
    try:
        rows = validate_cursors(json.loads((args.root / args.measurements).read_text()), args.root)
        print("CP-CursorPixels PASS samples=%d minimum=%.6f" % (len(rows), min(row[2] for row in rows)))
    except (ValueError, OSError, KeyError) as exc:
        parser.exit(1, "CP-CursorPixels: %s\n" % exc)
