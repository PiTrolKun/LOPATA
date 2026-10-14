"""Offline host-owned GigaAM v3 CTC worker, using verified managed GPU/CPU libraries."""
import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import sys
import time
import wave

PROVIDER = 'GigaAM-v3-CTC/whole-line-search'
VERSION = 'v3_ctc@7447938d791c4f3e643386ee22c33777004293a5/whole-line-1'
WEIGHT_SHA = 'a20a9b5b8ed929c4a60141753cd3dd1284138879a46fb4a1cef6430ec019a02f'


def log(message):
    print(message, flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('--request', required=True)
    args = parser.parse_args()
    request = json.loads(Path(args.request).read_text(encoding='utf-8-sig'))
    if request['schema'] != 1 or request['provider'] != PROVIDER or request['version'] != VERSION:
        raise ValueError('Unsupported lyrics request protocol.')
    device = request.get('device', 'cpu')
    if not re.fullmatch(r'cpu|(?:cuda|xpu):[0-9]{1,2}', device):
        raise ValueError('Unsupported managed acoustic device.')
    weights = Path(request['weights'])
    if weights.name != 'v3_ctc.ckpt':
        raise ValueError('Unsupported GigaAM checkpoint name.')
    with weights.open('rb') as stream:
        if hashlib.file_digest(stream, 'sha256').hexdigest() != WEIGHT_SHA:
            raise ValueError('GigaAM checkpoint integrity mismatch.')
    roots = [str(Path(p).resolve()) for p in request['import_roots']]
    if any(not Path(p).is_dir() for p in roots):
        raise ValueError('Managed GigaAM import directory is missing.')
    sys.path[:0] = roots
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    import numpy as np
    import torch
    import gigaam
    from music_lyrics_ctc import search

    torch.set_num_threads(max(1, min(8, request['threads'])))
    with wave.open(request['audio'], 'rb') as f:
        if f.getnchannels() != 1 or f.getsampwidth() != 2 or f.getframerate() != 16000:
            raise ValueError('Expected mono PCM16/16kHz input.')
        samples = np.frombuffer(f.readframes(f.getnframes()), dtype='<i2').astype(np.float32)/32768
    duration = len(samples)/16000
    if not 0 < duration <= 1800 or abs(duration-request['duration_seconds']) > .25:
        raise ValueError('Decoded song duration does not match the host.')
    source = request['lines']
    if len(source) > 1000 or any(len(r['text']) > 2000 for r in source):
        raise ValueError('Oversized acoustic search request.')
    # Upstream load_model sees an already present, SHA-verified checkpoint.
    # Acquisition belongs to ComponentManager; prohibit worker network attempts.
    import urllib.request
    def offline(*unused, **ignored):
        raise RuntimeError('Network access is forbidden in the lyrics worker.')
    urllib.request.urlopen = offline
    started = time.perf_counter()
    log(f'[Lyrics] Load GigaAM v3 CTC; device={device}')
    model = gigaam.load_model('v3_ctc', device=device, use_flash=False,
                             fp16_encoder=device != 'cpu', download_root=str(weights.parent))
    stride = model.preprocessor.hop_length*int(model.cfg.encoder.subsampling_factor)
    if stride != 640:
        raise ValueError('Unsupported GigaAM acoustic timebase.')
    chunks = []
    for core_start in range(0, len(samples), 20*16000):
        core_end = min(core_start+20*16000, len(samples))
        left, right = max(0, core_start-2*16000), min(len(samples), core_end+2*16000)
        signal = torch.from_numpy(samples[left:right])[None].to(device)
        lengths = torch.tensor([signal.shape[-1]], device=device)
        with torch.inference_mode():
            encoded, unused = model(signal, lengths)
            probs = model.head(encoded)[0].float().cpu().numpy()
        a, b = (core_start-left)//stride, min((core_end-left)//stride, len(probs))
        chunks.append(probs[a:b])
        log(f'[Lyrics] Acoustic frames: {core_end/16000:.1f}/{duration:.1f}s; {device}')
    probs = np.concatenate(chunks)
    # Upstream stores vocabulary as OmegaConf ListConfig. Numpy integer indexes
    # used by the matcher require an ordinary list, not ListConfig.__getitem__.
    chars = list(model.decoding.tokenizer.vocab)+['<blank>']
    blank = model.decoding.blank_id
    np.savez_compressed(Path(args.request).parent/'acoustics.npz', log_probs=probs,
                        stride=stride, rate=16000, vocabulary=np.asarray(chars), blank=blank)
    peak_device_bytes = 0 if device == 'cpu' else int(getattr(torch,device.split(':')[0]).max_memory_allocated())
    del model
    del signal, encoded, lengths
    if device != 'cpu':
        getattr(torch,device.split(':')[0]).empty_cache()
        log('[Lyrics] Acoustic model released; phrase search continues on CPU.')
    found, diagnostics = search(probs, chars, blank, stride/16000, source, log)
    lines = [{'index':r['index'], 'start':r['start'], 'end':r['end']} for r in found]
    state = 'unavailable' if not lines else 'complete' if len(lines) == len(source) else 'partial'
    result = {'schema':1, 'provider':PROVIDER, 'version':VERSION,
              'source_hash':request['source_hash'], 'duration_seconds':request['duration_seconds'],
              'state':state, 'lines':lines, 'diagnostics':diagnostics,
              'elapsed_seconds':time.perf_counter()-started, 'device':device, 'peak_device_allocated_bytes':peak_device_bytes}
    destination = Path(request['output'])
    temporary = destination.with_suffix('.tmp')
    temporary.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, destination)
    log(f'[Lyrics] {state}; timedLines={len(lines)}/{len(source)}; elapsed={result["elapsed_seconds"]:.2f}s')


if __name__ == '__main__':
    try:
        main()
    except RuntimeError as error:
        # Report a narrow hardware failure to the host. A fresh CPU process is
        # started only after this GPU process has exited; no in-process retry.
        message = str(error)
        markers = ('out of memory', 'failed to allocate', 'unsupported operation',
                   'no kernel image is available', 'invalid device function',
                   'hipErrorNoBinaryForGpu', 'ZE_RESULT_ERROR_OUT_OF_DEVICE_MEMORY',
                   'ZE_RESULT_ERROR_UNSUPPORTED_FEATURE')
        if 'DefaultCPUAllocator' not in message and any(m.lower() in message.lower() for m in markers):
            print('[Lyrics hardware] '+message, file=sys.stderr, flush=True)
            sys.exit(42)
        raise
