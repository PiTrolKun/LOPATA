"""Lossless Audio Sliders v1 -> official ACE PEFT artifact adaptation.

Reference: takakhoo/audio-diffusion-control, audiosliders/lora.py (MIT).
Both layouts compute strength * (alpha / rank) * B(A(x)); no sampler changes.
Original downloaded files remain untouched; derived files live in a job temp dir.
"""
import hashlib
import json
import math
from pathlib import Path
import re

RECIPE = 'audio-sliders-peft-v1'


def convert_audio_slider(source, destination, decoder):
    from safetensors import safe_open
    from safetensors.torch import save_file
    import torch

    with safe_open(str(source), framework='pt', device='cpu') as file:
        meta = json.loads((file.metadata() or {}).get('slider', '{}'))
        rank, alpha = meta.get('rank'), meta.get('alpha')
        if (type(rank) is not int or not 1 <= rank <= 128 or type(alpha) not in (int, float)
                or not math.isfinite(alpha) or alpha <= 0 or meta.get('backbone') != 'ace-turbo'
                or meta.get('targets') != 'all'):
            raise ValueError('Unsupported Audio Sliders metadata')
        keys = set(file.keys())
        stems = {key.removesuffix('.down') for key in keys if key.endswith('.down')}
        if not stems or keys != {stem + suffix for stem in stems for suffix in ('.down', '.up')}:
            raise ValueError('Incomplete or unknown Audio Sliders tensors')
        model = decoder.get_base_model() if hasattr(decoder, 'get_base_model') else decoder
        tensors, targets = {}, []
        rename = {'to_q': 'q_proj', 'to_k': 'k_proj', 'to_v': 'v_proj', 'to_out.0': 'o_proj'}
        for stem in sorted(stems):
            match = re.fullmatch(r'layers\.(\d+)\.(?:(self_attn|cross_attn)\.(to_q|to_k|to_v|to_out\.0)|mlp\.(gate_proj|up_proj|down_proj))', stem)
            if not match or int(match[1]) >= 32:
                raise ValueError('Unknown Audio Sliders layer: ' + stem)
            path = f'layers.{match[1]}.' + (f'{match[2]}.{rename[match[3]]}' if match[2] else 'mlp.' + match[4])
            base = model.get_submodule(path)
            if hasattr(base, 'base_layer'):
                base = base.base_layer
            down, up = file.get_tensor(stem + '.down'), file.get_tensor(stem + '.up')
            if (not isinstance(base, torch.nn.Linear) or tuple(down.shape) != (rank, base.in_features)
                    or tuple(up.shape) != (base.out_features, rank)
                    or down.dtype != torch.float32 or up.dtype != torch.float32
                    or not torch.isfinite(down).all() or not torch.isfinite(up).all()):
                raise ValueError('Audio Sliders shape/dtype/value mismatch: ' + path)
            targets.append(path)
            tensors['base_model.model.' + path + '.lora_A.weight'] = down
            tensors['base_model.model.' + path + '.lora_B.weight'] = up
    destination = Path(destination)
    destination.mkdir(parents=True, exist_ok=False)
    config = dict(peft_type='LORA', r=rank, lora_alpha=alpha, lora_dropout=0.0,
                  bias='none', target_modules=targets, inference_mode=True, use_rslora=False)
    config_path = destination / 'adapter_config.json'
    config_path.write_text(json.dumps(config, sort_keys=True), encoding='utf-8')
    weights = destination / 'adapter_model.safetensors'
    save_file(tensors, str(weights), metadata={'lopata_format_recipe': RECIPE})
    def digest(path):
        with path.open('rb') as stream:
            return hashlib.file_digest(stream, 'sha256').hexdigest()
    return dict(recipe=RECIPE, layers=len(targets), rank=rank, alpha=alpha,
                weights_sha256=digest(weights), config_sha256=digest(config_path))


def verify_loaded_slider(directory, decoder, name):
    """Reject a partially ignored PEFT load, including silently missing tensor keys."""
    from safetensors import safe_open
    import torch
    model = decoder.get_base_model()
    count = 0
    with safe_open(str(Path(directory) / 'adapter_model.safetensors'), framework='pt', device='cpu') as file:
        for key in file.keys():
            path, _, kind = key.removeprefix('base_model.model.').rpartition('.lora_')
            if kind not in ('A.weight', 'B.weight'):
                raise ValueError('Unexpected converted tensor')
            layer = model.get_submodule(path)
            bank = layer.lora_A if kind == 'A.weight' else layer.lora_B
            actual = bank[name].weight.detach().cpu()
            if not torch.equal(actual, file.get_tensor(key).to(actual.dtype)):
                raise RuntimeError('ACE ignored or changed adapter tensor: ' + key)
            count += 1
    return count
