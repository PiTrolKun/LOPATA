"""Offline process adapter to the unmodified, pinned official ACE-Step API."""
import json
import math
import os
from pathlib import Path
import sys
import tempfile
import time


class BoundedTokenizer:
    def __init__(self, tokenizer):
        self._tokenizer = tokenizer

    def __getattr__(self, name):
        return getattr(self._tokenizer, name)

    def __call__(self, text, **kwargs):
        maximum = kwargs.get('max_length')
        if kwargs.get('truncation') and maximum:
            values = text if isinstance(text, list) else [text]
            for value in values:
                count = len(self._tokenizer.encode(value, add_special_tokens=kwargs.get('add_special_tokens', True)))
                if count > maximum:
                    raise ValueError(f'ACE text conditioning exceeds {maximum} tokens ({count}); shorten lyrics/caption. No text was silently truncated.')
        return self._tokenizer(text, **kwargs)


def configure_duration(request, requested, gpu_config, llm, use_cot_metas):
    policy = request.get('duration_policy')
    if policy is None:
        return requested, None
    from acestep.constants import DURATION_MIN, DURATION_MAX
    hardware_limit = gpu_config.max_duration_with_lm if llm is not None else gpu_config.max_duration_without_lm
    maximum = min(hardware_limit, DURATION_MAX)
    info = {'effective_limit_seconds': None, 'stop_reason': None}

    def selected(seconds, source):
        info.update(effective_limit_seconds=seconds, source=source)
        print(f'[ACE] Duration {seconds:g}s ({source}); device limit {maximum:g}s.', flush=True)
        return seconds

    if requested is not None and requested > 0:
        if requested > maximum:
            raise ValueError(f'ACE requested duration {requested:g}s exceeds the limit for the selected device and mode ({maximum:g}s).')
        return selected(requested, 'manual'), info

    fallback = min(policy['fallback_seconds'], maximum)
    if llm is None or not use_cot_metas:
        return selected(fallback, 'fallback'), info

    # This pinned API parses the CoT response before setting the Phase 2 code
    # budget. Supply a missing duration there so CoT and DiT use the same value.
    parse = llm.parse_lm_output

    def parse_cot(text):
        llm.parse_lm_output = parse
        metadata, codes = parse(text)
        try:
            duration = float(metadata.get('duration'))
        except (TypeError, ValueError):
            duration = math.nan
        if not math.isfinite(duration) or not DURATION_MIN <= duration <= maximum:
            metadata = dict(metadata, duration=selected(fallback, 'fallback'))
        else:
            selected(duration, 'model')
        return metadata, codes

    llm.parse_lm_output = parse_cot
    return requested, info


def main():
    overlay, source, model, companions, request_path, output, device, mode = sys.argv[1:]
    sys.path[:0] = [source, overlay]
    os.environ['ACESTEP_CHECKPOINTS_DIR'] = companions
    os.environ['HF_HUB_OFFLINE'] = os.environ['TRANSFORMERS_OFFLINE'] = '1'
    os.environ['GRADIO_ANALYTICS_ENABLED'] = 'False'
    import torch
    if device.startswith('cuda'):
        index = int(device.split(':')[1]) if ':' in device else 0
        torch.cuda.set_device(index)
        name = torch.cuda.get_device_name(index)
        execution_device = 'cuda'
    elif device.startswith('xpu'):
        index = int(device.split(':')[1]) if ':' in device else 0
        torch.xpu.set_device(index)
        name = torch.xpu.get_device_name(index)
        execution_device = 'xpu'
    else:
        execution_device, name = 'cpu', 'CPU'
    print(f'[Hardware] {device}; {name}; torch={torch.__version__}', flush=True)

    # The official main bundle also includes an unused ordinary 2B DiT. Select
    # our verified XL instead, so its availability cannot trigger a hidden download.
    import acestep.model_downloader as downloader
    downloader.MAIN_MODEL_COMPONENTS = ['vae', 'Qwen3-Embedding-0.6B',
                                        'acestep-5Hz-lm-1.7B', str(Path(model) / 'acestep-v15-xl-turbo')]

    def no_download(*args, **kwargs):
        raise RuntimeError('Missing ACE component. Install/verify through LOPATA before generation.')

    downloader._smart_download = no_download
    from acestep.handler import AceStepHandler
    from acestep.llm_inference import LLMHandler
    from acestep.inference import GenerationParams, GenerationConfig, generate_music
    from acestep.gpu_config import get_global_gpu_config, get_gpu_config, set_global_gpu_config
    import soundfile as sf

    with open(request_path, encoding='utf-8-sig') as file:
        request = json.load(file)
    import runpy
    cover_runtime = runpy.run_path(str(Path(__file__).with_name('music_cover_runtime.py')))
    cover_runtime['verify_inputs'](request.get('audio_task'))
    for warning in request['warnings']:
        print('[ACE] ' + warning, flush=True)
    params = GenerationParams(**request['params'])
    config = GenerationConfig(**request['config'])
    public_request = cover_runtime['public_request'](request)
    receipt = {'request': public_request, 'hardware': device, 'torch': torch.__version__, 'mode': mode}
    if mode == 'probe':
        # Import/API validation only: no weights loaded, no inference.
        Path(output).write_text('ACE official API ready', encoding='utf-8')
        Path(output + '.receipt.json').write_text(json.dumps(receipt, ensure_ascii=False), encoding='utf-8')
        print('[ACE] Official API imports verified; no generation performed.', flush=True)
        return

    runtime = request['runtime']
    gpu_config = get_global_gpu_config()
    if request.get('duration_policy') is not None:
        # The default upstream probe uses device zero. Keep all duration
        # checks on the same tier as the adapter chosen for this process.
        memory_gb = 0.0
        if execution_device in ('cuda', 'xpu'):
            selected = getattr(torch, execution_device).get_device_properties(index)
            memory_gb = selected.total_memory / (1024 ** 3)
        gpu_config = get_gpu_config(memory_gb)
        set_global_gpu_config(gpu_config)

    def automatic(key, default):
        value = runtime[key]
        return default if value == -1 else value == 1

    if mode == 'plan':
        ideas = runpy.run_path(str(Path(__file__).with_name('music_ace_ideas.py')))
        ideas['generate_plan'](request, companions, execution_device, output, receipt, LLMHandler,
                               automatic('lm_offload_to_cpu', gpu_config.offload_to_cpu_default), configure_duration)
        return

    init = dict(project_root=source, config_path=str(Path(model) / 'acestep-v15-xl-turbo'),
                device=execution_device, use_flash_attention=bool(runtime['use_flash_attention']),
                compile_model=bool(runtime['compile_model']), quantization=None, use_mlx_dit=False,
                offload_to_cpu=automatic('offload_to_cpu', gpu_config.offload_to_cpu_default),
                offload_dit_to_cpu=automatic('offload_dit_to_cpu', gpu_config.offload_dit_to_cpu_default))
    receipt['initialization'] = init
    handler = AceStepHandler()
    print('[Load] ACE XL Turbo, VAE and text encoder', flush=True)
    status, ok = handler.initialize_service(**init)
    print('[ACE] ' + str(status), flush=True)
    if not ok:
        raise RuntimeError(status)
    retake_runtime = runpy.run_path(str(Path(__file__).with_name('music_ace_retake.py')))
    receipt['retake_adapter'] = retake_runtime['install'](handler.model, params.retake_variance, params.retake_seed)
    receipt['references'] = cover_runtime['attach_references'](handler, request.get('audio_task'))
    if receipt['references']:
        print('[Cover] Packed references: ' + str(len(receipt['references'])), flush=True)
    import runpy
    lora_runtime = runpy.run_path(str(Path(__file__).with_name('music_lora_runtime.py')))
    receipt['loras'] = lora_runtime['apply_ace_loras'](handler, request.get('loras', []))
    # Upstream's conditioner truncates at 256/2048 tokens. Keep the official
    # tokenizer, but fail explicitly instead of silently discarding input.
    handler.text_tokenizer = BoundedTokenizer(handler.text_tokenizer)
    llm = None
    if params.thinking or params.use_cot_metas or params.use_cot_caption or params.use_cot_language:
        print('[Plan] ACE LM 1.7B · official PyTorch backend', flush=True)
        llm = LLMHandler()
        lm_offload = automatic('lm_offload_to_cpu', gpu_config.offload_to_cpu_default)
        status, ok = llm.initialize(checkpoint_dir=companions, lm_model_path='acestep-5Hz-lm-1.7B',
                                    backend='pt', device=execution_device, offload_to_cpu=lm_offload)
        print('[ACE] ' + str(status), flush=True)
        receipt['lm_initialization'] = {'backend': 'pt', 'model': 'acestep-5Hz-lm-1.7B', 'offload_to_cpu': lm_offload}
        if not ok:
            raise RuntimeError(status)
    params.duration, duration_info = configure_duration(
        request, params.duration, get_global_gpu_config(), llm, params.use_cot_metas)
    if duration_info is not None:
        receipt['duration_policy'] = duration_info
    print('[Synth] Official ACE generation pipeline', flush=True)

    def progress(value, desc='', **kwargs):
        print(f'[ACE Progress] {value}: {desc}', flush=True)

    started = time.monotonic()
    with tempfile.TemporaryDirectory(prefix='lopata-ace-') as stage:
        result = generate_music(handler, llm, params, config, save_dir=stage, progress=progress)
        if params.retake_variance > 0 and not receipt['retake_adapter'].get('applied'):
            raise RuntimeError('Music.AceIdeas.RetakeUnsupported')
        lora_runtime['verify_ace_loras'](handler, request.get('loras', []))
        if not result.success or len(result.audios) != 1:
            raise RuntimeError(result.error or result.status_message or 'ACE returned no single audio result.')
        audio = result.audios[0]
        path = audio['path']
        samples, samplerate = sf.read(path, dtype='float32', always_2d=True)
        if samples.shape[0] == 0 or samples.shape[1] not in (1, 2):
            raise RuntimeError('Invalid ACE audio shape.')
        sf.write(output, samples, samplerate, format='WAV', subtype='PCM_16')
        receipt['effective'] = audio.get('params', {})
        receipt['status'] = result.status_message
        receipt['elapsed_seconds'] = time.monotonic() - started
        receipt['samplerate'] = samplerate
        receipt['duration_seconds'] = len(samples) / samplerate
        Path(output + '.receipt.json').write_text(json.dumps(receipt, ensure_ascii=False, default=str), encoding='utf-8')
    print('[Done] ACE audio and parameter receipt saved.', flush=True)


if __name__ == '__main__':
    main()
