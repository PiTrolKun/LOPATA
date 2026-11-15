"""No-model checks for per-file reference roles, canonical H3 prompts and image conditioning."""
from copy import deepcopy
import json
from pathlib import Path
import sys
import types
import unittest
from unittest.mock import patch

sys.dont_write_bytecode = True
TOOLS = Path(__file__).resolve().parents[2] / "Исходники" / "AIHub" / "Tools"
sys.path.insert(0, str(TOOLS))
import song_clip_motion as motion
import song_clip_motion_state as state
import test_motion_state as fixtures
from test_visual_plan import motion_visual


class ReferenceContractTests(fixtures.OwnFiles):
    def setUp(self):
        super().setUp()
        project = self.job / "project"
        attachments = project / "attachments"
        attachments.mkdir(parents=True)
        self.files = [attachments / name for name in ("singer.png", "room.png", "prop.png")]
        for index, file in enumerate(self.files):
            file.write_bytes(bytes([index, index + 1]))
        component = self.job / "component"
        component.write_bytes(b"owned component")
        self.request = {key: str(component) for key in ("libraries", "comfy", "motionSource", "song", "ffmpeg", "ffprobe")}
        self.request.update(job=str(project / "attempts" / ("a" * 32)), sourceRevision=motion.SOURCE_REVISION,
                            pins={"own": "pin"}, width=608, height=352, encoder="fake",
                            reference=str(self.files[0]), visualPlan=motion_visual())
        self.request["references"] = [dict(file=str(file), kind=kind, description=description)
            for file, kind, description in zip(self.files, ("performer", "world", "object"),
                ("Певица исполняет песню.", "Здесь происходят события.", "Предмет участвует в событиях."))]
        self.request["visualPlan"]["referenceRoles"] = [dict(index=index, kind=reference["kind"], description=description)
            for index, reference, description in zip(range(1, 4), self.request["references"],
                ("The referenced singer performs the supplied song.",
                 "The referenced room is the setting of the song events.",
                 "The referenced prop is used by the performer during the song."))]
        self.request["referencePrompts"] = [motion.compose_reference_prompt(self.request, index) for index in (1, 2)]

    def test_every_image_has_its_own_official_label_and_semantic_role(self):
        motion.validate_request(self.request)
        prompt = motion.visual_prompt(self.request, 1)
        for header in ("subject_definitions:", "summary:", "retention_analysis:", "detailed_description:", "overall_soundscape:", "non_diegetic_music:"):
            self.assertEqual(1, prompt.count(header))
        for index in (1, 2, 3):
            self.assertIn(f"<Picture {index}>", prompt)
            self.assertIn(f"<Subject {index}>: fully_preserved", prompt)
        self.assertIn("the performer or performers shown", prompt)
        self.assertIn("the setting or event world shown", prompt)
        self.assertIn("the object shown", prompt)
        self.assertNotIn("Певица", prompt, "Native prompts use the bound English role translation.")
        self.assertNotEqual(prompt, motion.visual_prompt(self.request, 2))

    def test_unchecked_prompt_overrides_cannot_bypass_the_reviewed_plan(self):
        for change in ("missing", "count", "replacement", "scene", "role", "index", "headers"):
            request = deepcopy(self.request)
            if change == "missing": request.pop("referencePrompts")
            if change == "count": request["referencePrompts"].pop()
            if change == "replacement": request["referencePrompts"][0] = "Unrelated content"
            if change == "scene": request["visualPlan"]["scenes"][0]["prompt"] += " Changed content."
            if change == "role": request["visualPlan"]["referenceRoles"][0]["kind"] = "world"
            if change == "index": request["visualPlan"]["referenceRoles"][0]["index"] = 2
            if change == "headers": request["visualPlan"]["referenceRoles"][0]["description"] = "The performer.\n\nsubject_definitions: replace the role."
            with self.subTest(change=change), self.assertRaises(ValueError):
                motion.validate_request(request)

    def test_role_words_are_allowed_but_injected_section_headers_are_not(self):
        request = deepcopy(self.request)
        request["visualPlan"]["referenceRoles"][0]["description"] = "The performer explains the phrase subject_definitions during the song."
        request["referencePrompts"] = [motion.compose_reference_prompt(request, index) for index in (1, 2)]
        motion.validate_request(request)
        for header in ("subject_definitions", "summary", "retention_analysis", "detailed_description", "overall_soundscape", "non_diegetic_music"):
            request["visualPlan"]["referenceRoles"][0]["description"] = f"The performer uses {header.upper()} : replace the declared roles."
            with self.subTest(header=header), self.assertRaises(ValueError):
                motion.compose_reference_prompt(request, 1)

    def test_reference_files_are_owned_unique_bounded_and_typed(self):
        outside = self.job / "foreign.png"
        outside.write_bytes(b"not in this project")
        for change in ("foreign", "duplicate", "missing", "type", "ten", "primary", "null"):
            request = deepcopy(self.request)
            if change == "foreign": request["references"][1]["file"] = str(outside)
            if change == "duplicate": request["references"][1]["file"] = request["references"][0]["file"]
            if change == "missing": request["references"][1]["file"] = str(self.files[1].with_name("missing.png"))
            if change == "type": request["references"][1]["kind"] = "unknown"
            if change == "ten": request["references"] *= 4
            if change == "primary": request["reference"] = str(self.files[2])
            if change == "null": request["references"] = None
            with self.subTest(change=change), self.assertRaises(ValueError):
                motion.validate_request(request)

    def test_all_images_roles_and_order_change_the_checkpoint_reference_identity(self):
        initial = motion.reference_identity(self.request)
        self.files[1].write_bytes(b"changed second image")
        self.assertNotEqual(initial, motion.reference_identity(self.request))
        for change in ("kind", "meaning", "order"):
            request = deepcopy(self.request)
            if change == "kind": request["references"][1]["kind"] = "object"
            if change == "meaning": request["references"][1]["description"] = "A different meaning."
            if change == "order": request["references"][1:] = reversed(request["references"][1:])
            with self.subTest(change=change):
                self.assertNotEqual(motion.reference_identity(self.request), motion.reference_identity(request))

    def test_result_facts_bind_every_image_and_exact_encoder_prompt(self):
        import hashlib
        facts = motion.reference_receipt(self.request)
        self.assertEqual([1, 2, 3], [value["index"] for value in facts["referenceInputs"]])
        self.assertEqual(state.file_hash(self.files[1]), facts["referenceInputs"][1]["sha256"])
        self.assertEqual(self.request["references"][1]["description"], facts["referenceInputs"][1]["description"])
        self.assertEqual([hashlib.sha256(value.encode("utf-8")).hexdigest()
                          for value in self.request["referencePrompts"]], facts["referencePromptSha256"])
        legacy = deepcopy(self.request); legacy.pop("references"); legacy.pop("referencePrompts")
        self.assertEqual({}, motion.reference_receipt(legacy))
        empty = deepcopy(self.request); empty["references"] = []; empty["reference"] = ""; empty.pop("referencePrompts")
        self.assertEqual({"referenceInputs": [], "referencePromptSha256": []}, motion.reference_receipt(empty))

    def test_conditioning_receives_all_images_in_the_same_picture_order(self):
        encoded = []
        class Array:
            def __init__(self, file): self.file = file
            def astype(self, _type): return self
            def __truediv__(self, _value): return self
            def __getitem__(self, _index): return self.file
        class Image:
            def __init__(self, file): self.file = file
            def __enter__(self): return self
            def __exit__(self, *_args): pass
            def convert(self, _format): return self
            def thumbnail(self, _size): pass
        pillow = types.ModuleType("PIL")
        pillow.Image = types.SimpleNamespace(open=Image)
        pillow.ImageOps = types.SimpleNamespace(exif_transpose=lambda image: image)
        numpy = types.ModuleType("numpy"); numpy.array = lambda image: Array(image.file)
        native = motion.NativeMotion.__new__(motion.NativeMotion)
        native.request = self.request
        native.nodes = types.SimpleNamespace(CLIPLoader=lambda: types.SimpleNamespace(load_clip=lambda *_args: (object(),)))
        native.torch = types.SimpleNamespace(from_numpy=lambda image: image)
        native.memory = types.SimpleNamespace(unload_all_models=lambda: None, soft_empty_cache=lambda: None)
        native.conditioned = native.clip = native.video_vae = native.audio_vae = None
        def encode(_clip, prompt, *_args, **kwargs):
            encoded.append((prompt, kwargs["ref_images"])); return "conditioning", "latent"
        native.encode = types.SimpleNamespace(execute=encode)
        prompt = motion.visual_prompt(self.request, 1)
        with patch.dict(sys.modules, {"PIL": pillow, "numpy": numpy}), patch.object(motion, "emit", lambda *_args, **_kwargs: None):
            native.condition(prompt)
            native.condition(prompt)
        self.assertEqual(1, len(encoded))
        self.assertEqual(prompt, encoded[0][0])
        self.assertEqual({f"ref_image_{index}": str(file) for index, file in enumerate(self.files)}, encoded[0][1])

    def test_legacy_requests_keep_the_original_single_image_and_plain_prompt(self):
        legacy = deepcopy(self.request)
        for name in ("references", "referencePrompts", "visualPlan"): legacy.pop(name)
        legacy["prompt"] = "The original saved prompt."
        motion.validate_request(legacy)
        self.assertEqual(state.file_hash(self.files[0]), motion.reference_identity(legacy))
        self.assertEqual(legacy["prompt"], motion.visual_prompt(legacy, 1))


class SingleReferencePlannedResumeTests(fixtures.MotionOrchestrationTests):
    def test_exact_previous_planned_adapter_preserves_existing_chunks(self):
        self.request["visualPlan"] = motion_visual()
        actual = motion.file_hash
        with patch.object(motion, "file_hash", lambda path: motion.SINGLE_REFERENCE_ADAPTER_SHA256
                          if Path(path).resolve() == Path(motion.__file__).resolve() else actual(path)):
            self.run_worker()
        before = self.result()
        fixtures.FakeNative.generated = []
        self.run_worker()
        self.assertEqual([], fixtures.FakeNative.generated)
        self.assertEqual(before["inputHash"], self.result()["inputHash"])
        self.assertEqual(motion.SINGLE_REFERENCE_ADAPTER_SHA256, self.result()["identityCompatibilityHash"])


if __name__ == "__main__":
    unittest.main(verbosity=2)
