"""Model-free checks for explicit, per-slot song-video prompts and resume identity."""
from contextlib import nullcontext
from copy import deepcopy
from dataclasses import dataclass
import math
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
TOOLS = Path(__file__).resolve().parents[2] / "Исходники" / "AIHub" / "Tools"
sys.path.insert(0, str(TOOLS))
import song_clip_glitch as glitch
import song_clip_motion as motion
import song_clip_motion_state as state
import test_motion_state as fixtures


def motion_visual(duration=18):
    plans = state.plan_chunks(math.ceil(duration * 24), 243, 39)
    return {"version": 1, "globalStyle": "One coherent ink-and-gold visual world.", "scenes": [
        {"index": plan["index"], "startSeconds": plan["startFrame"] / 24,
         "endSeconds": min(duration, (plan["startFrame"] + plan["rawFrames"]) / 24),
         "prompt": f"Scene {plan['index']}: a cat examines a mysterious bowl.", "lyricAnchors": ["Миска — круг."]}
        for plan in plans]}


def glitch_visual(duration=20, interval=8):
    count = max(1, math.ceil(duration / interval))
    return {"version": 1, "globalStyle": "One coherent ink-and-gold visual world.", "scenes": [
        {"index": index + 1, "startSeconds": duration * index / (count - 1) if count > 1 else 0,
         "endSeconds": duration * min(index + 1, count - 1) / (count - 1) if count > 1 else duration,
         "prompt": f"Keyframe {index + 1}: a cat and its shadow."} for index in range(count)]}


class VisualPlanValidationTests(unittest.TestCase):
    def test_malformed_explicit_plans_never_fall_back_to_the_generic_prompt(self):
        for module, valid in ((motion, motion_visual()), (glitch, glitch_visual())):
            broken = [None, {}, {**valid, "version": True}, {**valid, "globalStyle": " "},
                      {**valid, "scenes": []}]
            for field, value in (("prompt", ""), ("index", True), ("index", 99),
                                 ("startSeconds", float("nan")), ("endSeconds", float("inf")),
                                 ("startSeconds", False), ("startSeconds", -1)):
                candidate = deepcopy(valid); candidate["scenes"][0][field] = value; broken.append(candidate)
            for candidate in broken:
                with self.subTest(module=module.__name__, candidate=candidate), self.assertRaises(ValueError):
                    module.validate_visual_plan({"prompt": "Must not silently use this", "visualPlan": candidate})
            self.assertIsNone(module.validate_visual_plan({"prompt": "Legacy prompt"}))

    def test_motion_count_and_times_match_overlapping_native_chunks(self):
        plans = state.plan_chunks(18 * 24, 243, 39)
        request = {"visualPlan": motion_visual()}
        motion.validate_visual_plan(request, 18, plans)
        for change in ("count", "time"):
            broken = deepcopy(request)
            if change == "count": broken["visualPlan"]["scenes"].pop()
            else: broken["visualPlan"]["scenes"][1]["startSeconds"] += .2
            with self.subTest(change=change), self.assertRaises(ValueError):
                motion.validate_visual_plan(broken, 18, plans)


class PlannedMotionOrchestrationTests(fixtures.MotionOrchestrationTests):
    def setUp(self):
        super().setUp()
        self.request["visualPlan"] = motion_visual()

    def test_invalid_slot_count_is_rejected_before_native_allocation(self):
        self.request["visualPlan"]["scenes"].pop()
        with patch.object(motion, "NativeMotion", side_effect=AssertionError("Native runtime must not be allocated")):
            with self.assertRaisesRegex(ValueError, "count"):
                self.run_worker()
        self.assertEqual([], fixtures.FakeNative.generated)

    def test_changed_scene_prompt_cannot_reuse_previous_chunk_cache(self):
        self.run_worker()
        fixtures.FakeNative.generated = []
        self.request["visualPlan"]["scenes"][0]["prompt"] = "A different action in a different place."
        with self.assertRaisesRegex(ValueError, "inputs changed"):
            self.run_worker()
        self.assertEqual([], fixtures.FakeNative.generated)


class LegacyResumeIdentityTests(fixtures.MotionOrchestrationTests):
    def hash_for(self, adapter_hash):
        actual = motion.file_hash
        return lambda path: adapter_hash if Path(path).resolve() == Path(motion.__file__).resolve() else actual(path)

    def test_whitelisted_legacy_resume_preserves_completed_chunks_and_reports_actual_adapter(self):
        with patch.object(motion, "file_hash", self.hash_for(motion.LEGACY_ADAPTER_SHA256)):
            self.run_worker()
        before = self.result()
        fixtures.FakeNative.generated = []
        self.run_worker()
        after = self.result()
        self.assertEqual([], fixtures.FakeNative.generated)
        self.assertEqual(before["inputHash"], after["inputHash"])
        self.assertEqual(motion.LEGACY_ADAPTER_SHA256, after["identityCompatibilityHash"])
        self.assertEqual(state.file_hash(motion.__file__), after["actualAdapterSha256"])

    def test_unknown_previous_adapter_is_not_implicitly_trusted(self):
        with patch.object(motion, "file_hash", self.hash_for("a" * 64)):
            self.run_worker()
        fixtures.FakeNative.generated = []
        with self.assertRaisesRegex(ValueError, "inputs changed"):
            self.run_worker()
        self.assertEqual([], fixtures.FakeNative.generated)

    def test_whitelisted_adapter_cannot_cover_changed_legacy_inputs(self):
        with patch.object(motion, "file_hash", self.hash_for(motion.LEGACY_ADAPTER_SHA256)):
            self.run_worker()
        self.request["lyrics"] = "Changed old project text"
        fixtures.FakeNative.generated = []
        with self.assertRaisesRegex(ValueError, "compatibility contract"):
            self.run_worker()
        self.assertEqual([], fixtures.FakeNative.generated)


class MotionPromptConditioningTests(unittest.TestCase):
    def test_scene_prompt_changes_reencode_without_dropping_previous_latent_or_song_slice(self):
        encoded, prepared = [], []
        native = motion.NativeMotion.__new__(motion.NativeMotion)
        visual = motion_visual(25)
        visual["scenes"][1]["prompt"] = visual["scenes"][0]["prompt"]
        native.request = {"width": 608, "height": 352, "rawFrames": 243, "steps": 12,
                          "encoder": "fake", "prompt": "Wrong legacy prompt", "visualPlan": visual}
        native.torch = types.SimpleNamespace(no_grad=nullcontext)
        native.nodes = types.SimpleNamespace(CLIPLoader=lambda: types.SimpleNamespace(load_clip=lambda *_: (object(),)))
        native.memory = types.SimpleNamespace(unload_all_models=lambda: None, soft_empty_cache=lambda: None)
        def encode(_clip, prompt, *_args, **_kwargs):
            encoded.append(prompt); return prompt, "base latent"
        native.encode = types.SimpleNamespace(execute=encode)
        native.empty_latent = types.SimpleNamespace(execute=lambda *_: ("short terminal latent",))
        native.clip = native.video_vae = native.audio_vae = native.conditioned = None
        native.model = object()
        class Prepare:
            def prepare(self, latent, _audio_vae, master, **kwargs):
                prepared.append((master, kwargs)); return (latent,)
        native.motion = types.SimpleNamespace(NODE_CLASS_MAPPINGS={"MiniMaxH3SongMaskedAVContext": Prepare})
        native.noise = types.SimpleNamespace(get_noise=lambda seed: (seed,))
        native.guider = types.SimpleNamespace(get_guider=lambda _model, positive: (positive,))
        native.scheduler = types.SimpleNamespace(get_sigmas=lambda *_: ("sigmas",))
        native.sampler_select = types.SimpleNamespace(get_sampler=lambda _: ("sampler",))
        native.sampler = types.SimpleNamespace(sample=lambda _noise, positive, _sampler, _sigmas, latent: ((positive, latent),))
        master = object(); previous = None; produced = []
        with patch.object(motion, "emit", lambda *_args, **_kwargs: None):
            for plan in state.plan_chunks(25 * 24, 243, 39):
                previous = native.generate(plan, master, previous, plan["index"])
                produced.append(previous)
        self.assertEqual(2, len(encoded), "An identical neighbouring prompt can reuse its expensive encoding.")
        self.assertEqual(motion.visual_prompt(native.request, 1), encoded[0])
        self.assertEqual(motion.visual_prompt(native.request, 3), encoded[1])
        self.assertNotIn("Wrong legacy prompt", " ".join(encoded))
        self.assertIs(master, prepared[1][0])
        self.assertIs(produced[0], prepared[1][1]["source_latent"])
        self.assertEqual(8.5, prepared[1][1]["clip_start_seconds"])
        self.assertEqual(39, prepared[1][1]["context_length"])


@dataclass(frozen=True)
class FakeKeyframe:
    index: int
    t_sec: float
    prompt: str = "Upstream generic prompt"
    segment_label: int = 7


class GlitchPromptTests(fixtures.OwnFiles):
    def test_wrapped_cuda_oom_is_reported_without_reclassifying_other_errors(self):
        class CudaOutOfMemory(Exception):
            pass

        def wrap(cause):
            try:
                raise RuntimeError('Glitchframe could not generate a keyframe') from cause
            except RuntimeError as error:
                return error

        oom = wrap(CudaOutOfMemory('CUDA allocation failed'))
        self.assertEqual('OutOfMemoryError', glitch.worker_error_type(oom, CudaOutOfMemory))
        self.assertEqual('RuntimeError', glitch.worker_error_type(wrap(ValueError('invalid prompt')), CudaOutOfMemory))

    def test_retry_loads_same_pipeline_into_ram_then_offloads_to_the_selected_gpu(self):
        calls = []

        class Pipeline:
            def enable_model_cpu_offload(self, *, device):
                calls.append(device)

        pipeline = Pipeline()

        def load(model_id, *, cache_dir, device):
            calls.append((model_id, cache_dir, device))
            return pipeline

        retry = glitch.cpu_offloaded_loader(load)
        self.assertIs(pipeline, retry('local-sdxl', cache_dir=self.job, device='cuda:1'))
        self.assertEqual([('local-sdxl', self.job, 'cpu'), 'cuda:1'], calls)

    def test_per_index_prompts_preserve_native_metadata_and_keyframe_cache_identity(self):
        class Base:
            def _plan(self, _analysis): return [FakeKeyframe(0, 0), FakeKeyframe(1, 10), FakeKeyframe(2, 20)]
        request = {"interval": 8, "visualPlan": glitch_visual()}
        planned = glitch.planned_background_class(Base, request)()._plan({"duration_sec": 20})
        self.assertEqual([0, 10, 20], [p.t_sec for p in planned])
        self.assertEqual([7, 7, 7], [p.segment_label for p in planned])
        self.assertEqual([request["visualPlan"]["globalStyle"] + "\n" + s["prompt"]
                          for s in request["visualPlan"]["scenes"]], [p.prompt for p in planned])
        first_cache = glitch.cache_root(self.job, request)
        changed = deepcopy(request); changed["visualPlan"]["scenes"][0]["prompt"] += " New action."
        self.assertNotEqual(first_cache, glitch.cache_root(self.job, changed))
        self.assertNotEqual(self.job / "cache", first_cache)
        self.assertEqual(self.job / "cache", glitch.cache_root(self.job, {}))
        self.assertIs(Base, glitch.planned_background_class(Base, {}))

    def test_single_keyframe_uses_midpoint_but_scene_covers_whole_song(self):
        class Base:
            def _plan(self, _analysis): return [FakeKeyframe(0, 2)]
        request = {"interval": 8, "visualPlan": glitch_visual(4)}
        self.assertEqual(2, glitch.planned_background_class(Base, request)()._plan({"duration_sec": 4})[0].t_sec)

    def test_unreviewed_native_anchor_grid_or_count_is_refused(self):
        request = {"interval": 8, "visualPlan": glitch_visual()}
        for entries in ([FakeKeyframe(0, 0)], [FakeKeyframe(0, 0), FakeKeyframe(1, 8), FakeKeyframe(2, 20)]):
            class Base:
                def _plan(self, _analysis): return entries
            with self.subTest(entries=entries), self.assertRaises(ValueError):
                glitch.planned_background_class(Base, request)()._plan({"duration_sec": 20})


if __name__ == "__main__":
    unittest.main(verbosity=2)
