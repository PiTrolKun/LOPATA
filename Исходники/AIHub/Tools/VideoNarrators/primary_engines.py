"""Official PyTorch and ONNX entry points with explicit local assets."""
import importlib
import os
from pathlib import Path
import sys


def load(data, device, execution):
    import soundfile as sf
    engine, model = data['engine'], data['model']
    if engine.startswith('kokoro-'):
        sys.path.insert(0, str(Path(__file__).parents[1]))
        from kokoro_tts_worker import KokoroWorker
        tts = KokoroWorker()
        tts.load(data['language'], model, device)
        execution.loaded(engine='Kokoro PyTorch', device=str(tts.model.device))
        return lambda output: tts.synthesize(data['text'], output, speed=data['rate'] / 100)
    if engine == 'supertonic3':
        import supertonic.loader as loader
        if device.startswith('cuda'):
            loader.DEFAULT_ONNX_PROVIDERS = ['CUDAExecutionProvider', 'CPUExecutionProvider']
        from supertonic import TTS
        tts = TTS(model='supertonic-3', model_dir=model, auto_download=False)
        if data['speaker'] not in tts.voice_style_names:
            raise ValueError('The selected upstream voice is unavailable.')
        execution.loaded(engine='Supertonic ONNX', device=device)
        def synthesize(output):
            wav, _ = tts.synthesize(data['text'], voice_style=tts.get_voice_style(data['speaker']),
                                   lang=data['language'], speed=data['rate'] / 100, total_steps=data['steps'])
            sf.write(output, wav.reshape(-1), tts.sample_rate, subtype='PCM_16')
        return synthesize
    if engine == 'kitten2':
        if device == 'cpu':
            from kitten_tts_cpp import KittenTTS
            tts = KittenTTS(model, decoder='student_w4', device='cpu', offline=True,
                            threads=max(1, min(8, os.cpu_count() or 1)))
            backend = 'KittenTTS C++ / LibTorch CPU'
        else:
            import kittenml.kittentts2.vocoder as vocoder
            def local_decoder(repo_id, filename, **kwargs):
                if repo_id != vocoder.CHATTERBOX_REPO or filename != vocoder.S3GEN_WEIGHTS:
                    raise ValueError('Unknown decoder download was blocked.')
                path = Path(model) / 's3gen' / filename
                if not path.is_file():
                    raise FileNotFoundError('Prepare the pinned S3Gen decoder first.')
                return str(path)
            vocoder.hf_hub_download = local_decoder
            from kittenml import KittenTTS
            tts = KittenTTS(model, decoder='student_w4', device=device, weights='packed')
            from generation_guards import transformer_eos
            transformer_eos(tts.model)
            backend = 'Official KittenTTS PyTorch / packed ternary weights'
        if data['speaker'] not in tts.available_voices:
            raise ValueError('The selected Kitten voice is unavailable.')
        execution.loaded(engine=backend, device=str(tts.device))
        def synthesize(output):
            wav = tts.generate(data['text'], voice=data['speaker'], preset=data['preset'], max_new_tokens=4096)
            if device == 'cpu':
                from generation_guards import cpp_eos
                cpp_eos(tts.last_report)
                execution.actual['nativeReport'] = tts.last_report
            sf.write(output, wav, tts.sample_rate, subtype='PCM_16')
        if device == 'cpu':
            synthesize.close = tts.close
        return synthesize
    if engine in ('qwen3-17b', 'qwen3-06b'):
        import torch
        torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
        from qwen_tts import Qwen3TTSModel
        languages = dict(ru='Russian', en='English', zh='Chinese', ja='Japanese', ko='Korean',
                         de='German', fr='French', it='Italian', es='Spanish', pt='Portuguese')
        dtype = torch.float32
        if device.startswith('cuda'):
            with torch.cuda.device(device):
                dtype = torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
        elif device.startswith('xpu'):
            dtype = torch.float16
        tts = Qwen3TTSModel.from_pretrained(model, device_map=device, dtype=dtype,
                                         attn_implementation='sdpa', local_files_only=True)
        from generation_guards import transformer_eos
        transformer_eos(tts.model.talker)
        execution.loaded(engine='Qwen3-TTS PyTorch', device=str(tts.model.device))
        def synthesize(output):
            args = dict(text=data['text'], speaker=data['speaker'], language=languages[data['language']])
            if engine == 'qwen3-17b' and data['style']:
                args['instruct'] = data['style']
            wavs, sr = tts.generate_custom_voice(**args)
            sf.write(output, wavs[0], sr, subtype='PCM_16')
        return synthesize
    raise ValueError('Unknown primary narrator.')
