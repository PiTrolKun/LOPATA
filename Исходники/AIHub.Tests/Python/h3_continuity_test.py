"""Planning and worker regressions without model weights or user artifacts."""
import importlib.util
from pathlib import Path
import unittest
from unittest.mock import patch
from types import SimpleNamespace

TOOLS = Path(__file__).parents[2] / "AIHub" / "Tools"
spec = importlib.util.spec_from_file_location("h3_assemble", TOOLS / "h3_assemble.py")
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
native_spec = importlib.util.spec_from_file_location("h3_native", TOOLS / "h3_native.py")
native = importlib.util.module_from_spec(native_spec)
native_spec.loader.exec_module(native)


class ContinuityPlan(unittest.TestCase):
    def joint_stub(self, kind, starts):
        headers = [{"self_id": "parent"},
                   {"self_id": "child", "pins": [{"source_kind": kind,
                    "source_id": "parent", "place": "before"}]}]
        return SimpleNamespace(
            sequence_items=lambda text: [("clip", line) for line in text.splitlines()],
            clip_headers=lambda clips: headers,
            clip_of_line=lambda line: line.split(" @ ")[0],
            shown_cuts=lambda *args: [(0, 96), (0, 120)],
            derive_flags=lambda items: [True] * len(items),
            split_segments=lambda *args: [{"clips": [0, 1]}],
            raw_starts=lambda lines, headers, cuts: starts[:len(lines)],
            fr=SimpleNamespace(steps_for_frames=lambda n: 0 if n in (0, 56) else None),
            mctx=SimpleNamespace(parse_pins=lambda h: h.get("pins", [])))

    def test_off_grid_pixel_join_keeps_exact_cuts_in_separate_refine_segments(self):
        sequence = ["parent.mp4 @ 0..96", "child.mp4 @ 0..120"]
        groups = module.refinement_groups(sequence, self.joint_stub("clip_pixels", [0, 57]))
        self.assertEqual(groups, [[sequence[0]], [sequence[1]]])

    def test_on_grid_pixel_join_keeps_child_rows_separate(self):
        sequence = ["parent.mp4 @ 0..96", "child.mp4 @ 0..120"]
        self.assertEqual(module.refinement_groups(sequence, self.joint_stub("clip_pixels", [0, 56])),
                         [[sequence[0]], [sequence[1]]])

    def test_native_latent_join_remains_joint(self):
        sequence = ["parent.mp4 @ 0..96", "child.mp4 @ 0..120"]
        self.assertEqual(module.refinement_groups(sequence, self.joint_stub("clip", [0, 56])), [sequence])

    def test_invalid_latent_sidecar_is_left_to_native_guard(self):
        sequence = ["parent.mp4 @ 0..96", "child.mp4 @ 0..120"]
        self.assertEqual(module.refinement_groups(sequence, self.joint_stub("clip", [0, 57])), [sequence])

    def test_context_uses_planned_endpoint_not_raw_tail(self):
        take = {"Video": "takes/first.mp4", "ItemId": "a"}
        with patch.object(module, "resolved_takes", return_value=[({"enter": 0}, take, 243)]):
            spans = module.planned_spans([{"Id": "a", "Seconds": 4}], [take])
            self.assertEqual(spans[-1][2], 96)
            self.assertEqual(module.sequence_for([{"Id": "a", "Seconds": 4}], [take]),
                             ["takes/first.mp4 @ 0..243"])

    def test_final_tail_survives_short_storyboard_budget_without_moving_previous_join(self):
        a = {"Video": "takes/a.mp4", "ItemId": "a"}
        b = {"Video": "takes/b.mp4", "ItemId": "b"}
        items = [{"Id": "a", "Seconds": 4}, {"Id": "b", "Seconds": 3}]
        with patch.object(module, "resolved_takes", return_value=[({"enter": 0}, a, 96), ({"enter": 0}, b, 323)]):
            self.assertEqual(module.sequence_for(items, [a, b]),
                             ["takes/a.mp4 @ 0..96", "takes/b.mp4 @ 0..323"])
            self.assertEqual(module.planned_spans(items, [a, b])[-1][2], 72)

    def test_long_final_plan_preserves_just_the_last_pass_tail(self):
        takes = [{"Video": "takes/a1.mp4", "ItemId": "a"}, {"Video": "takes/a2.mp4", "ItemId": "a"}]
        with patch.object(module, "resolved_takes", return_value=[({"enter": 0}, takes[0], 238), ({"enter": 39}, takes[1], 243)]):
            self.assertEqual(module.sequence_for([{"Id": "a", "Seconds": 15}], takes),
                             ["takes/a1.mp4 @ 0..238", "takes/a2.mp4 @ 39..243"])

    def test_partial_plan_and_native_exit_are_not_extended(self):
        take = {"Video": "takes/a.mp4", "ItemId": "a"}
        with patch.object(module, "resolved_takes", return_value=[({"enter": 39}, take, 96)]):
            self.assertEqual(module.sequence_for([{"Id": "a", "Seconds": 15}], [take]),
                             ["takes/a.mp4 @ 39..96"])

    def test_no_final_tail_is_added_before_the_final_plan_exists(self):
        take = {"Video": "takes/a.mp4", "ItemId": "a"}
        items = [{"Id": "a", "Seconds": 4}, {"Id": "b", "Seconds": 3}]
        with patch.object(module, "resolved_takes", return_value=[({"enter": 0}, take, 243)]):
            self.assertEqual(module.sequence_for(items, [take]), ["takes/a.mp4 @ 0..96"])

    def test_empty_sequence_stays_empty(self):
        with patch.object(module, "resolved_takes", return_value=[]):
            self.assertEqual(module.sequence_for([], []), [])

    def test_shorter_and_longer_final_passes_use_the_native_visible_end(self):
        take = {"Video": "takes/a.mp4", "ItemId": "a"}
        for hi in (72, 73, 323):
            with self.subTest(hi=hi), patch.object(module, "resolved_takes", return_value=[({"enter": 0}, take, hi)]):
                self.assertEqual(module.sequence_for([{"Id": "a", "Seconds": 3}], [take]),
                                 ["takes/a.mp4 @ 0.." + str(hi)])

    def test_upscale_memory_guard_includes_final_tail(self):
        settings = {"Width": 1024, "Height": 1024, "UpscaleMegapixels": 1, "ClipSeconds": 15}
        # 3 seconds alone fit; the retained final model pass does not.
        with self.assertRaisesRegex(ValueError, "H3UpscaleMemory"):
            module.validate_upscale_budget(settings, [{"Seconds": 3}], 4 * 1024**3)

    def test_each_plan_keeps_its_own_duration(self):
        a = {"Video": "takes/a.mp4", "ItemId": "a"}
        b = {"Video": "takes/b.mp4", "ItemId": "b"}
        native = [({"enter": 0}, a, 96), ({"enter": 0}, b, 204)]
        with patch.object(module, "resolved_takes", return_value=native):
            spans = module.planned_spans([{"Id": "a", "Seconds": 4}, {"Id": "b", "Seconds": 5}], [a, b])
            self.assertEqual([(lo, hi) for _, lo, hi in spans], [(0, 96), (0, 120)])
            self.assertEqual(sum(hi - lo for _, lo, hi in spans), 216)

    def test_long_plan_retains_every_needed_pass(self):
        takes = [{"Video": "takes/a1.mp4", "ItemId": "a"}, {"Video": "takes/a2.mp4", "ItemId": "a"}]
        native = [({"enter": 0}, takes[0], 238), ({"enter": 0}, takes[1], 204)]
        with patch.object(module, "resolved_takes", return_value=native):
            spans = module.planned_spans([{"Id": "a", "Seconds": 15}], takes)
            self.assertEqual(sum(hi - lo for _, lo, hi in spans), 360)
            self.assertEqual(spans[-1][2], 122)

    def test_standard_output_dimensions(self):
        self.assertEqual(module.output_dimensions("16:9", 1080), (1920, 1080))
        self.assertEqual(module.output_dimensions("9:16", 1080), (1080, 1920))
        self.assertEqual(module.output_dimensions("1:1", 720), (720, 720))
        with self.assertRaises(ValueError):
            module.output_dimensions("9:16", 1234)

    def test_third_plan_lineage_does_not_subtract_second_plans_head_twice(self):
        pins = [{"spec": {"source_start": 21, "source_frames": 39}}]
        native.raw_pixel_lineage(pins, 39)
        # Native summary adds the held window, resolver subtracts parent head.
        join_raw = pins[0]["spec"]["source_start"] + pins[0]["spec"]["source_frames"]
        self.assertEqual(join_raw - 39, 60)

    def test_half_second_plan_uses_supported_five_frame_guide(self):
        self.assertEqual(native.continuity_window(39, 12, "masked"), (5, "guide"))
        self.assertEqual(native.continuity_window(90, 60, "both"), (39, "both"))
        self.assertEqual(native.continuity_window(39, 60, "masked"), (39, "masked"))
        with self.assertRaisesRegex(ValueError, "H3ContextTooShort"):
            native.continuity_window(39, 4, "masked")


if __name__ == "__main__":
    unittest.main()
