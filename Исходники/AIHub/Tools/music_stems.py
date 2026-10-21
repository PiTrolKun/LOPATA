"""Offline HT-Demucs ONNX adapter, based on StemSplitio's pinned infer.py (MIT).

Model: 49df9b6989cf2150840ea65b0bef77a2e471b678. Uses its overlap/add algorithm;
only the two requested roles are accumulated, before integer clipping. FFmpeg
resampling/encoding is owned by the host. No downloader, pip, torch import or GUI.
"""
import argparse
import json
import os
import sys
import wave
from pathlib import Path

sys.dont_write_bytecode = True


def run(request):
    for root in reversed(request['import_roots']):
        sys.path.insert(0, root)
    import numpy as np
    import onnxruntime as ort

    if request['schema'] != 1:
        raise ValueError('Unsupported separation protocol')
    n = 343980
    overlap = n // 4
    stride = n - overlap
    with wave.open(request['input'], 'rb') as source:
        if source.getframerate() != 44100 or source.getnchannels() != 2 or source.getsampwidth() != 2:
            raise ValueError('Expected stereo PCM16 at 44100 Hz')
        audio = np.frombuffer(source.readframes(source.getnframes()), dtype='<i2').reshape(-1, 2).astype(np.float32) / 32768
    total = len(audio)
    if not 0 < total <= 44100 * 1800 or not np.isfinite(audio).all():
        raise ValueError('Invalid input samples')
    options = ort.SessionOptions()
    options.intra_op_num_threads = request['threads']
    options.inter_op_num_threads = 1
    providers = ['CPUExecutionProvider']
    dll_handles = []
    if request['device'].startswith('cuda'):
        dll_dir = request['cuda_dlls']
        if dll_dir:
            dll_handles.append(os.add_dll_directory(dll_dir))
            ort.preload_dlls(directory=dll_dir)
        providers.insert(0, ('CUDAExecutionProvider', dict(
            device_id=int(request['device'].split(':')[-1]) if ':' in request['device'] else 0,
            gpu_mem_limit=request['gpu_memory_limit'], arena_extend_strategy='kSameAsRequested',
            cudnn_conv_algo_search='HEURISTIC')))
    try:
        session = ort.InferenceSession(request['model'], sess_options=options, providers=providers)
        session.disable_fallback()
        if request['device'].startswith('cuda') and session.get_providers()[0] != 'CUDAExecutionProvider':
            raise RuntimeError('CUDA provider could not load')
    except Exception as error:
        if request['device'].startswith('cuda'):
            print('STEMS_HARDWARE_FAILURE: ' + str(error), file=sys.stderr, flush=True)
            return 42
        raise
    if session.get_inputs()[0].shape != [1, 2, n] or session.get_outputs()[0].shape != [1, 6, 2, n]:
        raise ValueError('Unexpected model tensor contract')
    voices = np.zeros((total, 2), dtype=np.float32)
    music = np.zeros_like(voices)
    weight = np.zeros(total, dtype=np.float32)
    window = np.ones(n, dtype=np.float32)
    window[:overlap] = np.linspace(0, 1, overlap, dtype=np.float32)
    window[-overlap:] = window[:overlap][::-1]
    chunks = max(1, (total + stride - 1) // stride)
    print('[Stems] provider=' + session.get_providers()[0], flush=True)
    for index in range(chunks):
        start = index * stride
        end = min(start + n, total)
        count = end - start
        chunk = np.zeros((1, 2, n), dtype=np.float32)
        chunk[0, :, :count] = audio[start:end].T
        try:
            stems = session.run(['stems'], {'mix': chunk})[0][0]
        except Exception as error:
            # Only GPU allocation/provider failures merit one host-controlled CPU retry.
            text = str(error)
            if request['device'].startswith('cuda') and any(k in text.lower() for k in
                    ('cuda', 'cudnn', 'cublas', 'allocate', 'out of memory')):
                print('STEMS_HARDWARE_FAILURE: ' + text, file=sys.stderr, flush=True)
                return 42
            raise
        if stems.shape != (6, 2, n) or not np.isfinite(stems).all():
            raise ValueError('Invalid separation output')
        w = window[:count].copy()
        # No preceding/following window at the boundaries: preserve edge samples.
        if index == 0:
            w[:min(overlap, count)] = 1
        if end == total:
            w[max(0, count - overlap):] = 1
        voices[start:end] += stems[3, :, :count].T * w[:, None]
        music[start:end] += stems[[0, 1, 2, 4, 5], :, :count].sum(axis=0).T * w[:, None]
        weight[start:end] += w
        print(f'[Stems] {index + 1}/{chunks}', flush=True)
    voices /= weight[:, None]
    music /= weight[:, None]
    peak = max(float(np.max(np.abs(x))) for x in (voices, music, voices + music))
    gain = min(1.0, 0.98 / max(peak, 1e-8))
    for data, destination in ((voices, request['voice']), (music, request['music'])):
        with open(destination, 'xb') as stream:
            with wave.open(stream, 'wb') as target:
                target.setparams((2, 2, 44100, total, 'NONE', 'not compressed'))
                target.writeframes(np.rint(data * gain * 32768).clip(-32768, 32767).astype('<i2').tobytes())
    Path(request['output']).write_text(json.dumps(dict(schema=1, samples=total, sample_rate=44100,
        gain=gain, provider=session.get_providers()[0], runtime=ort.__version__,
        sources=['drums', 'bass', 'other', 'vocals', 'guitar', 'piano'])), 'utf-8')
    return 0


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    args = parser.parse_args()
    sys.exit(run(json.loads(Path(args.request).read_text('utf-8-sig'))))
