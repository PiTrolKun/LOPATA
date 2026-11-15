"""Model-free regression checks for durable H3 Music Video chunk execution.

Only Python's standard library is required. Tensor serialization, sampling and
media encoding are explicit stubs; the real state/receipt/orchestration code runs.
"""
import json
from contextlib import nullcontext
from pathlib import Path
import subprocess
import sys
import tempfile
import types
import unittest
from unittest.mock import patch


sys.dont_write_bytecode = True
TOOLS = Path(__file__).resolve().parents[2] / "Исходники" / "AIHub" / "Tools"
sys.path.insert(0, str(TOOLS))
import song_clip_motion_state as state
import song_clip_motion as runner


class OwnFiles(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="lopata-motion-unit-")
        self.job = Path(self.temporary.name).resolve()

    def tearDown(self):
        expected_parent = Path(tempfile.gettempdir()).resolve()
        if self.job.parent != expected_parent or not self.job.name.startswith("lopata-motion-unit-"):
            raise RuntimeError("Unsafe test cleanup target.")
        self.temporary.cleanup()


class MotionStateTests(OwnFiles):
    def setUp(self):
        super().setUp()
        self.identity = {"songSha256": "a" * 64, "pins": {"motion": "own-test-pin"}, "width": 608, "height": 352}
        self.cache = state.MotionState(self.job, self.identity, -1)
        self.plans = state.plan_chunks(447, 243, 39)

    @staticmethod
    def write_fake_tensors(path, identity):
        path.write_bytes(state.canonical(identity).encode("utf-8"))

    @staticmethod
    def verify_fake_tensors(path, identity):
        if json.loads(path.read_text(encoding="utf-8")) != identity:
            raise ValueError("Bad fake tensor identity.")
        return {"video": {"shape": [1, 24, 72, 22, 38], "dtype": "float32"}}

    def commit_first(self):
        return self.cache.commit(self.plans[0], self.cache.input_hash,
                                 self.write_fake_tensors, self.verify_fake_tensors)

    def test_plan_covers_tail_without_the_template_clip_limit(self):
        for frames in (1, 243, 244, 447, 448, 24 * 60 * 30):
            with self.subTest(frames=frames):
                plans = state.plan_chunks(frames, 243, 39)
                self.assertGreaterEqual(plans[-1]["startFrame"] + plans[-1]["rawFrames"], frames)
                self.assertTrue(all(124 <= item["rawFrames"] <= 243 for item in plans))
                self.assertTrue(all((item["rawFrames"] - 5) % 17 == 0 for item in plans))
                if len(plans) > 1:
                    self.assertLess(plans[-2]["startFrame"] + 243, frames)
                self.assertEqual(0, plans[0]["contextFrames"])
                self.assertTrue(all(item["contextFrames"] == 39 for item in plans[1:]))
        self.assertGreater(len(state.plan_chunks(24 * 60 * 30, 243, 39)), 64)
        self.assertEqual([243, 124], [item["rawFrames"] for item in state.plan_chunks(300, 243, 39)])
        self.assertEqual([158], [item["rawFrames"] for item in state.plan_chunks(144, 243, 39)])
        self.assertEqual(90, state.plan_chunks(1, 90, 39)[0]["rawFrames"])
        for raw, context in ((242, 39), (243, 40), (39, 39)):
            with self.subTest(raw=raw, context=context), self.assertRaises(ValueError):
                state.plan_chunks(600, raw, context)

    def test_random_seed_and_input_hash_survive_restart(self):
        resumed = state.MotionState(self.job, self.identity, -1)
        self.assertEqual(self.cache.seed, resumed.seed)
        self.assertEqual(self.cache.input_hash, resumed.input_hash)
        first = self.commit_first()
        self.assertEqual(first, resumed.load(self.plans[0], resumed.input_hash))

    def test_changed_inputs_and_previous_hash_are_rejected(self):
        self.commit_first()
        with self.assertRaises(ValueError):
            state.MotionState(self.job, {**self.identity, "songSha256": "b" * 64}, -1)
        with self.assertRaises(ValueError):
            self.cache.load(self.plans[0], "0" * 64)

    def test_one_byte_corruption_is_rejected(self):
        first = self.commit_first()
        data = self.cache.data_path(first)
        payload = bytearray(data.read_bytes())
        payload[len(payload) // 2] ^= 1
        data.write_bytes(payload)
        with self.assertRaises(ValueError):
            self.cache.load(self.plans[0], self.cache.input_hash)

    def test_failed_tensor_readback_creates_no_receipt(self):
        def fail(*_):
            raise ValueError("Simulated readback failure.")
        with self.assertRaises(ValueError):
            self.cache.commit(self.plans[0], self.cache.input_hash, self.write_fake_tensors, fail)
        self.assertIsNone(self.cache.load(self.plans[0], self.cache.input_hash))
        self.assertEqual([], list(self.cache.chunks.iterdir()))

    def test_orphan_tensor_is_not_a_completed_chunk(self):
        data, _receipt = self.cache._paths(1)
        data.write_bytes(b"Interrupted write, with no committed receipt.")
        self.assertIsNone(self.cache.load(self.plans[0], self.cache.input_hash))
        first = self.commit_first()
        self.assertEqual(first, self.cache.load(self.plans[0], self.cache.input_hash))

    def test_crash_between_tensor_and_receipt_is_retryable(self):
        original_atomic_json = state.atomic_json
        def fail_receipt(path, value):
            if Path(path).parent == self.cache.chunks:
                raise OSError("Simulated interruption before the receipt commit.")
            return original_atomic_json(path, value)
        with patch.object(state, "atomic_json", fail_receipt), self.assertRaises(OSError):
            self.commit_first()
        self.assertIsNone(self.cache.load(self.plans[0], self.cache.input_hash))
        self.commit_first()
        self.assertIsNotNone(self.cache.load(self.plans[0], self.cache.input_hash))

    def test_completed_chunk_cannot_be_overwritten(self):
        first = self.commit_first()
        before = self.cache.data_path(first).read_bytes()
        with self.assertRaises(ValueError):
            self.commit_first()
        self.assertEqual(before, self.cache.data_path(first).read_bytes())

    def test_invalid_seed_is_rejected_for_new_and_existing_state(self):
        for seed in (False, 0.0, -2, 2**53):
            with self.subTest(seed=seed):
                with self.assertRaises(ValueError):
                    state.MotionState(self.job, self.identity, seed)
                with tempfile.TemporaryDirectory(prefix="lopata-motion-seed-") as empty:
                    with self.assertRaises(ValueError):
                        state.MotionState(empty, self.identity, seed)

    def test_relative_escape_and_external_absolute_paths_are_rejected(self):
        for relative in ("../escape.json", str(self.job.parent / "escape.json"), "."):
            with self.subTest(relative=relative), self.assertRaises(ValueError):
                state.contained(self.job, relative)

    def test_process_lock_blocks_a_second_writer_and_releases(self):
        code = ("import sys;sys.dont_write_bytecode=True;sys.path.insert(0,sys.argv[1]);"
                "from song_clip_motion_state import operation_lock\n"
                "with operation_lock(sys.argv[2]): pass")
        command = [sys.executable, "-I", "-B", "-c", code, str(TOOLS), str(self.job)]
        with state.operation_lock(self.job):
            blocked = subprocess.run(command, capture_output=True, timeout=10)
            self.assertNotEqual(0, blocked.returncode, "The second process must not acquire an active operation lock.")
        released = subprocess.run(command, capture_output=True, timeout=10)
        self.assertEqual(0, released.returncode, released.stderr.decode("utf-8", "replace"))

    def test_request_rejects_unpinned_source_and_invalid_frame_grid(self):
        component = self.job / "own-component"
        component.write_text("Own test component placeholder.", encoding="utf-8")
        request = {key: str(component) for key in ("libraries", "comfy", "motionSource", "song", "ffmpeg", "ffprobe")}
        request.update(sourceRevision=runner.SOURCE_REVISION, pins={"own": "test"}, width=608, height=352)
        runner.validate_request(request)
        for quality in (1, 100):
            runner.validate_request({**request, "quality": quality})
        for key, value in (("sourceRevision", "wrong"), ("width", 610), ("rawFrames", 242),
                           ("contextFrames", 40), ("projection", "unapproved"), ("quality", 0), ("quality", 101)):
            with self.subTest(field=key), self.assertRaises(ValueError):
                runner.validate_request({**request, key: value})


class MotionTargetLengthTests(unittest.TestCase):
    def test_short_terminal_target_reuses_conditioning_without_reusing_wrong_shape(self):
        encoded_lengths, empty_lengths, prepared_lengths = [], [], []
        conditioning = object()

        def empty_target(width, height, frames):
            video = types.SimpleNamespace(shape=(1, 24, 2 + 5 * ((frames - 5) // 17), height // 16, width // 16))
            audio = types.SimpleNamespace(shape=(1, 32, 2, round(frames * 40 / 24)))
            return {"samples": [video, audio]}

        class FakeReferenceToVideo:
            @staticmethod
            def execute(_clip, _prompt, width, height, length, *_args, **_kwargs):
                encoded_lengths.append(length)
                return conditioning, empty_target(width, height, length)

        class FakeEmptyMiniMaxH3LatentAV:
            @staticmethod
            def execute(width, height, length):
                empty_lengths.append(length)
                return (empty_target(width, height, length),)

        class FakePrepare:
            def prepare(self, latent, _audio_vae, _master, **kwargs):
                video, audio = latent["samples"]
                frames = 5 + 17 * ((video.shape[2] - 2) // 5)
                prepared_lengths.append(frames)
                self_test.assertEqual(round(frames * 40 / 24), audio.shape[-1])
                if kwargs["context_length"]:
                    self_test.assertIsNotNone(kwargs["source_latent"])
                    self_test.assertGreater(frames, kwargs["context_length"])
                return (latent,)

        self_test = self
        native = runner.NativeMotion.__new__(runner.NativeMotion)
        native.request = {"width": 608, "height": 352, "rawFrames": 243, "steps": 20,
                          "encoder": "own-fake", "prompt": "Own prompt"}
        native.torch = types.SimpleNamespace(no_grad=nullcontext)
        native.nodes = types.SimpleNamespace(CLIPLoader=lambda: types.SimpleNamespace(load_clip=lambda *_: (object(),)))
        native.memory = types.SimpleNamespace(unload_all_models=lambda: None, soft_empty_cache=lambda: None)
        native.encode, native.empty_latent = FakeReferenceToVideo, FakeEmptyMiniMaxH3LatentAV
        native.clip = native.video_vae = native.audio_vae = native.conditioned = None
        native.model = object()
        native.motion = types.SimpleNamespace(NODE_CLASS_MAPPINGS={"MiniMaxH3SongMaskedAVContext": FakePrepare})
        native.noise = types.SimpleNamespace(get_noise=lambda seed: (seed,))
        native.guider = types.SimpleNamespace(get_guider=lambda _model, positive: (positive,))
        native.scheduler = types.SimpleNamespace(get_sigmas=lambda *_: ("sigmas",))
        native.sampler_select = types.SimpleNamespace(get_sampler=lambda _: ("sampler",))
        native.sampler = types.SimpleNamespace(sample=lambda _noise, positive, _sampler, _sigmas, latent:
                                             (latent,) if positive is conditioning else ())
        plans = state.plan_chunks(300, 243, 39)
        with patch.object(runner, "emit", lambda *_args, **_kwargs: None):
            first = native.generate(plans[0], {}, None, 123)
            final = native.generate(plans[1], {}, first, 124)
        self.assertEqual([243], encoded_lengths, "The expensive reference encoder must run once.")
        self.assertEqual([124], empty_lengths, "Only the shortened terminal target needs fresh empty AV geometry.")
        self.assertEqual([243, 124], prepared_lengths)
        self.assertEqual(72, first["samples"][0].shape[2])
        self.assertEqual(37, final["samples"][0].shape[2])
        self.assertEqual(405, first["samples"][1].shape[-1])
        self.assertEqual(207, final["samples"][1].shape[-1])


class FakeTensor:
    def __init__(self, samples):
        self.shape = (1, 2, samples)


class FakeTorch:
    nn = types.SimpleNamespace(functional=types.SimpleNamespace(
        pad=lambda tensor, padding: FakeTensor(tensor.shape[-1] + padding[1])))


class FakeNative:
    generated = []
    fail_at = None

    def __init__(self, request, job):
        self.request, self.job, self.torch = request, job, FakeTorch()

    def load_vaes(self):
        pass

    def generate(self, plan, master, previous, seed):
        if self.fail_at == plan["index"]:
            raise RuntimeError("Own simulated interruption.")
        if plan["index"] > 1 and previous is None:
            raise AssertionError("A continuation must retain its previous completed chunk.")
        self.generated.append(plan["index"])
        return {"index": plan["index"], "seed": seed}

    def tensors(self, sample):
        return sample

    def verify_checkpoint(self, path, identity):
        value = json.loads(path.read_text(encoding="utf-8"))
        if json.loads(value["metadata"]["LOPATA_MOTION"]) != identity:
            raise ValueError("Bad fake serialized identity.")
        return {"video": {"shape": [1, 24, 72, 22, 38]}, "audio": {"shape": [1, 32, 2, 405]}}

    def load_checkpoint(self, cache, facts):
        if self.verify_checkpoint(cache.data_path(facts), facts["identity"]) != facts["tensors"]:
            raise ValueError("Changed checkpoint descriptor.")
        return {"samples": ["own-fake-video", "own-fake-audio"]}

    def release_sampling(self):
        pass


def fake_save_file(tensors, path, metadata):
    Path(path).write_text(json.dumps({"tensors": tensors, "metadata": metadata}), encoding="utf-8")


class MotionOrchestrationTests(OwnFiles):
    def setUp(self):
        super().setUp()
        component = self.job / "own-source"
        component.write_text("Own immutable source bytes.", encoding="utf-8")
        self.request = {key: str(component) for key in ("libraries", "comfy", "motionSource", "song", "ffmpeg", "ffprobe")}
        self.request.update(job=str(self.job / "attempt"), sourceRevision=runner.SOURCE_REVISION,
                            pins={"own": "test-pin"}, width=608, height=352, rawFrames=243,
                            contextFrames=39, steps=20, seed=-1)
        FakeNative.generated, FakeNative.fail_at = [], None
        fake_package = types.ModuleType("safetensors")
        fake_torch_module = types.ModuleType("safetensors.torch")
        fake_torch_module.save_file = fake_save_file
        patches = [
            patch.dict(sys.modules, {"safetensors": fake_package, "safetensors.torch": fake_torch_module}),
            patch.object(runner, "NativeMotion", FakeNative),
            patch.object(runner, "decode_master_audio", lambda _path, _torch:
                         {"waveform": FakeTensor(18 * 32000), "sample_rate": 32000}),
            patch.object(runner, "probe_audio_seconds", lambda _request: 18.0),
            patch.object(runner, "encode_video", lambda native, _state, _receipts, frames:
                         (native.job / "own-fake-video.mp4", frames)),
            patch.object(runner, "mux_original_song", lambda _request, job, _video, _duration:
                         job / "own-fake-video.mp4"),
            patch.object(runner, "emit", lambda _kind, **_data: None),
        ]
        for active_patch in patches:
            active_patch.start()
            self.addCleanup(active_patch.stop)

    def run_worker(self):
        runner.run(self.request)

    def result(self):
        return json.loads((self.job / "attempt" / "worker-result.json").read_text(encoding="utf-8"))

    def test_second_run_reuses_every_completed_chunk(self):
        self.run_worker()
        self.assertEqual([1, 2], FakeNative.generated)
        first = self.result()
        self.assertEqual(432, first["frames"])
        self.assertEqual(2, first["completedChunks"])
        FakeNative.generated = []
        self.run_worker()
        self.assertEqual([], FakeNative.generated)
        resumed = self.result()
        self.assertEqual(first["inputHash"], resumed["inputHash"])
        self.assertEqual(first["seed"], resumed["seed"])

    def test_interrupted_second_chunk_resumes_only_the_missing_chunk(self):
        FakeNative.fail_at = 2
        with self.assertRaisesRegex(RuntimeError, "simulated interruption"):
            self.run_worker()
        folder = self.job / "attempt" / "motion-chunks"
        self.assertTrue((folder / "000001.json").is_file())
        self.assertFalse((folder / "000002.json").exists())
        FakeNative.generated, FakeNative.fail_at = [], None
        self.run_worker()
        self.assertEqual([2], FakeNative.generated)

    def test_changed_descriptor_is_rejected_before_reuse(self):
        self.run_worker()
        receipt = self.job / "attempt" / "motion-chunks" / "000001.json"
        facts = json.loads(receipt.read_text(encoding="utf-8"))
        facts["tensors"]["video"]["shape"] = [999]
        receipt.write_text(json.dumps(facts), encoding="utf-8")
        FakeNative.generated = []
        with self.assertRaisesRegex(ValueError, "descriptor"):
            self.run_worker()
        self.assertEqual([], FakeNative.generated)

    def test_original_song_edit_is_rejected_before_reuse(self):
        self.run_worker()
        Path(self.request["song"]).write_text("Changed own source bytes.", encoding="utf-8")
        FakeNative.generated = []
        with self.assertRaisesRegex(ValueError, "inputs changed"):
            self.run_worker()
        self.assertEqual([], FakeNative.generated)


if __name__ == "__main__":
    unittest.main(verbosity=2)
