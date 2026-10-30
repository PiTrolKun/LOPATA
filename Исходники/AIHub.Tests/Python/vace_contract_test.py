"""Lightweight contract tests; deliberately no Torch, model import or inference."""
import contextlib
import importlib.util
from pathlib import Path
import sys
import types
import unittest
import json
import tempfile
from unittest.mock import patch

ROOT = Path(__file__).resolve().parents[2] / "AIHub" / "Tools"
sys.path.insert(0, str(ROOT))
import vace_media
import vace_worker
from vace_native import NativeVace


class ContractTests(unittest.TestCase):
    def test_temporal_grid_accounts_for_context(self):
        self.assertEqual(17, vace_media.grid_length(16, 0, 81))
        self.assertEqual(25, vace_media.grid_length(16, 9, 81))
        self.assertEqual(81, vace_media.grid_length(999, 9, 81))
        self.assertEqual(13, vace_media.grid_length(1, 9, 81))
        for prefix in (0, 5, 9, 13):
            for new in range(1, 100):
                length = vace_media.grid_length(new, prefix, 81)
                self.assertEqual(1, length % 4)
                self.assertGreaterEqual(length - prefix, 4)

    def test_coverage_and_parent_chain_use_actual_previous_take(self):
        takes = [dict(Id="a", ItemId="one", Parent="", Frames=17, PrefixFrames=0),
                 dict(Id="b", ItemId="two", Parent="a", Frames=25, PrefixFrames=9),
                 dict(Id="c", ItemId="three", Parent="b", Frames=25, PrefixFrames=9)]
        vace_worker.validate_takes(takes, [dict(Id=x) for x in ("one", "two", "three")], dict(Frames=81))
        self.assertEqual({"one": 17, "two": 16, "three": 16}, vace_worker.coverage(takes))
        takes[2]["Parent"] = "a"
        with self.assertRaises(ValueError):
            vace_worker.validate_takes(takes, [dict(Id=x) for x in ("one", "two", "three")], dict(Frames=81))

    def test_assembly_retains_parent_and_skips_only_child_prefix(self):
        class Frame:
            def __init__(self, value): self.value = value
            def to_ndarray(self, format): return self.value
        class Video:
            def __init__(self, values): self.values = values
            def __enter__(self): return self
            def __exit__(self, *args): return False
            def decode(self, video): return (Frame(x) for x in self.values)
        paths = {"a.mp4": [10, 11, 12, 13, 14], "b.mp4": [90, 91, 92, 20, 21], "c.mp4": [80, 81, 82, 30, 31]}
        av = types.SimpleNamespace(open=lambda path: Video(paths[Path(path).name]))
        captured = []
        def encode(path, frames, *args):
            captured.extend(frames)
            return len(captured)
        takes = [dict(Video="a.mp4", Frames=5, PrefixFrames=0), dict(Video="b.mp4", Frames=5, PrefixFrames=3),
                 dict(Video="c.mp4", Frames=5, PrefixFrames=3)]
        with patch.dict(sys.modules, av=av), patch.object(vace_media, "encode", encode):
            result = vace_media.assemble(ROOT / "whole.mp4", takes, ROOT,
                dict(Width=320, Height=192, OutputHeight=0, Crf=19), {}, "operation")
        self.assertEqual([10, 11, 12, 13, 14, 20, 21, 30, 31], captured)
        self.assertEqual(9, result)

    def test_firstclip_passes_multiple_frames_and_black_white_mask_to_native_node(self):
        class Tensor:
            def __init__(self, shape, fill): self.shape, self.fill, self.assignments = shape, fill, []
            def __setitem__(self, key, value): self.assignments.append((key, value))
        class Pixels:
            def __len__(self): return 9
            def astype(self, value): return self
            def __truediv__(self, value): return self
        tensors, conditioned, mode = [], {}, {"active": False}
        @contextlib.contextmanager
        def inference_mode():
            mode["active"] = True
            try:
                yield
            finally:
                mode["active"] = False
        def tensor(shape, *args):
            value = Tensor(shape, args[0] if args else 1); tensors.append(value); return value
        def condition(*args, **kwargs):
            self.assertTrue(mode["active"], "Native VACE conditioning must use Comfy's inference mode")
            conditioned.update(kwargs)
            return ("positive", "negative", {"samples": "native"}, 1)
        def decode(*args):
            self.assertTrue(mode["active"], "Tiled VAE producer and in-place consumer must share inference mode")
            return (list(range(25)),)
        native = NativeVace.__new__(NativeVace)
        native.model, native.clip, native.vae = "model", "clip", "vae"
        native.settings = dict(Width=320, Height=192, Steps=4)
        native.emit = lambda *args, **kwargs: None
        native.torch = types.SimpleNamespace(inference_mode=inference_mode, from_numpy=lambda p: p, full=tensor, ones=tensor)
        native.nodes = types.SimpleNamespace(CLIPTextEncode=lambda: types.SimpleNamespace(encode=lambda *args: ("conditioning",)),
            KSampler=lambda: types.SimpleNamespace(sample=lambda *args, **kwargs: ({"samples": "sampled"},)),
            VAEDecodeTiled=lambda: types.SimpleNamespace(decode=decode))
        native.condition = types.SimpleNamespace(execute=condition)
        trimmed = []
        native.trim = types.SimpleNamespace(execute=lambda latent, amount: (trimmed.append(amount) or latent,))
        native.reference = lambda paths: None
        self.assertEqual(25, len(native.generate(dict(Prompt="Move forward", Seed=123, Images=[]), 25, Pixels())))
        self.assertEqual((25, 192, 320, 3), conditioned["control_video"].shape)
        self.assertEqual((25, 192, 320), conditioned["control_masks"].shape)
        self.assertEqual(0.5, conditioned["control_video"].fill)
        self.assertEqual(1, conditioned["control_masks"].fill)
        self.assertEqual(9, len(conditioned["control_video"].assignments[0][1]))
        self.assertEqual(0, conditioned["control_masks"].assignments[0][1])
        self.assertEqual(9, conditioned["control_masks"].assignments[0][0].stop)
        self.assertEqual([1], trimmed)
        self.assertFalse(mode["active"])

    def test_extra_reference_images_cannot_be_silently_discarded(self):
        native = NativeVace.__new__(NativeVace)
        with self.assertRaisesRegex(ValueError, "VideoPrep.VaceReferencesLimit"):
            native.reference(["first.png", "second.png"])

    def test_scaling_and_containment(self):
        self.assertEqual((720, 1280), vace_media.output_dimensions(480, 854, 720))
        self.assertEqual((1872, 1080), vace_media.output_dimensions(832, 480, 1080))
        with self.assertRaises(ValueError): vace_media.contained(ROOT, "../escape")
        with self.assertRaises(ValueError): vace_media.output_dimensions(320, 192, 2160)

    def test_worker_three_passes_then_resumes_without_repeating_sampling(self):
        generated, tails, events = [], [], []
        class Native:
            def __init__(self, request, emit): pass
            def generate(self, item, length, prefix):
                generated.append((item["Id"], length, prefix is not None))
                return list(range(length))
            def unload(self): pass
        def encode(path, pixels, *args):
            Path(path).write_bytes(bytes(pixels))
            return len(pixels)
        def tail(path, count):
            tails.append((Path(path).name, count))
            return "nine previous frames", "e" * 64
        def assemble(path, takes, *args):
            Path(path).write_bytes(b"whole")
            return sum(t["Frames"] - t["PrefixFrames"] for t in takes)
        with tempfile.TemporaryDirectory() as temporary:
            directory = Path(temporary)
            request = dict(Id="run", InputHash="f" * 64, Settings=dict(Frames=25, ContextFrames=9, Width=320,
                Height=192, Steps=4, Crf=19, OutputHeight=0), Output=str(directory), ElapsedBeforeAttempt=123,
                Items=[dict(Id="one", Seconds=17 / 16, Seed=123, Images=[], Prompt="start"),
                       dict(Id="two", Seconds=1, Seed=123, Images=[], Prompt="continue"),
                       dict(Id="three", Seconds=1, Seed=123, Images=[], Prompt="finish")],
                Takes=[], Provenance={})
            request_path = directory / "request.json"
            request_path.write_text(json.dumps(request), encoding="utf-8")
            with patch.dict(sys.modules, vace_native=types.SimpleNamespace(NativeVace=Native)), \
                    patch.object(vace_media, "encode", encode), patch.object(vace_media, "tail_frames", tail), \
                    patch.object(vace_media, "assemble", assemble), patch.object(vace_worker.faulthandler, "enable"), \
                    patch.object(vace_worker, "emit", lambda kind, **data: events.append(dict(kind=kind, **data))), \
                    patch.object(sys, "argv", ["vace_worker", str(request_path)]):
                vace_worker.main()
                takes = [e["take"] for e in events if e["kind"] == "take"]
                self.assertEqual([("one", 17, False), ("two", 25, True), ("three", 25, True)], generated)
                self.assertEqual([(takes[0]["Video"], 9), (takes[1]["Video"], 9)], tails)
                self.assertEqual(["", takes[0]["Id"], takes[1]["Id"]], [t["Parent"] for t in takes])
                self.assertEqual(49, [e for e in events if e["kind"] == "whole"][0]["frames"])
                request["Takes"] = takes
                request_path.write_text(json.dumps(request), encoding="utf-8")
                generated.clear(); events.clear()
                vace_worker.main()
                self.assertEqual([], generated)
                self.assertEqual([], [e for e in events if e["kind"] == "take"])
                self.assertEqual(49, [e for e in events if e["kind"] == "whole"][0]["frames"])


if __name__ == "__main__":
    unittest.main()
