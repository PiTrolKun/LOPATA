"""Offline boundary around the pinned author HeartMuLa pipeline.

Windows adaptations: eager execution (no Triton compiler), backend-aware unload,
and WAV writing with soundfile instead of torchaudio's optional TorchCodec.
Sampling, input preprocessing and codec decoding remain the author's methods.
"""
import gc
import json
import os
from pathlib import Path
import random
import sys
import time


def main():
    overlay, source, model, codec, request_path, output, device, mode = sys.argv[1:]
    sys.path[:0] = [str(Path(source) / 'src'), overlay]
    for key in ('HF_HUB_OFFLINE', 'TRANSFORMERS_OFFLINE', 'HF_DATASETS_OFFLINE'):
        os.environ[key] = '1'
    request = json.loads(Path(request_path).read_text(encoding='utf-8-sig'))
    import numpy as np
    import torch
    import soundfile as sf
    from tokenizers import Tokenizer
    from heartlib.pipelines.music_generation import HeartMuLaGenPipeline, HeartMuLaGenConfig, HeartMuLa, _resolve_devices
    import torchaudio

    selected = torch.device(device)
    if selected.type == 'cuda':
        torch.cuda.set_device(selected)
        name = torch.cuda.get_device_name(selected)
    elif selected.type == 'xpu':
        torch.xpu.set_device(selected)
        name = torch.xpu.get_device_name(selected)
    else:
        name = 'CPU'
    print(f'[Hardware] {device}; {name}; torch={torch.__version__}', flush=True)

    def dtype(index, target, component):
        if index == 0:
            value = torch.float32 if target.type == 'cpu' or component == 'codec' else torch.bfloat16
            if target.type == 'cuda' and value == torch.bfloat16 and not torch.cuda.is_bf16_supported():
                value = torch.float16
        else:
            value = [None, torch.bfloat16, torch.float16, torch.float32][index]
        if target.type == 'cpu' and value != torch.float32:
            raise ValueError('This CPU route requires FP32; choose Auto or FP32 for ' + component)
        if target.type == 'cuda' and value == torch.bfloat16 and not torch.cuda.is_bf16_supported():
            raise ValueError('Selected GPU does not support BF16; choose Auto, FP16 or FP32.')
        return value

    mula_device, codec_device, lazy = _resolve_devices(
        {'mula': selected, 'codec': torch.device('cpu') if request['codec_on_cpu'] else selected}, request['lazy_load'])
    mula_dtype = dtype(request['mula_dtype'], mula_device, 'mula')
    codec_dtype = dtype(request['codec_dtype'], codec_device, 'codec')
    started = time.monotonic()

    class LocalPipeline(HeartMuLaGenPipeline):
        def _apply_compile(self, network):
            print('[Adapter] Windows eager execution; author sampler unchanged.', flush=True)

        def _unload(self):
            if not self.lazy_load:
                return
            devices = set()
            for field, target in (('_mula', self.mula_device), ('_codec', self.codec_device)):
                if getattr(self, field) is not None:
                    setattr(self, field, None)
                    devices.add(target.type)
                    print(f'[Load] Released {field[1:]} from {target}', flush=True)
            gc.collect()
            for backend in devices:
                if backend in ('cuda', 'xpu'):
                    getattr(torch, backend).empty_cache()

    tokenizer = Tokenizer.from_file(str(Path(model) / 'tokenizer.json'))
    config = HeartMuLaGenConfig.from_file(str(Path(model) / 'gen_config.json'))
    print('[Check] Official API and tokenizer imported.', flush=True)
    # No weights loaded by a probe. Preprocessing is the actual author method.
    pipe = LocalPipeline(heartmula_path=str(Path(model) / 'HeartMuLa-oss-3B'),
        heartcodec_path=str(Path(codec) / 'HeartCodec-oss'), heartmula_device=mula_device,
        heartcodec_device=codec_device, heartmula_dtype=mula_dtype,
        heartcodec_dtype=codec_dtype, lazy_load=True, muq_mulan=None,
        text_tokenizer=tokenizer, config=config)
    inputs = {'lyrics': request['lyrics'], 'tags': request['tags']}
    prompt = pipe.preprocess(inputs, request['cfg_scale'])
    prompt_tokens = prompt['tokens'].shape[1]
    maximum_frames = request['max_audio_length_ms'] // 80
    if prompt_tokens + maximum_frames + 1 > 8192:
        raise ValueError(f'Text/tags plus audio exceed the 8192-position author context ({prompt_tokens} + {maximum_frames} + 1). Shorten the request; no text was truncated.')
    model_config = json.loads((Path(model) / 'HeartMuLa-oss-3B' / 'config.json').read_text(encoding='utf-8-sig'))
    if request['topk'] > model_config['audio_vocab_size']:
        raise ValueError('topk exceeds the audio vocabulary.')
    print(f'[Request] Text/tags={prompt_tokens} tokens; audio ceiling={request["max_audio_length_ms"]/1000:g}s', flush=True)
    receipt = dict(request=request, hardware=device, codec_hardware=str(codec_device), mula_precision=str(mula_dtype),
        codec_precision=str(codec_dtype), lazy_load=lazy, prompt_tokens=prompt_tokens,
        adapter='windows-eager-device-unload-soundfile-v1', source=request['source_revision'])
    if mode == 'probe':
        Path(output).write_text('HeartMuLa API ready', encoding='utf-8')
        receipt['check'] = 'imports-and-author-preprocess-no-weights'
    else:
        for warning in request['warnings']:
            print('[Request] ' + warning, flush=True)
        random.seed(request['seed'])
        np.random.seed(request['seed'])
        torch.manual_seed(request['seed'])
        pipe.lazy_load = lazy
        # Match the author constructor's eager-loading path when requested.
        if not lazy:
            print('[Load] Loading generator and codec together', flush=True)
            _ = pipe.mula
            _ = pipe.codec
        tick = [0, 0.0]
        original_frame = HeartMuLa.generate_frame
        def frame(network, *args, **kwargs):
            result = original_frame(network, *args, **kwargs)
            tick[0] += 1
            now = time.monotonic()
            if now - tick[1] >= 2:
                tick[1] = now
                print(f'[AR] Audio frames={tick[0]}; elapsed={now-started:.1f}s', flush=True)
            return result
        HeartMuLa.generate_frame = frame
        original_save = torchaudio.save
        def save(path, wave, rate, **kwargs):
            if str(path) != output:
                raise ValueError('Unexpected output requested by pipeline.')
            audio = wave.detach().to(torch.float32).cpu().numpy()
            if audio.size == 0 or not np.isfinite(audio).all():
                raise ValueError('Invalid generated audio.')
            sf.write(path, audio.T, rate, format='WAV', subtype='PCM_16')
            receipt.update(sample_rate=rate, samples=audio.shape[-1], duration=audio.shape[-1]/rate)
        torchaudio.save = save
        try:
            print('[Load] Starting official HeartMuLa generation', flush=True)
            with torch.no_grad():
                generated = pipe._forward(prompt, request['max_audio_length_ms'], request['temperature'], request['topk'], request['cfg_scale'])
                print('[Synth] Decoding with official HeartCodec', flush=True)
                pipe.postprocess(generated, output)
        finally:
            HeartMuLa.generate_frame = original_frame
            torchaudio.save = original_save
        receipt.update(frames=tick[0], elapsed=time.monotonic()-started)
        print('[Done] HeartMuLa audio and effective request saved.', flush=True)
    Path(output + '.receipt.json').write_text(json.dumps(receipt, ensure_ascii=False), encoding='utf-8')


if __name__ == '__main__':
    main()
