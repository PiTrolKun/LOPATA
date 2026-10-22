"""Retake compatibility for the pinned XL checkpoint's older remote-code file.

The variance-preserving sin/cos mix is from ACE-Step (MIT), pinned source
ca1e85fe, models/xl_turbo/modeling_acestep_v15_xl_turbo.py. The checkpoint at
d4a0b288 contains an older generate_audio(**kwargs) which drops retake args.
Only its existing noise factory is wrapped; model weights and sampler stay intact.
"""
import hashlib
import inspect
import math
from pathlib import Path


LEGACY_MODEL_HASH = '6576bd225807998ac169c5a05b86fb778091d5967666cbb417841836bd553ecc'


def install(model, variance, retake_seed):
    signature = inspect.signature(model.generate_audio)
    if 'retake_variance' in signature.parameters and 'retake_seed' in signature.parameters:
        return {'mode': 'native', 'applied': True}
    state = {'mode': 'legacy-noop', 'applied': variance == 0, 'noise_calls': 0}
    if variance == 0:
        return state
    source = Path(inspect.getfile(type(model)))
    if hashlib.sha256(source.read_bytes()).hexdigest() != LEGACY_MODEL_HASH:
        raise RuntimeError('Music.AceIdeas.RetakeUnsupported')
    import torch
    original = model.prepare_noise

    def digest(value):
        return hashlib.sha256(value.detach().float().cpu().contiguous().numpy().tobytes()).hexdigest()

    def mixed_noise(context_latents, seed=None):
        state['noise_calls'] += 1
        if state['noise_calls'] != 1:
            raise RuntimeError('Music.AceIdeas.RetakeUnsupported')
        noise = original(context_latents, seed)
        retake_noise = original(context_latents, retake_seed)
        # Exact author formula; no custom diffusion or audio mixing.
        v_rad = variance * (math.pi / 2.0)
        mixed = math.cos(v_rad) * noise + math.sin(v_rad) * retake_noise
        state.update(mode='pinned-xl-noise-adapter-1', base_noise_sha256=digest(noise),
                     retake_noise_sha256=digest(retake_noise), mixed_noise_sha256=digest(mixed),
                     applied=not torch.equal(noise, mixed))
        if not state['applied']:
            raise RuntimeError('Music.AceIdeas.RetakeUnsupported')
        return mixed

    model.prepare_noise = mixed_noise
    return state
