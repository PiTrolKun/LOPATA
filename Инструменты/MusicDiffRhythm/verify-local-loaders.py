"""Regression for the offline Hub local-bin path; uses tiny synthetic weights."""
import argparse
import json
from pathlib import Path
import sys
import tempfile
from unittest.mock import patch

parser = argparse.ArgumentParser()
parser.add_argument('--overlay', required=True)
args = parser.parse_args()
workspace = Path(__file__).resolve().parents[2]
sys.path[:0] = [str(workspace / 'Исходники/AIHub/Tools'), args.overlay]
import torch
from huggingface_hub import PyTorchModelHubMixin
from safetensors.torch import save_file
from music_diffrhythm_worker import install_local_loaders


class Tiny(torch.nn.Module, PyTorchModelHubMixin):
    def __init__(self, width: int):
        super().__init__()
        self.weight = torch.nn.Parameter(torch.arange(width, dtype=torch.float32))


with tempfile.TemporaryDirectory(prefix='lopata-diff-loader-test-') as temporary:
    root = Path(temporary)
    model, companions = root / 'model', root / 'companions'
    binary, safe = companions / 'muq', companions / 'mulan'
    for folder in (binary, safe):
        folder.mkdir(parents=True)
        (folder / 'config.json').write_text(json.dumps({'width': 3}), encoding='utf-8')
    expected = {'weight': torch.tensor([4., 5., 6.])}
    torch.save(expected, binary / 'pytorch_model.bin')
    save_file(expected, str(safe / 'model.safetensors'))
    install_local_loaders(model, companions)
    original_load = torch.load
    calls = []
    def checked_load(*args, **kwargs):
        assert kwargs.get('weights_only') is True
        calls.append(args[0])
        return original_load(*args, **kwargs)
    with patch.object(torch, 'load', checked_load):
        result = Tiny.from_pretrained('OpenMuQ/MuQ-large-msd-iter', strict=True)
        assert torch.equal(result.weight, expected['weight'])
    assert len(calls) == 1
    assert torch.equal(Tiny.from_pretrained('OpenMuQ/MuQ-MuLan-large', strict=True).weight,
                       expected['weight'])
    for unsupported in ('unapproved/model', str(root / 'elsewhere')):
        try:
            Tiny.from_pretrained(unsupported)
        except ValueError:
            pass
        else:
            raise AssertionError('Unpinned model was allowed')
    (binary / 'pytorch_model.bin').unlink()
    try:
        Tiny.from_pretrained('OpenMuQ/MuQ-large-msd-iter')
    except FileNotFoundError:
        pass
    else:
        raise AssertionError('Missing weights were not rejected')
print('PASS: local BIN and Safetensors, weights_only, pinned roots, missing-file rejection')
