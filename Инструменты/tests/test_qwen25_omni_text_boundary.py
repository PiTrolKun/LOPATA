"""Offline regression tests: no Omni weights, downloads, CUDA, or app launch.

Run with the existing Heavy runtime's Python and unittest discovery.
The tiny randomly initialized CPU model exercises Transformers.generate;
scripted logits reproduce an answer, EOS, then an unwanted new dialogue.
"""

import importlib.util
import sys
import unittest
from pathlib import Path
from types import SimpleNamespace
from unittest.mock import patch

import torch
from transformers import GenerationConfig, GPT2Config, GPT2LMHeadModel, LogitsProcessor


WORKER_PATH = Path(__file__).resolve().parents[2] / "Исходники/AIHub/Tools/qwen25_omni_worker.py"
spec = importlib.util.spec_from_file_location("omni_worker", WORKER_PATH)
worker_module = importlib.util.module_from_spec(spec)
# Worker owns stdout only in its isolated process, not in the test runner.
original_stdout = sys.stdout
try:
    spec.loader.exec_module(worker_module)
finally:
    sys.stdout = original_stdout


class ScriptedAnswer(LogitsProcessor):
    def __call__(self, input_ids, scores):
        # 2 = answer; 3 = end of this assistant turn; 4 = fabricated dialogue.
        token = (2, 3, 4)[min(input_ids.shape[1] - 1, 2)]
        scores.fill_(-float("inf"))
        scores[:, token] = 0
        return scores


class TinyTokenizer:
    eos_token_id = 3
    pad_token_id = 0

    def decode(self, ids, **kwargs):
        return "".join({2: "Answer. ", 4: "Human: invented. "}.get(int(i), "") for i in ids)


class TinyInputs(dict):
    def to(self, *args):
        return self


class TinyProcessor:
    tokenizer = TinyTokenizer()

    def apply_chat_template(self, conversation, **kwargs):
        return "assistant"

    def __call__(self, **kwargs):
        return TinyInputs(input_ids=torch.tensor([[1]]), attention_mask=torch.ones((1, 1), dtype=torch.long))

    def batch_decode(self, ids, **kwargs):
        return [self.tokenizer.decode(row, **kwargs) for row in ids]


class TinyModelAdapter:
    def __init__(self, model):
        self.model = model
        self.config = SimpleNamespace(eos_token_id=3, max_position_embeddings=16)
        self.generation_config = model.generation_config
        self.device = torch.device("cpu")
        self.dtype = torch.float32

    def generate(self, **kwargs):
        kwargs.pop("use_audio_in_video")
        return self.model.generate(**kwargs, logits_processor=[ScriptedAnswer()])


class TextBoundaryTests(unittest.TestCase):
    @classmethod
    def setUpClass(cls):
        torch.set_num_threads(1)
        cls.tiny_model = GPT2LMHeadModel(GPT2Config(
            vocab_size=8, n_positions=32, n_embd=8, n_layer=1, n_head=1,
            bos_token_id=None, eos_token_id=None, pad_token_id=None,
        )).eval()
        # The real Omni checkpoint's file has only these two metadata fields.
        cls.tiny_model.generation_config = GenerationConfig.from_dict({
            "_from_model_config": True, "transformers_version": "4.50.0.dev0",
        })

    def make_worker(self):
        worker = worker_module.OmniWorker()
        worker.model = TinyModelAdapter(self.tiny_model)
        worker.processor = TinyProcessor()
        worker.process_mm_info = lambda *a, **k: (None, None, None)
        worker._ensure_profile = lambda profile: 0
        worker.runtime_profile = "thinker"
        return worker

    def test_old_configuration_generates_past_eos(self):
        result = self.tiny_model.generate(
            input_ids=torch.tensor([[1]]), attention_mask=torch.ones((1, 1), dtype=torch.long),
            max_new_tokens=4, logits_processor=[ScriptedAnswer()],
        )
        self.assertEqual([2, 3, 4, 4], result[0, 1:].tolist())

    def test_worker_stops_generation_and_stream_at_first_eos(self):
        worker = self.make_worker()
        # Admission now reserves 4096 tokens. Keep the tiny scripted EOS model,
        # but supply the production-size admission window for this EOS test.
        worker._max_context_tokens = lambda: 32768
        messages = [{"role": "user", "content": "Describe the image", "includesImage": True}]
        with patch.object(worker_module, "stream") as streamed:
            result = worker.generate_text(1, str(WORKER_PATH), messages)
        self.assertEqual("Answer.", result["content"])
        self.assertEqual(2, result["generatedTokens"])
        self.assertEqual("eos", result["finishReason"])
        self.assertEqual([3], result["eosTokenIds"])
        self.assertEqual(3, result["lastTokenId"])
        self.assertNotIn("Human", "".join(call.args[1] for call in streamed.call_args_list))
        self.assertIsNone(self.tiny_model.generation_config.eos_token_id)

    def test_tokenizer_supplies_eos_when_model_configs_are_empty(self):
        worker = self.make_worker()
        worker.model.config = SimpleNamespace()
        self.assertEqual([3], worker._text_eos_token_ids())

    def test_nested_thinker_and_multiple_eos_are_resolved(self):
        worker = self.make_worker()
        worker.model.config = SimpleNamespace(thinker_config=SimpleNamespace(eos_token_id=[3, 5]))
        worker.model.thinker = SimpleNamespace(generation_config=SimpleNamespace(eos_token_id=5))
        self.assertEqual([3, 5], worker._text_eos_token_ids())
        worker._validate_text_completion(torch.tensor([[2, 5]]), [3, 5], 12)

    def test_missing_eos_fails_closed(self):
        worker = self.make_worker()
        worker.model.config = SimpleNamespace()
        worker.processor = SimpleNamespace(tokenizer=SimpleNamespace(eos_token_id=None))
        with self.assertRaisesRegex(RuntimeError, "generation_config_invalid"):
            worker._text_eos_token_ids()

    def test_tokens_after_eos_are_not_silently_trimmed(self):
        with self.assertRaisesRegex(RuntimeError, "generation_boundary_invalid"):
            worker_module.OmniWorker._validate_text_completion(torch.tensor([[2, 3, 4, 3]]), [3], 12)

    def test_physical_context_exhaustion_is_not_success(self):
        with self.assertRaisesRegex(RuntimeError, "context_exhausted"):
            worker_module.OmniWorker._validate_text_completion(torch.tensor([[2, 2]]), [3], 2)

    def test_admission_reserves_answer_before_95_percent(self):
        boundary = 32768 * 95 // 100
        budget = worker_module.OmniWorker._context_output_budget
        self.assertEqual(4097, budget(boundary - 4097, 32768))
        for tokens in (boundary - 4096, boundary, 32769):
            with self.subTest(tokens=tokens), self.assertRaisesRegex(RuntimeError, "context_exhausted"):
                budget(tokens, 32768)

    def test_eos_at_physical_boundary_is_success(self):
        worker_module.OmniWorker._validate_text_completion(torch.tensor([[2, 3]]), [3], 2)

    def test_empty_or_interrupted_response_is_not_success(self):
        for ids in ([], [2]):
            with self.subTest(ids=ids), self.assertRaisesRegex(RuntimeError, "generation_incomplete"):
                worker_module.OmniWorker._validate_text_completion(torch.tensor([ids]), [3], 12)


if __name__ == "__main__":
    unittest.main()
