"""Adapter fixtures only. No model weights, network calls or song inference."""
import importlib.util
from pathlib import Path
import unittest

ROOT = Path(__file__).resolve().parents[2]
spec = importlib.util.spec_from_file_location('music_ace_worker', ROOT / 'Исходники/AIHub/Tools/music_ace_worker.py')
adapter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(adapter)


class Tokenizer:
    name = 'official-tokenizer-fixture'

    def __init__(self):
        self.calls = []

    def encode(self, value, add_special_tokens=True):
        return [0] * (len(value) + int(add_special_tokens))

    def __call__(self, value, **kwargs):
        self.calls.append((value, kwargs))
        return {'input_ids': [value]}


class ConditioningBoundaryTests(unittest.TestCase):
    def setUp(self):
        self.source = Tokenizer()
        self.tokenizer = adapter.BoundedTokenizer(self.source)

    def test_preserves_exact_unicode_and_underlying_result(self):
        text = '[Verse]\nЛАПАТА роет'
        result = self.tokenizer(text, truncation=True, max_length=2048, return_tensors='pt')
        self.assertEqual({'input_ids': [text]}, result)
        self.assertEqual(text, self.source.calls[0][0])
        self.assertEqual('pt', self.source.calls[0][1]['return_tensors'])

    def test_overflow_rejected_before_underlying_truncation(self):
        with self.assertRaisesRegex(ValueError, '256 tokens'):
            self.tokenizer('x' * 256, truncation=True, max_length=256)
        self.assertEqual([], self.source.calls)

    def test_special_tokens_counted_and_exact_boundary_passes(self):
        self.tokenizer('x' * 255, truncation=True, max_length=256)
        self.tokenizer('x' * 256, truncation=True, max_length=256, add_special_tokens=False)
        self.assertEqual(2, len(self.source.calls))

    def test_batch_cannot_hide_one_long_entry(self):
        with self.assertRaisesRegex(ValueError, '2048 tokens'):
            self.tokenizer(['short', 'x' * 2048], truncation=True, max_length=2048)
        self.assertEqual([], self.source.calls)

    def test_other_tokenizer_attributes_and_nontruncated_calls_unchanged(self):
        self.assertEqual(self.source.name, self.tokenizer.name)
        self.tokenizer('x' * 4096, truncation=False, max_length=256)
        self.assertEqual(1, len(self.source.calls))


if __name__ == '__main__':
    unittest.main()
