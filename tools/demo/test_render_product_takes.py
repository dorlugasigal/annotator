import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("takes", Path(__file__).with_name("render-product-takes.py"))
takes = importlib.util.module_from_spec(spec)
spec.loader.exec_module(takes)


class RecordingEvidenceTests(unittest.TestCase):
    def probe(self, **changes):
        stream = {"codec_type": "video", "codec_name": "h264", "profile": "Main", "level": 32,
                  "pix_fmt": "yuv420p", "codec_tag_string": "avc1", "avg_frame_rate": "30/1"}
        return json.dumps({"streams": [{**stream, **changes}], "format": {"duration": "8"}})

    def metadata(self, source):
        rect = {"x": 70, "y": 600, "width": 930, "height": 72}
        return {
            "path": str(source), "take": "style", "duration": 8, "width": 1200, "height": 1000,
            "contentRect": {"x": 0, "y": 0, "width": 1100, "height": 800},
            "imageWorkspace": False, "toolbarAppearance": "dark", "ink": "lagoon gradient",
            "input": "Native test-owned controls with a scripted pointer.",
            "toolbarAnchorStable": True, "toolbarPropertiesMode": "full", "toolbarBottomInset": 100,
            "toolbarSamples": [{"time": 0, "rect": rect}, {"time": 1, "rect": {**rect, "height": 100}}],
            "actions": [{"gesture": "type-character", "text": character, "time": 2 + index*0.08}
                        for index, character in enumerate("A useful idea.")],
        }

    def test_accepts_stationary_rail_and_native_status_row_without_retiming(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"style-source.mp4"; source.write_bytes(b"source")
            metadata = root/"take.json"; metadata.write_text(json.dumps(self.metadata(source)))
            def render(args, **kwargs): Path(args[-1]).write_bytes(b"encoded")
            probe = self.probe()
            with patch.object(takes.subprocess, "run", side_effect=render), \
                 patch.object(takes.subprocess, "check_output", return_value=probe):
                result = takes.encode(metadata, root/"out", "showcase")
            self.assertEqual(result["speed"], 1)
            self.assertTrue(result["toolbarAnchorStable"])
            self.assertEqual(len(result["actions"]), 14)
            self.assertEqual(result["encoding"]["frameRate"], 30)
            self.assertEqual(result["encoding"]["profile"], "Main")

    def test_rejects_incompatible_encoded_video(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source.mp4"; source.write_bytes(b"source")
            metadata = root/"take.json"; metadata.write_text(json.dumps(self.metadata(source)))
            def render(args, **kwargs): Path(args[-1]).write_bytes(b"encoded")
            for changes in ({"profile": "High"}, {"level": 42}, {"avg_frame_rate": "60/1"},
                            {"pix_fmt": "yuv420p10le"}, {"codec_tag_string": "avc3"}):
                with self.subTest(changes=changes), \
                     patch.object(takes.subprocess, "run", side_effect=render), \
                     patch.object(takes.subprocess, "check_output", return_value=self.probe(**changes)), \
                     self.assertRaisesRegex(ValueError, "H.264 Main"):
                    takes.encode(metadata, root/"out", "showcase")

    def test_rejects_jumps_missing_evidence_and_whole_line_insertion(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); source=root/"source.mp4"; source.write_bytes(b"source")
            cases=[]
            moved=self.metadata(source); moved["toolbarSamples"][1]["rect"]["y"]+=12; cases.append(moved)
            missing=self.metadata(source); missing["toolbarSamples"]=[]; cases.append(missing)
            pasted=self.metadata(source); pasted["actions"]=[{"gesture":"type-character","text":"A useful idea.","time":2}]; cases.append(pasted)
            cramped=self.metadata(source); cramped["toolbarSamples"][0]["rect"]["y"]=730; cases.append(cramped)
            for value in cases:
                path=root/"take.json"; path.write_text(json.dumps(value))
                with self.subTest(value=value), patch.object(takes.subprocess,"run") as render, self.assertRaises(ValueError):
                    takes.encode(path,root/"out","showcase")
                render.assert_not_called()

    def test_region_take_keeps_native_actions_crop_and_recorded_speed(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root/"source.mp4"; source.write_bytes(b"source")
            metadata = self.metadata(source)
            metadata.update(take="region", recordingSource="Real Annotator region chooser; only recorder-owned windows.",
                            actions=[{"control": name, "time": index + 1}
                                     for index, name in enumerate(["Right half", "Center third", "Top left", "Left half"])])
            path = root/"take.json"; path.write_text(json.dumps(metadata))
            def render(args, **kwargs): Path(args[-1]).write_bytes(b"encoded")
            with patch.object(takes.subprocess, "run", side_effect=render), \
                 patch.object(takes.subprocess, "check_output", return_value=self.probe()):
                result = takes.encode(path, root/"out", "showcase")
            self.assertEqual(result["speed"], 1)
            self.assertEqual(result["crop"], [0, 0, 1100, 816])
            self.assertEqual(result["toolbarClearance"], 16)
            metadata["actions"].reverse(); path.write_text(json.dumps(metadata))
            with patch.object(takes.subprocess, "run") as rendered, self.assertRaisesRegex(ValueError, "approved order"):
                takes.encode(path, root/"out", "showcase")
            rendered.assert_not_called()


if __name__ == "__main__":
    unittest.main()


class FocusFramingTests(unittest.TestCase):
    metadata = {"contentRect": {"x": 100, "y": 100, "width": 800, "height": 800},
                "actions": [{"control": "Toolbar", "time": 2, "rect": {"x": 150, "y": 110, "width": 60, "height": 30}}]}

    def test_eased_camera_reaches_each_shot_and_keeps_used_controls_in_frame(self):
        shots = [{"at": 0, "x": 100, "y": 100}, {"at": 1, "move": 0.5, "x": 100, "y": 300},
                 {"at": 1.5, "move": 0.4, "x": 100, "y": 100}]
        self.assertEqual(takes.camera_position(shots, 1.25, "y"), 200)
        self.assertEqual(takes.camera_position(shots, 1.5, "y"), 300)
        self.assertEqual(takes.camera_position(shots, 3, "y"), 100)
        width, height, crop = takes.focus_window(self.metadata, {"width": 400, "height": 300, "shots": shots})
        self.assertEqual((width, height), (400, 300))
        self.assertIn("if(lt(t,1)", crop)

    def test_rejects_a_control_outside_the_frame_when_it_is_used(self):
        shots = [{"at": 0, "x": 100, "y": 100}, {"at": 1, "move": 0.5, "x": 100, "y": 300}]
        with self.assertRaisesRegex(ValueError, "Toolbar control is outside"):
            takes.focus_window(self.metadata, {"width": 400, "height": 300, "shots": shots})
