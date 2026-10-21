"""Verified PEFT adapters through the pinned ACE handler; no replacement inference pipeline."""
import hashlib
import math
from contextlib import ExitStack
from pathlib import Path
import runpy
import tempfile


def verify_ace_loras(handler, uses):
    if not uses:
        if getattr(handler, 'use_lora', False):
            raise RuntimeError('Unexpected ACE LoRA activation')
        return
    decoder = handler.model.decoder
    active = decoder.active_adapters
    active = [active] if isinstance(active, str) else list(active)
    expected = [u['id'] for u in uses]
    if sorted(active) != sorted(expected) or not handler.use_lora:
        raise RuntimeError(f'ACE LoRA activation mismatch: {active} != {expected}')
    for u in uses:
        name, strength = u['id'], u['strength']
        layers = [m for m in decoder.modules() if hasattr(m, 'lora_A') and name in m.lora_A]
        if not layers or any(getattr(m, 'disable_adapters', False) for m in layers):
            raise RuntimeError('ACE LoRA has no active tensor layers: ' + name)
        for m in layers:
            config = decoder.peft_config[name]
            expected_scale = strength * m.lora_alpha[name] / (math.sqrt(m.r[name]) if config.use_rslora else m.r[name])
            if not math.isclose(m.scaling[name], expected_scale, rel_tol=1e-5, abs_tol=1e-7):
                raise RuntimeError('ACE LoRA strength mismatch: ' + name)


def apply_ace_loras(handler, uses):
    if not uses:
        verify_ace_loras(handler, uses)
        return []
    public = []
    with ExitStack() as temporary:
        for u in uses:
            directory = Path(u['directory']).resolve(strict=True)
            for f in u['files']:
                path = directory / f['name']
                if path.parent != directory or path.is_symlink():
                    raise RuntimeError('Invalid ACE LoRA file')
                with path.open('rb') as stream:
                    if hashlib.file_digest(stream, 'sha256').hexdigest() != f['sha256'].lower():
                        raise RuntimeError('ACE LoRA hash mismatch')
            item = {k: v for k, v in u.items() if k != 'directory'}
            format_name = u.get('format', 'peft')
            if format_name == 'audio-sliders-peft-v1':
                if len(u['files']) != 1 or not u['files'][0]['name'].endswith('.safetensors'):
                    raise RuntimeError('Invalid Audio Sliders file set')
                stage = Path(temporary.enter_context(tempfile.TemporaryDirectory(prefix='lopata-lora-format-')))
                converter = runpy.run_path(str(Path(__file__).with_name('music_lora_formats.py')))
                item['adaptation'] = converter['convert_audio_slider'](directory / u['files'][0]['name'], stage / 'peft', handler.model.decoder)
                directory = stage / 'peft'
            elif format_name != 'peft':
                raise RuntimeError('Unsupported ACE LoRA format')
            print('[LoRA] Loading ' + u['id'], flush=True)
            status = handler.add_lora(str(directory), adapter_name=u['id'])
            if u['id'] not in getattr(handler, '_active_loras', {}):
                raise RuntimeError('ACE did not load requested LoRA: ' + str(status))
            if format_name == 'audio-sliders-peft-v1':
                item['adaptation']['verified_tensors'] = converter['verify_loaded_slider'](directory, handler.model.decoder, u['id'])
            public.append(item)
    handler.set_use_lora(True)
    for u in uses:
        handler.set_lora_scale(u['id'], u['strength'])
    # The handler's single-current-adapter UI helper selects only the last one.
    # PEFT itself accepts a list; restore the complete requested bank afterwards.
    handler.model.decoder.base_model.set_adapter([u['id'] for u in uses], inference_mode=True)
    handler.model.decoder.enable_adapter_layers()
    verify_ace_loras(handler, uses)
    print('[LoRA] Verified active adapters: ' + str([(u['id'], u['strength']) for u in uses]), flush=True)
    return public
