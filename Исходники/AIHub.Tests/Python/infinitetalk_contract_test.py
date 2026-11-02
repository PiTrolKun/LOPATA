"""InfiniteTalk checkpoints/adapter contracts; no weights or inference required."""
import ast
import copy
from contextlib import nullcontext
import hashlib
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import subprocess
import struct
import tempfile
import types
import unittest
import wave
from unittest.mock import Mock, patch

TOOLS = Path(__file__).resolve().parents[2] / 'AIHub' / 'Tools'
sys.path.insert(0, str(TOOLS))
from song_clip_motion_state import atomic_json, object_hash
from song_clip_infinitetalk_state import InfiniteState, windows, LOOP_SHA256
from song_clip_infinitetalk_native import (hooked_source, WindowCheckpoint, COUNTERS,
                                          NativeInfiniteTalk, HeadlessEvents, install_headless_events)
from song_clip_infinitetalk_native import enable_wav2vec_hidden_states
import song_clip_infinitetalk as worker


def counters(index, total):
    final = index == total
    start = (index - 1 if final else index) * 72
    return dict(iteration_count=index, img_counter=81 + (index - 1) * 72,
                audio_start_idx=start, audio_end_idx=start + 81,
                current_condframe_index=index, step_iteration_count=40 * index,
                arrive_last_frame=final or index == total - 1,
                cur_motion_frames_num=1 if final and index == 1 else 9,
                is_first_clip=final and index == 1)


def commit(state):
    index = len(state.chain) + 1
    count = 81 + (index - 1) * 72
    for number in range(state.frame_count, count):
        (state.frames / f'frame_{number:05d}.png').write_bytes(b'PNG-fixture:' + str(number).encode())
    return state.commit(counters(index, len(state.plan)), index == len(state.plan),
                        lambda path: path.write_bytes(b'bounded-tensor-fixture'))


class CheckpointTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)

    def tearDown(self):
        self.temp.cleanup()

    def state(self, frames=225, identity=None):
        return InfiniteState(self.root, identity or {'input': 'song+photo', 'adapter': 'reviewed'}, 42, frames)

    def test_native_windows_cover_song_with_single_overlap(self):
        for frames, expected in [(1, 1), (81, 1), (82, 2), (153, 2), (154, 3), (225, 3)]:
            plan = windows(frames)
            self.assertEqual(expected, len(plan))
            self.assertEqual(81 + (expected - 1) * 72, sum(p['rawFrames'] - p['contextFrames'] for p in plan))
            self.assertGreaterEqual(81 + (expected - 1) * 72, frames)
            self.assertLess(81 + (expected - 1) * 72 - frames, 81)
            self.assertEqual([i * 72 for i in range(expected)], [p['startFrame'] for p in plan])

    def test_resume_preserves_prefix_and_accepts_only_receipt_commits(self):
        state = self.state()
        commit(state)
        accepted = (state.frames / 'frame_00000.png').stat().st_mtime_ns
        (state.frames / 'frame_00081.png').write_bytes(b'interrupted-window')
        (state.frames / 'frame_00081.png.0011223344556677.tmp').write_bytes(b'interrupted-metadata')
        (state.receipts / '000002.safetensors').write_bytes(b'orphan')
        resumed = self.state()
        self.assertEqual(1, len(resumed.chain))
        resumed.quarantine_uncommitted()
        self.assertFalse((resumed.frames / 'frame_00081.png').exists())
        self.assertFalse((resumed.frames / 'frame_00081.png.0011223344556677.tmp').exists())
        self.assertEqual(accepted, (resumed.frames / 'frame_00000.png').stat().st_mtime_ns)
        self.assertEqual(1, len(list(self.root.glob('infinite-uncommitted/*/frame_00081.png'))))
        commit(resumed)
        commit(resumed)
        self.assertTrue(self.state().done)
        self.assertEqual(accepted, (resumed.frames / 'frame_00000.png').stat().st_mtime_ns)

    def test_only_own_uncommitted_metadata_temps_are_quarantined(self):
        state = self.state()
        commit(state)
        accepted = (state.frames / 'frame_00000.png').read_bytes()
        for name in ('frame_00080.png.0011223344556677.tmp',
                     'frame_00081.png.001122334455667.tmp',
                     'frame_00081.png.001122334455667g.tmp', 'unrelated.tmp'):
            path = state.frames / name
            path.write_bytes(b'not-an-owned-uncommitted-temp')
            with self.assertRaisesRegex(ValueError, 'Unexpected'):
                self.state().quarantine_uncommitted()
            self.assertTrue(path.exists())
            self.assertEqual(accepted, (state.frames / 'frame_00000.png').read_bytes())
            path.unlink()

    def test_changed_song_reference_or_adapter_cannot_resume(self):
        commit(self.state())
        for changed in ({'input': 'different-song'}, {'input': 'different-photo'}, {'adapter': 'different-source'}):
            with self.assertRaisesRegex(ValueError, 'inputs|binding'):
                self.state(identity=changed)

    def test_committed_frame_and_tensor_corruption_are_detected(self):
        state = self.state()
        commit(state)
        frame = state.frames / 'frame_00080.png'
        original = frame.read_bytes()
        frame.write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'frame'):
            self.state()
        frame.write_bytes(original)
        (state.receipts / '000001.safetensors').write_bytes(b'changed')
        with self.assertRaisesRegex(ValueError, 'tensor'):
            self.state()

    def test_counter_tampering_and_receipt_gaps_are_detected(self):
        state = self.state()
        commit(state)
        path = state.receipts / '000001.json'
        receipt = json.loads(path.read_text())
        receipt['counters']['audio_start_idx'] = 0
        atomic_json(path, receipt)
        with self.assertRaisesRegex(ValueError, 'counter'):
            self.state()
        path.rename(state.receipts / '000002.json')
        with self.assertRaisesRegex(ValueError, 'gap'):
            self.state()

    def test_failed_tensor_write_does_not_create_a_receipt(self):
        state = self.state()
        def interrupted(path):
            path.write_bytes(b'partial')
            raise OSError('power loss')
        with self.assertRaises(OSError):
            state.commit(counters(1, 3), False, interrupted)
        self.assertEqual([], self.state().chain)
        self.assertEqual([], list(state.receipts.glob('*.tmp')))

    def test_seed_is_stable_after_restart(self):
        state = InfiniteState(self.root, {'source': 'same'}, -1, 82)
        self.assertEqual(state.seed, InfiniteState(self.root, {'source': 'same'}, -1, 82).seed)
        with self.assertRaisesRegex(ValueError, 'seed'):
            InfiniteState(self.root, {'source': 'same'}, state.seed + 1, 82)


class WorkerTests(unittest.TestCase):
    def test_native_run_preserves_conditioning_and_official_no_lora_sampling_contract(self):
        # Exercise orchestration with inert author nodes: no models or GPU are loaded.
        native = NativeInfiniteTalk.__new__(NativeInfiniteTalk)
        native.request = dict(vision='vision', vae='vae', adapter='talk-Q4.gguf',
            model='wan-480p-Q4.gguf', width=480, height=832, steps=40, vramReserveGb=2)
        native.state = types.SimpleNamespace(seed=73, frames=Path('committed-frames'), done=False)
        native.device = types.SimpleNamespace(type='cuda')
        native.emit = Mock()
        native.torch = types.SimpleNamespace(manual_seed=Mock(), inference_mode=nullcontext)
        native.memory = Mock()
        native.memory.should_use_bf16.return_value = True
        native.memory.get_total_memory.return_value = 24 * 1024**3
        native.memory.get_free_memory.return_value = 18 * 1024**3
        native.nodes, native.wan_nodes = Mock(), Mock()
        native.loading, native.talk, native.sampling = Mock(), Mock(), Mock()
        reference, audio, text, vision_context = object(), object(), object(), object()
        image, model, adapter, swap = {}, object(), object(), object()
        native.audio, native.text, native.image = Mock(return_value=audio), Mock(return_value=text), Mock(return_value=reference)
        native.retire = Mock()
        native.nodes.CLIPVisionLoader.return_value.load_clip.return_value = (object(),)
        native.loading.WanVideoVAELoader.return_value.loadmodel.return_value = (object(),)
        native.wan_nodes.WanVideoClipVisionEncode.return_value.process.return_value = (vision_context,)
        native.talk.WanVideoImageToVideoMultiTalk.return_value.process.return_value = (image,)
        native.talk.MultiTalkModelLoader.return_value.loadmodel.return_value = (adapter,)
        native.loading.WanVideoBlockSwap.return_value.setargs.return_value = (swap,)
        native.loading.WanVideoModelLoader.return_value.loadmodel.return_value = (model,)
        sampler = native.sampling.WanVideoSampler.return_value.process
        sampler.side_effect = lambda **kwargs: setattr(native.state, 'done', True)
        native.run(['performer sings'])
        native.torch.manual_seed.assert_called_once_with(73)
        native.text.assert_called_once_with(['performer sings'])
        conditioning = native.talk.WanVideoImageToVideoMultiTalk.return_value.process.call_args
        self.assertEqual((480, 832, 81, 9, True, 'disabled'), conditioning.args[1:])
        self.assertIs(reference, conditioning.kwargs['start_image'])
        self.assertIs(vision_context, conditioning.kwargs['clip_embeds'])
        self.assertEqual('infinitetalk', conditioning.kwargs['mode'])
        self.assertEqual(str(native.state.frames), image['output_path'])
        native.loading.WanVideoBlockSwap.return_value.setargs.assert_called_once_with(
            blocks_to_swap=20, offload_img_emb=False, offload_txt_emb=False,
            use_non_blocking=False, vace_blocks_to_swap=0, prefetch_blocks=0, block_swap_debug=False)
        native.loading.WanVideoModelLoader.return_value.loadmodel.assert_called_once_with(
            'wan-480p-Q4.gguf', 'bf16', 'offload_device', 'disabled',
            attention_mode='sdpa', block_swap_args=swap, multitalk_model=adapter)
        sampler.assert_called_once_with(model=model, image_embeds=image, shift=7.0, steps=40,
            cfg=5.0, seed=73, scheduler='unipc', riflex_freq_index=0, text_embeds=text,
            force_offload=True, multitalk_embeds=audio)

    def test_new_wav2vec_api_uses_hf_capture_only_on_the_selected_model(self):
        class NewEncoder:
            def forward(self, hidden_states, **kwargs):
                return hidden_states
        class Model:
            encoder = NewEncoder()
            def forward(self, values, seq_len, output_hidden_states=False):
                return (values, seq_len, output_hidden_states)
        calls = []
        def capture_outputs(**options):
            calls.append(options)
            def decorate(forward):
                def wrapped(self, *args, **kwargs):
                    return ('captured', forward(self, *args, **kwargs))
                return wrapped
            return decorate
        module = types.SimpleNamespace(capture_outputs=capture_outputs)
        first, other = Model(), Model()
        with patch.dict(sys.modules, {'transformers.utils.output_capturing': module}):
            self.assertTrue(enable_wav2vec_hidden_states(first))
            self.assertTrue(enable_wav2vec_hidden_states(first))
            self.assertEqual(('captured', ('audio', 13, True)), first.forward('audio', 13, output_hidden_states=True))
            self.assertEqual(('audio', 13, True), other.forward('audio', 13, output_hidden_states=True))
            self.assertEqual([{'tie_last_hidden_states': False}], calls)
            first.encoder = types.SimpleNamespace(forward=lambda audio, output_hidden_states=False: audio)
            self.assertFalse(enable_wav2vec_hidden_states(first))

    def test_headless_preview_bridge_has_no_server_and_forwards_only_safe_status(self):
        events = []
        emit = lambda *args, **kwargs: events.append((args, kwargs))
        preview_method = types.SimpleNamespace(NoPreviews='disabled')
        cli = types.SimpleNamespace(args=types.SimpleNamespace(preview_method='auto'), LatentPreviewMethod=preview_method)
        server = types.SimpleNamespace(PromptServer=type('PromptServer', (), {}))
        with patch.dict(sys.modules, {'server': server, 'comfy.cli_args': cli}):
            bridge = install_headless_events(emit)
            self.assertIs(bridge, server.PromptServer.instance)
            self.assertEqual('disabled', cli.args.preview_method)
            bridge.send_sync(1, b'not-an-encoded-preview')
            self.assertEqual([], events)
            bridge.send_progress_text('native status', 'node1')
            bridge.send_sync('progress', {'value': 3, 'max': 40, 'not_json': object()})
            self.assertEqual('native status', events[0][1]['message'])
            self.assertEqual({'value': 3, 'max': 40}, events[1][1]['data'])
            self.assertFalse(hasattr(bridge, 'app'))
            self.assertFalse(hasattr(bridge, 'loop'))
            server.PromptServer.instance = object()
            with self.assertRaisesRegex(ValueError, 'live ComfyUI server'):
                install_headless_events(emit)

    def test_slots_cover_fractional_song_tail_on_native_25fps_grid(self):
        slots = worker.analysis_slots(20.019)
        self.assertEqual(7, len(slots))
        self.assertEqual(0, slots[0]['startSeconds'])
        self.assertEqual(72 / 25, slots[1]['startSeconds'])
        self.assertEqual(20.019, slots[-1]['endSeconds'])
        self.assertEqual(9, slots[-1]['contextFrames'])

    def test_photo_identity_is_conditioned_separately_from_prompt_syntax(self):
        analysis = dict(slots=worker.analysis_slots(3))
        request = dict(visualPlan=dict(globalStyle='natural light', scenes=[dict(index=1, startSeconds=0,
            endSeconds=3, prompt='<Subject 1> sings | subtle camera motion')]))
        prompt = worker.scene_prompts(request, analysis)[0]
        self.assertNotIn('<Subject', prompt)
        self.assertNotIn('|', prompt)
        self.assertIn('same performer', prompt)
        request['visualPlan']['scenes'][0]['endSeconds'] = 2
        with self.assertRaisesRegex(ValueError, 'timing'):
            worker.scene_prompts(request, analysis)

    def test_mux_uses_original_song_and_only_removes_native_padding(self):
        with tempfile.TemporaryDirectory() as directory:
            state = InfiniteState(directory, {'test': 'mux'}, 4, 251)
            request = dict(ffmpeg='ffmpeg', song='whole-original-song.wav', outputWidth=1280,
                           outputHeight=720, quality=85)
            called = []
            def invoke(args, **kwargs):
                called.append(args)
                Path(args[-1]).write_bytes(b'encoded-mp4')
            with patch.object(worker.subprocess, 'run', side_effect=invoke), patch.object(worker, 'verify_output'), \
                    patch.object(worker, 'encode_video', return_value=Path(directory) / 'native-to-24fps.mp4') as encoding:
                result = worker.mux(request, state, 10.019)
            args = called[0]
            self.assertEqual(10.019, encoding.call_args.args[2])
            self.assertEqual(request['song'], args[args.index('-i', args.index('-i') + 1) + 1])
            self.assertEqual('1:a:0', args[args.index('-map', args.index('-map') + 1) + 1])
            self.assertAlmostEqual(10.019, float(args[args.index('-t') + 1]))
            self.assertEqual('copy', args[args.index('-c:v') + 1])
            self.assertTrue(result.exists())

    def test_bridge_uses_supported_scaled_t5_comfy_loader(self):
        # This protects the known format incompatibility from being reintroduced.
        text = ast.unparse(ast.parse((TOOLS / 'song_clip_infinitetalk_native.py').read_text()))
        self.assertIn("load_clip(self.request['encoder'], 'wan', 'default')", text)
        self.assertIn('WanVideoTextEmbedBridge', text)
        self.assertNotIn('LoadWanVideoT5TextEncoder', text)

    def test_installed_loop_change_is_rejected_before_execution(self):
        with self.assertRaisesRegex(ValueError, 'source revision'):
            hooked_source(b'def multitalk_loop(self, **kwargs):\n    return None\n')

    def test_exact_author_loop_hook_preserves_every_diffusion_statement(self):
        upstream = TOOLS.parents[2] / '_tmp/songclip-infinitetalk-0102/upstream' / (
            'ComfyUI-WanVideoWrapper-' + worker.SOURCE_REVISION) / 'multitalk/multitalk_loop.py'
        if not upstream.exists():
            self.skipTest('Pinned research source not available on this checkout.')
        before = upstream.read_bytes()
        after = hooked_source(before)
        self.assertEqual(LOOP_SHA256, hashlib.sha256(before).hexdigest())
        original = next(n for n in ast.parse(before.decode()).body if isinstance(n, ast.FunctionDef))
        derived = ast.parse(after).body[0]
        # Strip only the explicit checkpoint calls and restore assignment block.
        class StripHook(ast.NodeTransformer):
            def visit_Expr(self, node):
                if isinstance(node.value, ast.Call) and isinstance(node.value.func, ast.Attribute) and isinstance(node.value.func.value, ast.Name) and node.value.func.value.id == '_lopata_checkpoint':
                    return None
                return self.generic_visit(node)
            def visit_Assign(self, node):
                if any(isinstance(t, ast.Name) and t.id == 'restored' for t in node.targets):
                    return None
                return self.generic_visit(node)
            def visit_If(self, node):
                if isinstance(node.test, ast.Compare) and isinstance(node.test.left, ast.Name) and node.test.left.id == 'restored':
                    return None
                return self.generic_visit(node)
        cleaned = StripHook().visit(copy.deepcopy(derived))
        self.assertEqual(ast.dump(original, include_attributes=False), ast.dump(cleaned, include_attributes=False))
        self.assertEqual(before, upstream.read_bytes())


@unittest.skipUnless(importlib.util.find_spec('torch') and importlib.util.find_spec('safetensors')
                     and importlib.util.find_spec('PIL'), 'Managed CPU Torch/safetensors/Pillow required.')
class CpuTensorTests(unittest.TestCase):
    @unittest.skipUnless(os.environ.get('LOPATA_TEST_FFMPEG') and os.environ.get('LOPATA_TEST_FFPROBE'),
                         'Set explicit managed FFmpeg/FFprobe paths for media smoke.')
    def test_real_mux_keeps_fractional_song_tail_and_embedded_provenance(self):
        from PIL import Image
        with tempfile.TemporaryDirectory() as directory:
            seconds = 6.019
            state = InfiniteState(directory, {'test': 'media'}, 11, 151)
            for number in range(153):
                Image.new('RGB', (8, 8), (number % 256, 30, 90)).save(state.frames / f'frame_{number:05d}.png')
            # Real receipt commits, without inference, bind this media fixture.
            state.commit(counters(1, 2), False, lambda path: path.write_bytes(b'fixture-1'))
            state.commit(counters(2, 2), True, lambda path: path.write_bytes(b'fixture-2'))
            song = Path(directory) / 'original-song.wav'
            ffmpeg, ffprobe = os.environ['LOPATA_TEST_FFMPEG'], os.environ['LOPATA_TEST_FFPROBE']
            with wave.open(str(song), 'wb') as stream:
                stream.setparams((1, 2, 48000, 0, 'NONE', 'not compressed'))
                stream.writeframes(b''.join(struct.pack('<h', round(6000 * math.sin(2 * math.pi * 440 * i / 48000)))
                                            for i in range(round(seconds * 48000))))
            request = dict(ffmpeg=ffmpeg, ffprobe=ffprobe, song=str(song), outputWidth=320,
                           outputHeight=256, quality=85, programVersion='cpu-media-smoke', operationId='test-op',
                           pins={'model': 'fixture-no-inference'})
            original = hashlib.sha256(song.read_bytes()).hexdigest()
            output = worker.mux(request, state, seconds)
            facts = worker.verify_output(request, output, seconds)
            self.assertEqual(original, hashlib.sha256(song.read_bytes()).hexdigest())
            tags = {key.upper(): value for key, value in facts['format']['tags'].items()}
            metadata = json.loads(tags['LOPATA_VIDEO'])
            self.assertEqual('LOPATA', metadata['program'])
            self.assertEqual('test-op', metadata['operation'])
            self.assertNotIn(directory, tags['LOPATA_VIDEO'])
            self.assertGreater(float(next(s for s in facts['streams'] if s['codec_type'] == 'audio')['duration']), 6)

    def test_boundary_roundtrip_restores_rng_motion_audio_and_png_provenance(self):
        import torch
        from PIL import Image
        with tempfile.TemporaryDirectory() as directory:
            state = InfiniteState(directory, {'test': 'native-boundary'}, 73, 225)
            checkpoint = WindowCheckpoint(state, torch, lambda *args, **kwargs: None, torch.device('cpu'),
                dict(program='LOPATA', programVersion='test', scenario='song-to-clip/infinitetalk',
                     operation='test-op', models={'Wan': 'pinned-revision'}))
            seed_rng = torch.Generator(device='cpu').manual_seed(17)
            torch.manual_seed(29)
            torch.randn(13, generator=seed_rng)
            torch.randn(7)
            cond = torch.arange(1 * 3 * 9 * 8 * 8, dtype=torch.float32).reshape(1, 3, 9, 8, 8) / 1000
            audio = torch.arange(225 * 2 * 3, dtype=torch.float32).reshape(225, 2, 3)
            for number in range(81):
                Image.new('RGB', (8, 8), (number, 31, 97)).save(state.frames / f'frame_{number:05d}.png')
            checkpoint.commit(dict(counters(1, 3), seed_g=seed_rng, cond_frame=cond,
                                   audio_embedding=[audio]), final=False)
            expected_seed, expected_global = torch.randn(9, generator=seed_rng), torch.randn(11)
            replay_rng = torch.Generator(device='cpu').manual_seed(999)
            torch.manual_seed(999)
            resumed = InfiniteState(directory, {'test': 'native-boundary'}, 73, 225)
            restore = WindowCheckpoint(resumed, torch, lambda *args, **kwargs: None, torch.device('cpu'), checkpoint.provenance)
            restored = restore.restore(replay_rng, [audio], torch.zeros(1, 3, 1, 8, 8), torch.device('cpu'), torch.bfloat16)
            self.assertTrue(torch.equal(expected_seed, torch.randn(9, generator=replay_rng)))
            self.assertTrue(torch.equal(expected_global, torch.randn(11)))
            self.assertTrue(torch.equal(cond, restored['cond_frame']))
            self.assertEqual(torch.float32, restored['cond_frame'].dtype)
            self.assertEqual('cpu', restored['cond_frame'].device.type)
            self.assertEqual(72, restored['audio_start_idx'])
            self.assertEqual((1, 3, 2, 8, 8), tuple(restored['original_images'].shape))
            self.assertTrue(torch.equal(audio, restored['audio_embedding'][0]))
            with Image.open(state.frames / 'frame_00080.png') as image:
                self.assertEqual((80, 31, 97), image.getpixel((0, 0)))
                metadata = json.loads(image.info['LOPATA_VIDEO'])
                self.assertEqual('LOPATA', metadata['program'])
                self.assertEqual('test-op', metadata['operation'])
                self.assertNotIn(directory, image.info['LOPATA_VIDEO'])
            # Commit the penultimate native window, including its next-window
            # audio tail padding, then reconstruct that exact boundary too.
            for number in range(81, 153):
                Image.new('RGB', (8, 8), (number, 31, 97)).save(state.frames / f'frame_{number:05d}.png')
            padded = torch.cat([audio, audio[-3:].flip(0)], dim=0)
            restore.commit(dict(counters(2, 3), seed_g=replay_rng, cond_frame=cond + 1,
                                audio_embedding=[padded]), final=False)
            final_state = InfiniteState(directory, {'test': 'native-boundary'}, 73, 225)
            last_restore = WindowCheckpoint(final_state, torch, lambda *args, **kwargs: None, torch.device('cpu'), checkpoint.provenance)
            last = last_restore.restore(replay_rng, [audio], torch.zeros(1, 3, 1, 8, 8), torch.device('cpu'), torch.float32)
            self.assertTrue(last['arrive_last_frame'])
            self.assertEqual(144, last['audio_start_idx'])
            self.assertTrue(torch.equal(padded, last['audio_embedding'][0]))
            self.assertTrue(torch.equal(cond + 1, last['cond_frame']))


if __name__ == '__main__':
    unittest.main()
