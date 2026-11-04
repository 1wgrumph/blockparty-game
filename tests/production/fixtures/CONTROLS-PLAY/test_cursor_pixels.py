"""Positive and negative pixel-oracle checks; synthetic frames are fixtures only."""
import copy
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest

from PIL import Image, ImageDraw
from cursor_pixels import GLYPH_CENTER, measure, validate_cursors

spec = importlib.util.spec_from_file_location("controls_driver", Path(__file__).with_name("drive-player.py"))
driver = importlib.util.module_from_spec(spec)
spec.loader.exec_module(driver)


class CursorPixelsTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.measurements = {"cursors": []}
        for size in (0.5, 1.0, 2.0):
            px = int(size * 32)
            x, y = (v - px // 2 for v in GLYPH_CENTER)
            for i, state in enumerate(("Default", "HoverInteractable", "HoverResident", "Blocked", "Busy", "Placement")):
                for high in (False, True):
                    path = self.root / ("%s-%s-%s.png" % (state, size, high))
                    image = Image.new("RGB", (1920, 1080), (57, 60, 68))
                    draw = ImageDraw.Draw(image)
                    draw.rectangle((x - 2, y - 2, x + px + 1, y + px + 1), fill="white")
                    draw.rectangle((x, y, x + i + 1, y + px - 1), fill=(0, 0, 0) if high else (72, 72, 72))
                    image.save(path)
                    digest, ratio = measure(path, px, high)
                    self.measurements["cursors"].append(dict(state=state, size=size, px=px, high_contrast=high,
                        contrast=round(ratio, 5), glyph_sha256=digest, frame={"path": path.name}))

    def bind(self):
        (self.root / "measurements.json").write_text(json.dumps(self.measurements))
        (self.root / "controls.json").write_text(json.dumps({"measurements": {}}))
        # Frame references are relative to the repository in the real driver.
        for cursor in self.measurements["cursors"]:
            cursor["frame"]["path"] = str(self.root / cursor["frame"]["path"])
        (self.root / "measurements.json").write_text(json.dumps(self.measurements))
        return driver.bind_glyph_hashes(self.root)

    def test_backed_distinct_frames_pass_both_checks(self):
        self.assertEqual(len(validate_cursors(self.measurements, self.root)), 36)
        self.assertEqual(self.bind(), {"cursors": 36, "sizes": 3})

    def test_stale_black_white_contrast_rejected_by_both_checks(self):
        self.measurements["cursors"][0]["contrast"] = 21
        with self.assertRaisesRegex(ValueError, "contrast receipt differs"):
            validate_cursors(self.measurements, self.root)
        with self.assertRaisesRegex(ValueError, "contrast receipt differs"):
            self.bind()

    def test_high_contrast_collapse_rejected_by_both_checks(self):
        for cursor in self.measurements["cursors"]:
            if cursor["high_contrast"]:
                arrow = next(c for c in self.measurements["cursors"] if c["high_contrast"] and
                             c["size"] == cursor["size"] and c["state"] == "Default")
                cursor["frame"] = copy.deepcopy(arrow["frame"])
                cursor["glyph_sha256"] = arrow["glyph_sha256"]
        with self.assertRaisesRegex(ValueError, "distinct across states.*high_contrast=True"):
            validate_cursors(self.measurements, self.root)
        with self.assertRaisesRegex(ValueError, "distinct across states.*high_contrast=True"):
            self.bind()

    def test_low_contrast_actual_backdrop_rejected(self):
        cursor = self.measurements["cursors"][0]
        path = self.root / cursor["frame"]["path"]
        with Image.open(path) as source:
            image = source.convert("RGB")
        # One world pixel touching the ink must determine the minimum.
        image.putpixel((GLYPH_CENTER[0] - 9, GLYPH_CENTER[1] - 8), (57, 60, 68))
        image.save(path)
        digest, ratio = measure(path, 16, False)
        self.assertLess(ratio, 4.5)
        cursor.update(contrast=round(ratio, 5), glyph_sha256=digest)
        with self.assertRaisesRegex(ValueError, "rendered contrast below"):
            validate_cursors(self.measurements, self.root)

    def test_forged_hash_rejected(self):
        self.measurements["cursors"][0]["glyph_sha256"] = "0" * 64
        with self.assertRaisesRegex(ValueError, "hash differs"):
            validate_cursors(self.measurements, self.root)


if __name__ == "__main__":
    unittest.main()
