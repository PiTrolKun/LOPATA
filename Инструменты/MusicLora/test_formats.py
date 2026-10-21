"""CPU numeric equivalence and rejection tests; run in the prepared ACE Python overlay."""
import hashlib
import json
from pathlib import Path
import runpy
import tempfile
import unittest

import torch
from safetensors.torch import save_file, load_file
from peft import PeftModel

formats = runpy.run_path(str(Path(__file__).resolve().parents[2] / 'Исходники/AIHub/Tools/music_lora_formats.py'))
convert = formats['convert_audio_slider']


class Decoder(torch.nn.Module):
    def __init__(self):
        super().__init__()
        layer = torch.nn.Module()
        layer.self_attn = torch.nn.Module()
        layer.self_attn.q_proj = torch.nn.Linear(5, 7, bias=False)
        self.layers = torch.nn.ModuleList([layer])


class Formats(unittest.TestCase):
    def setUp(self):
        torch.manual_seed(19)
        self.stage = tempfile.TemporaryDirectory(prefix='lopata-test-format-')
        self.addCleanup(self.stage.cleanup)
        self.folder = Path(self.stage.name)
        self.source = self.folder / 'slider.safetensors'
        self.model = Decoder()
        self.down, self.up = torch.randn(4, 5), torch.randn(7, 4)

    def save(self, extra=None, **metadata):
        tensors = {'layers.0.self_attn.to_q.down': self.down, 'layers.0.self_attn.to_q.up': self.up}
        tensors.update(extra or {})
        meta = dict(rank=4, alpha=1.0, backbone='ace-turbo', targets='all') | metadata
        save_file(tensors, str(self.source), metadata={'slider': json.dumps(meta)})

    def test_matrices_scale_and_peft_forward_match_author_equation(self):
        self.save()
        original = self.source.read_bytes()
        receipt = convert(self.source, self.folder / 'peft', self.model)
        weights = load_file(str(self.folder / 'peft/adapter_model.safetensors'))
        self.assertTrue(torch.equal(weights['base_model.model.layers.0.self_attn.q_proj.lora_A.weight'], self.down))
        self.assertTrue(torch.equal(weights['base_model.model.layers.0.self_attn.q_proj.lora_B.weight'], self.up))
        x = torch.randn(9, 5)
        base = self.model.layers[0].self_attn.q_proj(x).detach()
        model = PeftModel.from_pretrained(self.model, str(self.folder / 'peft'))
        self.assertEqual(2, formats['verify_loaded_slider'](self.folder / 'peft', model, 'default'))
        layer = model.get_base_model().layers[0].self_attn.q_proj
        for strength in (0, .2, .5, 1):
            layer.set_scale('default', strength)
            actual = layer(x)
            expected = base + (x @ self.down.T @ self.up.T) * (strength / 4)
            torch.testing.assert_close(actual, expected, rtol=1e-5, atol=2e-6)
        self.assertEqual(original, self.source.read_bytes())
        self.assertEqual(hashlib.sha256((self.folder / 'peft/adapter_model.safetensors').read_bytes()).hexdigest(), receipt['weights_sha256'])
        with torch.no_grad():
            layer.lora_A['default'].weight[0, 0] += 1
        with self.assertRaisesRegex(RuntimeError, 'ignored or changed'):
            formats['verify_loaded_slider'](self.folder / 'peft', model, 'default')

    def test_unknown_tensor_is_rejected(self):
        self.save({'unknown.weight': torch.ones(1)})
        with self.assertRaisesRegex(ValueError, 'unknown'):
            convert(self.source, self.folder / 'peft', self.model)

    def test_wrong_shape_is_rejected(self):
        self.down = torch.randn(4, 6)
        self.save()
        with self.assertRaisesRegex(ValueError, 'shape'):
            convert(self.source, self.folder / 'peft', self.model)

    def test_nonfinite_tensor_is_rejected(self):
        self.up[0, 0] = float('nan')
        self.save()
        with self.assertRaisesRegex(ValueError, 'value'):
            convert(self.source, self.folder / 'peft', self.model)

    def test_unknown_backbone_is_rejected(self):
        self.save(backbone='another-model')
        with self.assertRaisesRegex(ValueError, 'metadata'):
            convert(self.source, self.folder / 'peft', self.model)


if __name__ == '__main__':
    unittest.main()
