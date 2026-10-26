"""Local-file bindings to upstream engines; no model or package downloads."""
import os
from pathlib import Path


def load(data, device, execution):
    engine, model = data['engine'], Path(data['model'])
    if engine == 'cosyvoice300':
        os.environ['CUDA_VISIBLE_DEVICES'] = device.split(':')[-1] if device.startswith('cuda:') else '0' if device == 'cuda' else ''
    if engine == 'vosk010':
        from vosk_tts import Model, Synth
        tts = Synth(Model(model_path=str(model)))
        execution.loaded(engine='Vosk ONNX', device=device)
        return lambda output: tts.synth(data['text'], output, speaker_id=int(data['speaker']), speech_rate=data['rate'] / 100)
    import numpy as np
    import soundfile as sf
    if engine == 'silero56':
        import torch
        torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
        tts = torch.package.PackageImporter(str(model / 'v5_6_ru.pt')).load_pickle('tts_models', 'model')
        if data['speaker'] not in tts.speakers:
            raise ValueError('The selected Silero speaker is unavailable.')
        tts.to(torch.device(device))
        execution.loaded(engine='Silero PyTorch', device=device)
        def synthesize(output):
            wav = tts.apply_tts(text=data['text'], speaker=data['speaker'], sample_rate=48000)
            sf.write(output, wav.detach().cpu().numpy(), 48000, subtype='PCM_16')
        return synthesize
    if engine == 'orpheus3b':
        import orpheus_cpp.model as upstream
        from runtime_execution import vulkan_device
        gpu = vulkan_device() if device != 'cpu' else dict(available=False)
        if device != 'cpu' and not gpu['available']:
            raise RuntimeError('No usable Vulkan GPU; refusing CPU fallback in a GPU trial.')
        def local_file(repo_id, filename, subfolder=None, **kwargs):
            allowed = {('isaiahbjork/orpheus-3b-0.1-ft-Q4_K_M-GGUF', 'orpheus-3b-0.1-ft-q4_k_m.gguf', None): 'orpheus.gguf',
                       ('onnx-community/snac_24khz-ONNX', 'decoder_model.onnx', 'onnx'): 'snac.onnx'}
            name = allowed.get((repo_id, filename, subfolder))
            if name is None or not (model / name).is_file():
                raise ValueError('The pinned local Orpheus/SNAC file is unavailable.')
            return str(model / name)
        upstream.hf_hub_download = local_file
        import llama_cpp
        original = llama_cpp.Llama
        def selected_llama(*args, **kwargs):
            kwargs['n_ctx'] = data.get('context_tokens') or max(4096, ((len(data['text'].encode('utf-8')) + 2367) // 256) * 256)
            kwargs['offload_kqv'] = kwargs['op_offload'] = gpu['available']
            if gpu['available']:
                kwargs['main_gpu'], kwargs['split_mode'] = gpu['index'], llama_cpp.LLAMA_SPLIT_MODE_NONE
            return original(*args, **kwargs)
        llama_cpp.Llama = selected_llama
        tts = upstream.OrpheusCpp(n_gpu_layers=-1 if gpu['available'] else 0,
                                 n_threads=max(1, min(8, os.cpu_count() or 1)), verbose=True, lang='en')
        from generation_guards import CheckedLlama
        tts._llm = CheckedLlama(tts._llm)
        execution.loaded(engine='OrpheusCpp / llama.cpp', device='vulkan:' + str(gpu['index']) if gpu['available'] else 'cpu', gpu=gpu)
        def synthesize(output):
            chunks = [audio.reshape(-1) for rate, audio in tts.stream_tts_sync(data['text'], options={'voice_id': data['speaker']})]
            if not chunks:
                raise ValueError('Orpheus returned no audio.')
            sf.write(output, np.concatenate(chunks), 24000, subtype='PCM_16')
        return synthesize
    if engine == 'cosyvoice300':
        import torch
        torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
        from cosyvoice.cli.cosyvoice import CosyVoice
        tts = CosyVoice(str(model), load_jit=False, load_trt=False, fp16=False)
        if data['speaker'] not in tts.list_available_spks():
            raise ValueError('The selected upstream CosyVoice SFT speaker is unavailable.')
        execution.loaded(engine='CosyVoice SFT PyTorch', device=str(tts.model.device))
        def synthesize(output):
            chunks = [item['tts_speech'].detach().cpu().numpy().reshape(-1)
                      for item in tts.inference_sft(data['text'], data['speaker'], stream=False, speed=data['rate'] / 100)]
            if not chunks:
                raise ValueError('CosyVoice returned no audio.')
            sf.write(output, np.concatenate(chunks), tts.sample_rate, subtype='PCM_16')
        return synthesize
    raise ValueError('The selected extra narrator is unavailable.')


def generate(data, output, device):
    class Report:
        def loaded(self, **facts): pass
    load(data, device, Report())(output)
