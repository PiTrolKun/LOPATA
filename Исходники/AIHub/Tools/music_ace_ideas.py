"""LM-only planning via the pinned author's API. No DiT/VAE inference here."""
import json
import re
from pathlib import Path


def generate_plan(request, companions, device, output, receipt, llm_type, offload, configure_duration):
    import random
    import numpy as np
    import torch
    from acestep.gpu_config import get_global_gpu_config
    from acestep.inference import _update_metadata_from_lm
    params = request['params']
    seed = int(params['seed'])
    random.seed(seed)
    np.random.seed(seed)
    torch.manual_seed(seed)
    if device == 'cuda':
        torch.cuda.manual_seed_all(seed)
    elif device == 'xpu':
        torch.xpu.manual_seed_all(seed)
    llm = llm_type()
    print('[Plan] ACE LM 1.7B only; DiT/VAE weights are not loaded', flush=True)
    status, ok = llm.initialize(checkpoint_dir=companions, lm_model_path='acestep-5Hz-lm-1.7B',
                                backend='pt', device=device, offload_to_cpu=offload)
    if not ok:
        raise RuntimeError(status)
    # Restored legacy plans retain the original six-minute shaping.
    if 'duration_policy' not in request and llm.constrained_processor.max_duration > 360:
        from acestep.constrained_logits_processor import MetadataConstrainedLogitsProcessor
        llm.constrained_processor = MetadataConstrainedLogitsProcessor(
            tokenizer=llm.llm_tokenizer, enabled=True, debug=False, max_duration=360)
    metadata = {k: params[k] for k in ('bpm', 'keyscale', 'timesignature') if params.get(k)}
    gpu_config = get_global_gpu_config()
    duration, duration_info = configure_duration(request, params.get('duration'), gpu_config, llm, params['use_cot_metas'])
    if duration is not None and duration > 0:
        metadata['duration'] = duration
    if params.get('vocal_language') not in ('', 'unknown', 'auto', None):
        metadata['vocal_language'] = params['vocal_language']
    result = llm.generate_with_stop_condition(
        caption=params['caption'], lyrics=params['lyrics'], infer_type='llm_dit',
        temperature=params['lm_temperature'], cfg_scale=params['lm_cfg_scale'],
        negative_prompt=params['lm_negative_prompt'],
        top_k=params['lm_top_k'] or None, top_p=params['lm_top_p'] if params['lm_top_p'] < 1 else None,
        target_duration=duration if duration and duration > 0 else None, user_metadata=metadata or None,
        use_cot_metas=params['use_cot_metas'], use_cot_caption=params['use_cot_caption'],
        use_cot_language=params['use_cot_language'], use_constrained_decoding=True,
        constrained_decoding_debug=False, batch_size=1, seeds=[seed])
    if not result.get('success'):
        raise RuntimeError(result.get('error') or 'Music.AceIdeas.Invalid')
    codes = result.get('audio_codes', '')
    if isinstance(codes, list):
        codes = codes[0] if len(codes) == 1 else ''
    tokens = re.findall(r'<\|audio_code_(\d{1,5})\|>', codes)
    maximum_codes = 1800
    if duration_info is not None:
        from acestep.constants import DURATION_MAX
        maximum_codes = min(DURATION_MAX, gpu_config.max_duration_with_lm) * 5
        receipt['duration_policy'] = duration_info
    if (not 5 <= len(tokens) <= maximum_codes or re.sub(r'<\|audio_code_\d{1,5}\|>', '', codes).strip()
            or any(int(token) > 63999 for token in tokens)):
        raise ValueError('Music.AceIdeas.Invalid')
    native_meta = result.get('metadata') or {}
    if isinstance(native_meta, list):
        native_meta = native_meta[0]
    bpm, key, meter, _, language, caption, lyrics = _update_metadata_from_lm(
        metadata=native_meta, bpm=params.get('bpm'), key_scale=params.get('keyscale', ''),
        time_signature=params.get('timesignature', ''), audio_duration=duration,
        vocal_language=params.get('vocal_language', 'unknown'), caption=params['caption'], lyrics=params['lyrics'])
    plan = dict(Codes=codes, Caption=caption, Lyrics=lyrics, Bpm=int(bpm) if bpm else None,
                Keyscale=key or '', Timesignature=meter or '', VocalLanguage=language or 'unknown',
                Duration=len(tokens) / 5.0)
    Path(output).write_text(json.dumps(plan, ensure_ascii=False), encoding='utf-8')
    receipt.update(lm_only=True, seed=seed, source_revision=request['source_revision'], metadata=native_meta,
                   code_count=len(tokens), lm_model='acestep-5Hz-lm-1.7B')
    Path(output + '.receipt.json').write_text(json.dumps(receipt, ensure_ascii=False), encoding='utf-8')
    print('[Done] ACE musical idea saved; no full song synthesized', flush=True)
