"""Lossless planning, resumable streaming, failure atomicity and output-budget guards."""
import array
import hashlib
import json
from pathlib import Path
import sys
import tempfile
import types
import unittest
import wave
sys.dont_write_bytecode = True
ROOT = Path(__file__).parents[2] / 'Исходники/AIHub/Tools/VideoNarrators'
sys.path.insert(0, str(ROOT))
from text_chunks import LIMITS, plan, units
from chunked_synthesis import wrap
from generation_guards import transformer_eos, CheckedLlama, cpp_eos


def tone(path, frames=240, rate=24000):
    with wave.open(str(path), 'wb') as target:
        target.setnchannels(1); target.setsampwidth(2); target.setframerate(rate)
        block = array.array('h', [2000] * min(65536, frames)).tobytes()
        while frames:
            count = min(frames, len(block) // 2); target.writeframesraw(block[:count * 2]); frames -= count


class Planning(unittest.TestCase):
    def test_every_model_covers_all_input_in_order_with_bounded_work(self):
        texts = ['Начало.\n\n' + ('Длинное предложение, без потери слов. ' * 200) + '\nКонец!',
                 'а' * 12001, '1234567890' * 500, ' ' * 1000 + 'Текст' + '\n' * 700,
                 'Hello?! «Quoted text.»\n\n' + 'English Русский 2026 😀 е́. ' * 100]
        for engine, maximum in LIMITS.items():
            for text in texts:
                with self.subTest(engine=engine, length=len(text)):
                    packets = list(plan(text, engine))
                    self.assertEqual(text, ''.join(p[2] for p in packets))
                    self.assertEqual(0, packets[0][0]); self.assertEqual(len(text), packets[-1][1])
                    self.assertTrue(all(a[1] == b[0] for a, b in zip(packets, packets[1:])))
                    self.assertTrue(all(0 < units(p[2]) <= maximum for p in packets))

    def test_sentence_and_paragraph_boundaries_are_preferred(self):
        text = 'Первая часть.\n\nВторая часть.'
        self.assertEqual(['Первая часть.\n\n', 'Вторая часть.'], [p[2] for p in plan(text, 'silero56')])
        text = 'Короткое начало. ' + 'а' * 400
        self.assertEqual('Короткое начало. ', next(plan(text, 'silero56'))[2])


class Assembly(unittest.TestCase):
    def run_case(self, folder, data, fail=None, progress=lambda _: None):
        calls = []
        report = types.SimpleNamespace(actual={})
        def synthesize(path):
            calls.append(data['text'])
            if fail is not None and len(calls) == fail:
                raise RuntimeError('controlled failure')
            tone(path)
        wrapper = wrap(data, synthesize, report, progress)
        wrapper(str(folder / 'speech.wav'))
        return calls, report

    def test_failure_does_not_publish_partial_audio_and_resume_reuses_verified_packets(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); text = 'Первый абзац.\n\nВторой абзац.\n\nТретий абзац.'
            data = dict(engine='silero56', text=text, model_revision='one', speaker='xenia')
            with self.assertRaisesRegex(RuntimeError, 'controlled failure'):
                self.run_case(root, data, fail=2)
            self.assertFalse((root / 'speech.wav').exists()); self.assertEqual(text, data['text'])
            progress = []
            calls, report = self.run_case(root, data, progress=progress.append)
            self.assertEqual(2, len(calls)); self.assertEqual(1, report.actual['chunking']['reused'])
            self.assertEqual(1, progress[-1]); self.assertEqual(sorted(progress), progress)
            with wave.open(str(root / 'speech.wav')) as audio: self.assertEqual(720, audio.getnframes())
            receipt = next(root.rglob('000001.wav')); receipt.write_bytes(b'corrupt')
            calls, report = self.run_case(root, data)
            self.assertEqual(1, len(calls)); self.assertEqual(2, report.actual['chunking']['reused'])

    def test_changed_text_voice_revision_or_version_cannot_reuse_old_packets(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); data = dict(engine='silero56', text='Один.\n\nДва.', speaker='xenia', generation_version='one')
            self.run_case(root, data)
            for key, value in [('speaker', 'aidar'), ('text', 'Три.\n\nЧетыре.'), ('model_revision', 'new'),
                               ('generation_version', 'two'), ('runtime_identity', 'new'), ('execution_device', 'cpu')]:
                changed = dict(data, **{key: value})
                calls, report = self.run_case(root, changed)
                self.assertEqual(2, len(calls)); self.assertEqual(0, report.actual['chunking']['reused'])

    def test_benchmark_repeats_do_not_measure_checkpoint_copying_as_gpu_speed(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); data = dict(engine='silero56', text='Один.\n\nДва.', repeats=2)
            self.run_case(root, data)
            calls, report = self.run_case(root, data)
            self.assertEqual(2, len(calls)); self.assertEqual(0, report.actual['chunking']['reused'])

    def test_hour_of_pcm_is_streamed_and_frame_count_is_exact(self):
        with tempfile.TemporaryDirectory() as d:
            root = Path(d); data = dict(engine='silero56', text=('Часть.\n\n' * 100))
            report = types.SimpleNamespace(actual={}); calls = []
            def synthesize(path):
                calls.append(data['text']); tone(path, frames=24000 * 36)
            wrap(data, synthesize, report)(str(root / 'hour.wav'))
            with wave.open(str(root / 'hour.wav')) as audio:
                self.assertEqual(3600, audio.getnframes() / audio.getframerate())
            self.assertEqual(100, len(calls)); self.assertEqual(100, report.actual['chunking']['packets'])


class OutputGuards(unittest.TestCase):
    def test_native_kitten_requires_the_pinned_termination_receipt(self):
        cpp_eos(dict(chunks=[dict(terminated=True)]))
        for report in ({}, dict(chunks=[dict(terminated=False)]), dict(chunks=[dict(terminated=True), dict(terminated=False)])):
            with self.assertRaisesRegex(RuntimeError, 'without EOS'): cpp_eos(report)

    def test_transformer_without_eos_is_an_error_not_truncated_success(self):
        model = types.SimpleNamespace(generate=lambda **_: types.SimpleNamespace(sequences=types.SimpleNamespace(tolist=lambda: [[1, 2, 3]])))
        transformer_eos(model)
        with self.assertRaisesRegex(RuntimeError, 'without EOS'): model.generate(eos_token_id=9)
        self.assertIsNotNone(model.generate(eos_token_id=[3, 9]))

    def test_llama_budget_cutoff_is_not_a_successful_stream(self):
        model = lambda **_: iter([dict(choices=[dict(text='audio', finish_reason=None)]), dict(choices=[dict(text='', finish_reason='length')])])
        with self.assertRaisesRegex(RuntimeError, 'budget exhausted'): list(CheckedLlama(model)())

    def test_stop_token_in_the_prompt_does_not_prove_complete_generated_output(self):
        model = types.SimpleNamespace(generate=lambda *_, **__: types.SimpleNamespace(tolist=lambda: [[9, 1, 2]]))
        transformer_eos(model)
        with self.assertRaisesRegex(RuntimeError, 'without EOS'):
            model.generate(types.SimpleNamespace(shape=(1, 1)), eos_token_id=9)


if __name__ == '__main__': unittest.main()
