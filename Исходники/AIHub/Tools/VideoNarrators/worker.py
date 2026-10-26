"""Thin offline adapters for the pinned official TTS Python APIs. No model downloads."""
import json
import os
from pathlib import Path
import sys


def configure(overlay, expanded, flavor, device='cpu'):
    import importlib.util
    hardware_torch = importlib.util.find_spec('torch') if device.startswith(('cuda', 'xpu')) else None
    sys.path.insert(0, str(Path(__file__).parent))
    sys.path.insert(0, overlay)
    sys.path.insert(0, str(Path(expanded) / 'Audio' / flavor / 'Lib' / 'site-packages'))
    if device.startswith('cuda'):
        sys.path.insert(0, str(Path(expanded) / 'Gpu/cuda/Lib/site-packages'))
    vulkan = Path(expanded) / 'Gpu/vulkan/Lib/site-packages'
    if vulkan.is_dir():
        # The CPU control uses the same llama.cpp build with zero GPU layers.
        sys.path.insert(0, str(vulkan))
    for source in sorted((Path(expanded) / 'Sources').glob('*')):
        if source.is_dir():
            sys.path.insert(0, str(source))
    if hardware_torch is not None:
        # Keep pinned NumPy/dependencies from the engine overlay, but load the
        # managed hardware torch rather than Kitten's CPU-only native build.
        torch = importlib.util.module_from_spec(hardware_torch)
        sys.modules['torch'] = torch
        hardware_torch.loader.exec_module(torch)
        torch.set_num_threads(max(1, min(8, os.cpu_count() or 1)))
    for key in ('HF_HUB_OFFLINE', 'TRANSFORMERS_OFFLINE', 'HF_HUB_DISABLE_TELEMETRY'):
        os.environ[key] = '1'
    # Reject unexpected network access by an upstream optional helper as well.
    def offline(event, args):
        if event in ('socket.connect', 'socket.getaddrinfo'):
            raise RuntimeError('Network access is disabled during narration; prepare components first.')
    sys.addaudithook(offline)


def check_runtime(engine):
    """Exercise native audio IO and upstream imports without loading model weights."""
    import io
    import numpy as np
    import soundfile as sf
    stream = io.BytesIO()
    sf.write(stream, np.zeros(240, dtype=np.float32), 24000, format='WAV', subtype='PCM_16')
    stream.seek(0)
    audio, rate = sf.read(stream)
    assert rate == 24000 and len(audio) == 240
    if engine == 'supertonic3':
        from supertonic import TTS
    elif engine == 'kitten2':
        from kitten_tts_cpp import KittenTTS
    elif engine in ('qwen3-17b', 'qwen3-06b'):
        from qwen_tts import Qwen3TTSModel
    elif engine == 'vosk010':
        from vosk_tts import Model, Synth
    elif engine == 'silero56':
        import torch.package
    elif engine == 'orpheus3b':
        import orpheus_cpp
        import llama_cpp
    elif engine == 'cosyvoice300':
        from cosyvoice.cli.cosyvoice import CosyVoice
    else:
        raise ValueError('Unknown runtime check.')
    print(json.dumps(dict(engine=engine, soundfile=sf.__version__,
                          libsndfile=sf.__libsndfile_version__, audio_io=True, imports=True)))


def serve(overlay, expanded, flavor, device):
    import traceback
    protocol = sys.stdout
    configure(overlay, expanded, flavor, device)
    from runtime_execution import Execution
    from chunked_synthesis import wrap
    progress = lambda fraction: print('LOPATA_NARRATOR_PROGRESS ' + json.dumps(dict(fraction=fraction)), file=protocol, flush=True)
    active = execution = synthesize = None
    try:
        for line in sys.stdin:
            frame = json.loads(line)
            if frame.get('quit'):
                break
            try:
                data = json.loads(Path(frame['request']).read_text(encoding='utf-8'))
                if active is None:
                    active = data
                    execution = Execution(device, frame['output'], data.get('repeats', 1))
                    execution.configure_onnx(gpu=data['engine'] in ('supertonic3', 'vosk010', 'orpheus3b'))
                    if data['engine'] in ('vosk010', 'silero56', 'orpheus3b', 'cosyvoice300'):
                        from extra_engines import load
                    else:
                        from primary_engines import load
                    synthesize = wrap(active, load(active, device, execution), execution, progress)
                else:
                    if (data['engine'], data['model']) != (active['engine'], active['model']):
                        raise ValueError('A narrator session cannot substitute a different model.')
                    active.update(data)
                    execution.reset(frame['output'], data.get('repeats', 1))
                execution.run(synthesize)
                print('LOPATA_NARRATOR_RESPONSE ' + json.dumps(dict(id=frame['id'], ok=True)), file=protocol, flush=True)
            except Exception as error:
                traceback.print_exc(file=sys.stderr)
                print('LOPATA_NARRATOR_RESPONSE ' + json.dumps(dict(id=frame['id'], ok=False, error=str(error))), file=protocol, flush=True)
                break
    finally:
        close = getattr(synthesize, 'close', None)
        if close is not None:
            close()


def main():
    if sys.argv[1] == '--serve':
        serve(*sys.argv[2:])
        return
    if sys.argv[1] in ('--check-runtime', '--probe-vulkan'):
        action, overlay, expanded, flavor, *engine = sys.argv[1:]
        configure(overlay, expanded, flavor, 'vulkan' if action == '--probe-vulkan' else 'cpu')
        if action == '--probe-vulkan':
            from runtime_execution import vulkan_device
            print(json.dumps(vulkan_device()))
        else:
            check_runtime(engine[0])
        return
    overlay, expanded, flavor, request, output, device = sys.argv[1:]
    protocol = sys.stdout
    configure(overlay, expanded, flavor, device)
    data = json.loads(Path(request).read_text(encoding='utf-8'))
    from runtime_execution import Execution
    execution = Execution(device, output, data.get('repeats', 1))
    execution.configure_onnx(gpu=data['engine'] in ('supertonic3', 'vosk010', 'orpheus3b'))
    if data['engine'] in ('vosk010', 'silero56', 'orpheus3b', 'cosyvoice300'):
        from extra_engines import load
    else:
        from primary_engines import load
    from chunked_synthesis import wrap
    synthesize = wrap(data, load(data, device, execution), execution,
        lambda fraction: print('LOPATA_NARRATOR_PROGRESS ' + json.dumps(dict(fraction=fraction)), file=protocol, flush=True))
    try:
        execution.run(synthesize)
    finally:
        close = getattr(synthesize, 'close', None)
        if close is not None:
            close()


if __name__ == '__main__':
    main()
