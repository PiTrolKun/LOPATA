"""Owned offline adapter. Sampling and decoding stay in the pinned author API."""
import json
import os
from pathlib import Path
import random
import shutil
import sys
from contextlib import contextmanager
import time


def install_local_loaders(model, companions):
    import huggingface_hub as hub
    from transformers import AutoTokenizer, XLMRobertaModel
    roots = {'ASLP-lab/DiffRhythm2': Path(model) / 'diff',
             'OpenMuQ/MuQ-MuLan-large': Path(companions) / 'mulan',
             'OpenMuQ/MuQ-large-msd-iter': Path(companions) / 'muq',
             'xlm-roberta-base': Path(companions) / 'xlm',
             'FacebookAI/xlm-roberta-base': Path(companions) / 'xlm'}
    def local(name):
        if name in roots: return str(roots[name])
        path = Path(name).resolve()
        if path in {p.resolve() for p in roots.values()}: return str(path)
        raise ValueError('Unpinned model requested by author pipeline: ' + str(name))
    def download(repo_id, filename, **kwargs):
        path = (Path(local(repo_id)) / filename).resolve()
        if path.parent != Path(local(repo_id)).resolve() or not path.is_file():
            raise FileNotFoundError('Prepare pinned component through LOPATA: ' + str(path))
        return str(path)
    hub.hf_hub_download = download
    original = hub.PyTorchModelHubMixin.from_pretrained.__func__
    def mixin(cls, name, **kwargs):
        kwargs.update(local_files_only=True)
        return original(cls, local(name), **kwargs)
    hub.PyTorchModelHubMixin.from_pretrained = classmethod(mixin)
    original_weights = hub.PyTorchModelHubMixin._from_pretrained.__func__
    def weights(cls, *, model_id, map_location='cpu', strict=False, **kwargs):
        folder = Path(local(model_id))
        # Hub's local-directory branch assumes Safetensors. The pinned MuQ
        # repositories publish pytorch_model.bin. Use the very same official
        # weights-only pickle loader as its remote-file fallback, offline.
        binary = folder / 'pytorch_model.bin'
        if binary.is_file() and not (folder / 'model.safetensors').is_file():
            network = {'revision', 'cache_dir', 'force_download', 'proxies',
                       'resume_download', 'local_files_only', 'token'}
            arguments = {key: value for key, value in kwargs.items() if key not in network}
            model = cls(**arguments)
            print(f'[Load] Official local weights-only loader: {binary}', flush=True)
            return cls._load_as_pickle(model, str(binary), map_location, strict)
        return original_weights(cls, model_id=str(folder), map_location=map_location,
                                strict=strict, **kwargs)
    hub.PyTorchModelHubMixin._from_pretrained = classmethod(weights)
    for cls in (AutoTokenizer, XLMRobertaModel):
        method = cls.from_pretrained.__func__
        def load(cls, name, _method=method, **kwargs):
            kwargs.update(local_files_only=True, trust_remote_code=False)
            return _method(cls, local(name), **kwargs)
        cls.from_pretrained = classmethod(load)


@contextmanager
def owned_staging(output):
    staging = Path(output + '.source')
    staging.mkdir()  # Refuse to overwrite any pre-existing folder.
    try:
        yield str(staging)
    finally:
        shutil.rmtree(staging)


def main():
    overlay, source, model, companions, request_path, output, device, mode = sys.argv[1:]
    request = json.loads(Path(request_path).read_text(encoding='utf-8-sig'))
    # The trusted source tree stays immutable. The author G2P resolves its ONNX
    # file relative to source, so stage an owned copy beside prepared weights.
    with owned_staging(output) as staging:
        shutil.copytree(source, staging, dirs_exist_ok=True)
        g2p = Path(companions) / 'g2p/sources/g2p_chinese_model/poly_bert_model.onnx'
        if g2p.is_file(): shutil.copyfile(g2p, Path(staging) / 'g2p/sources/g2p_chinese_model/poly_bert_model.onnx')
        sys.path[:0] = [str(Path(__file__).parent), staging, overlay]
        for key in ('HF_HUB_OFFLINE', 'TRANSFORMERS_OFFLINE', 'HF_DATASETS_OFFLINE'): os.environ[key] = '1'
        import torch
        import numpy as np
        import soundfile as sf
        if device.startswith('cuda'):
            torch.cuda.set_device(device); name = torch.cuda.get_device_name(device)
        elif device.startswith('xpu'):
            torch.xpu.set_device(device); name = torch.xpu.get_device_name(device)
        else: name = 'CPU'
        print(f'[Hardware] {device}; {name}; torch={torch.__version__}', flush=True)
        install_local_loaders(model, companions)
        from diffrhythm2 import utils as api
        from music_diffrhythm_frontend import ExperimentalFrontend
        factory = api.CNENTokenizer
        tokenizer = ExperimentalFrontend(staging, factory, request['experimental_ru'])
        api.CNENTokenizer = lambda: tokenizer
        if mode == 'probe':
            tokenizer.encode('Текст и музыку достань, и машине волю дай!')
            # Import exercises ABI; G2P and English also checked without loading
            # the song model. The prepared Chinese ONNX stays on CPU.
            tokenizer.encode('Music in the night.')
            Path(output).write_text('DiffRhythm API ready', encoding='utf-8')
            receipt = dict(check='imports-and-frontend', hardware=device, source=request['source_revision'], frontend=tokenizer.rows)
        else:
            started = time.monotonic()
            blocks = api.parse_lyrics(tokenizer, request['lyrics'])
            tokens = [token for line in blocks for token in line]
            if not tokens: raise ValueError('Empty lyrics after preprocessing.')
            print(f'[AR] Lyrics: {len(tokens)} tokens; {len(tokenizer.rows)} lines; duration ceiling={request["duration"]}s', flush=True)
            for warning in request['warnings']: print('[Request] ' + warning, flush=True)
            random.seed(request['seed']); np.random.seed(request['seed']); torch.manual_seed(request['seed'])
            dtype = torch.float32 if device == 'cpu' else torch.float16
            print('[Load] Loading DiffRhythm, MuQ and BigVGAN', flush=True)
            song, mulan, _, decoder = api.prepare_model('ASLP-lab/DiffRhythm2', torch.device(device), dtype)
            for module in (song, mulan, decoder): module.eval()
            # Guard XLM input length explicitly. Author forward does not truncate,
            # but would otherwise fail after the large models have loaded.
            text_model = mulan.mulan_module.text
            if hasattr(text_model, 'tokenizer'):
                count = len(text_model.tokenizer.encode(request['style']))
                limit = min(512, int(text_model.tokenizer.model_max_length))
                if count > limit: raise ValueError(f'Style exceeds {limit} XLM tokens ({count}). Shorten the wishes.')
            print('[Plan] Encoding style with MuQ', flush=True)
            style = api.get_text_prompt(mulan, request['style'], torch.device(device), dtype)
            # Read-only progress hook around the author transformer. No sampler
            # calculations or generated tensors are changed by the hook.
            tick = [0, 0.0]
            def progress(_module, _args, _result):
                tick[0] += 1; now = time.monotonic()
                if now - tick[1] >= 2:
                    tick[1] = now; print(f'[Synth] Model evaluations={tick[0]}; elapsed={now-started:.1f}s', flush=True)
            hook = song.transformer.register_forward_hook(progress)
            try:
                print('[Synth] Block flow matching', flush=True)
                rate, audio = api.inference(song, decoder, torch.tensor(tokens, dtype=torch.long, device=device), style,
                    request['duration'], cfg_strength=request['cfg'], sample_steps=request['steps'],
                    fake_stereo=request['fake_stereo'], odeint_method=request['solver'], file_type='wav')
            finally: hook.remove()
            if not np.isfinite(audio).all() or audio.size == 0: raise ValueError('Invalid generated audio.')
            sf.write(output, audio, rate, format='WAV', subtype='PCM_16')
            receipt = dict(request=request, frontend=tokenizer.rows, hardware=device, precision=str(dtype), sample_rate=rate,
                samples=len(audio), duration=len(audio)/rate, source=request['source_revision'], elapsed=time.monotonic()-started)
            print('[Done] DiffRhythm audio and effective request saved.', flush=True)
        Path(output + '.receipt.json').write_text(json.dumps(receipt, ensure_ascii=False), encoding='utf-8')


if __name__ == '__main__':
    main()
