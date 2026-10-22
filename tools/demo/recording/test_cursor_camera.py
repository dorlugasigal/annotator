import json
from pathlib import Path
import unittest
from cursor_camera import plan, pointer_at


SOURCE = Path(__file__).resolve().parents[3]/"videos/annotator-native-promo/native-quality-source.json"


class CursorCameraTests(unittest.TestCase):
    @unittest.skipUnless(SOURCE.is_file(), "needs dorlugasigal/annotator-media cloned into videos/")
    def test_recorded_controls_stay_visible_without_upscaling(self):
        source = SOURCE
        metadata = json.loads(source.read_text())
        camera = plan(metadata, speed=2)
        self.assertEqual(camera["missedControlPresses"], [])
        self.assertLessEqual(camera["maximumUpscale"], 1)
        width,height = camera["base"][2:]
        for row in camera["frames"]:
            self.assertGreaterEqual(row["x"],0)
            self.assertGreaterEqual(row["y"],0)
            self.assertLessEqual(row["x"]+width/row["zoom"], width+.01)
            self.assertLessEqual(row["y"]+height/row["zoom"], height+.01)
        max_delta = max(abs(a["x"]-b["x"])+abs(a["y"]-b["y"])
                        for a,b in zip(camera["frames"],camera["frames"][1:]))
        self.assertLess(max_delta, 15, "Avoid abrupt camera jumps while following the cursor.")
        for action in metadata["actions"]:
            if action["kind"] != "control":
                continue
            frame = min(len(camera["frames"])-1, round(action["pressTime"]/2*60))
            chunk_start = frame//360*360
            lower = chunk_start+(frame-chunk_start)//8*8
            upper = min(lower+8,min(chunk_start+359,len(camera["frames"])-1))
            blend = (frame-lower)/max(1,upper-lower)
            a,b = camera["frames"][lower],camera["frames"][upper]
            pose = {key:a[key]+(b[key]-a[key])*blend for key in ("x","y","zoom")}
            rect=action["rect"]
            self.assertGreaterEqual(rect["x"]-camera["base"][0],pose["x"]-1,action["label"])
            self.assertGreaterEqual(rect["y"]-camera["base"][1],pose["y"]-1,action["label"])
            self.assertLessEqual(rect["x"]+rect["width"]-camera["base"][0],
                                 pose["x"]+width/pose["zoom"]+1,action["label"])
            self.assertLessEqual(rect["y"]+rect["height"]-camera["base"][1],
                                 pose["y"]+height/pose["zoom"]+1,action["label"])

    def test_interpolation_is_bounded_and_invalid_speed_fails(self):
        samples = [{"time":1,"x":10,"y":20},{"time":2,"x":30,"y":40}]
        self.assertEqual(pointer_at(samples,[1,2],1.5),(20,30))
        self.assertEqual(pointer_at(samples,[1,2],0),(10,20))
        self.assertEqual(pointer_at(samples,[1,2],3),(30,40))
        with self.assertRaises(ValueError):
            plan({}, speed=0)


if __name__ == "__main__":
    unittest.main()
