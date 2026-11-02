"""The pinned WanVideoWrapper nodes with a window-boundary persistence hook.

Diffusion, audio conditioning, temporal overlap and decoding remain upstream.
The derived function only restores/commits boundary state and reports progress.
No installed source file is rewritten and no server is started.
"""
import ast
from datetime import datetime, timezone
import gc
import hashlib
import importlib
import inspect
import json
import os
from pathlib import Path
import subprocess
import secrets
import sys
import types

from song_clip_motion_state import contained, file_hash, reject_links
from song_clip_infinitetalk_state import LOOP_SHA256

COUNTERS = ('is_first_clip', 'arrive_last_frame', 'cur_motion_frames_num',
            'audio_start_idx', 'audio_end_idx', 'iteration_count',
            'step_iteration_count', 'current_condframe_index', 'img_counter')


class HeadlessEvents:
    """Only the upstream preview/status transport surface, without a server.

    WanVideoWrapper imports PromptServer.instance even with previews disabled.
    This worker has no browser client, event loop, queue or HTTP listener.
    Native progress/accepted windows are reported by WindowCheckpoint instead.
    """
    client_id = None
    last_node_id = 'infinitetalk'
    supports_preview_metadata = False

    def __init__(self, emit):
        self.emit = emit

    def send_sync(self, event, data, sid=None):
        # Binary preview frames are deliberately not decoded, cached or sent.
        if isinstance(data, str):
            self.emit('native-status', event=str(event), message=data[:2000])
        elif isinstance(data, dict):
            safe = {key: value for key, value in data.items()
                    if isinstance(key, str) and type(value) in (str, int, float, bool, type(None))}
            self.emit('native-status', event=str(event), data=safe)

    def send_progress_text(self, text, node_id=None, **kwargs):
        self.send_sync('progress_text', text)


def install_headless_events(emit):
    import server
    from comfy.cli_args import args, LatentPreviewMethod
    # The author's native NoPreviews option avoids allocating preview decoders
    # and asynchronous VHS preview threads in a worker with no browser client.
    args.preview_method = LatentPreviewMethod.NoPreviews
    current = getattr(server.PromptServer, 'instance', None)
    if current is not None and not isinstance(current, HeadlessEvents):
        raise ValueError('Unexpected live ComfyUI server inside an owned InfiniteTalk worker.')
    bridge = HeadlessEvents(emit)
    server.PromptServer.instance = bridge
    return bridge


def enable_wav2vec_hidden_states(model):
    """Use HF's own output capture for its newer encoder API, on this model only.

    Earlier encoders collect hidden_states themselves. Newer Transformers move
    that task to a decorator on the parent model's forward; Wrapper overrides
    forward for frame interpolation and therefore needs the same decorator.
    No encoder layer, weight, feature interpolation or numerical op is replaced.
    """
    if 'output_hidden_states' in inspect.signature(model.encoder.forward).parameters:
        return False
    if getattr(model, '_lopata_hidden_states_capture', False):
        return True
    from transformers.utils.output_capturing import capture_outputs
    forward = capture_outputs(tie_last_hidden_states=False)(type(model).forward)
    model.forward = types.MethodType(forward, model)
    model._lopata_hidden_states_capture = True
    return True


def hooked_source(source):
    """Fail closed when the exact reviewed loop or one of its anchors changes."""
    if hashlib.sha256(source).hexdigest() != LOOP_SHA256:
        raise ValueError('InfiniteTalk loop differs from the supported source revision.')
    text = source.decode('utf-8')
    node = next(n for n in ast.parse(text).body if isinstance(n, ast.FunctionDef) and n.name == 'multitalk_loop')
    text = '\n'.join(text.splitlines()[node.lineno - 1:node.end_lineno]) + '\n'
    start = '    while True: # start video generation iteratively\n'
    restore = ('    restored = _lopata_checkpoint.restore(seed_g, audio_embedding, original_images, device, dtype)\n'
               '    if restored is not None:\n'
               + ''.join('        ' + key + ' = restored["' + key + '"]\n' for key in COUNTERS)
               + '        cond_frame = restored["cond_frame"]\n'
               '        original_images = restored["original_images"]\n'
               '        audio_embedding = restored["audio_embedding"]\n')
    final = '        if arrive_last_frame:\n            break\n'
    finish = '    if not output_path:\n        gen_video_samples = torch.cat(gen_video_list, dim=1)\n'
    step = '            step_iteration_count += 1\n'
    replacements = [(start, restore + start + '        _lopata_checkpoint.begin(iteration_count)\n'),
                    (final, '        if arrive_last_frame:\n            _lopata_checkpoint.commit(locals(), final=True)\n            break\n'),
                    (finish, '        _lopata_checkpoint.commit(locals(), final=False)\n\n' + finish),
                    (step, step + '            _lopata_checkpoint.step(iteration_count, i + 1, len(timesteps) - 1)\n')]
    for anchor, replacement in replacements:
        if text.count(anchor) != 1:
            raise ValueError('InfiniteTalk checkpoint hook anchor changed.')
        text = text.replace(anchor, replacement)
    ast.parse(text)
    return text


def install_hook(loop_module, sampler_module, checkpoint):
    path = Path(loop_module.__file__)
    reject_links(path)
    source = hooked_source(path.read_bytes())
    namespace = dict(vars(loop_module), _lopata_checkpoint=checkpoint)
    exec(compile(source, str(path) + ':LOPATA-window-checkpoint', 'exec'), namespace)
    sampler_module.multitalk_loop = namespace['multitalk_loop']
    return hashlib.sha256(source.encode('utf-8')).hexdigest()


class WindowCheckpoint:
    def __init__(self, state, torch, emit, device, provenance):
        self.state, self.torch, self.emit, self.device = state, torch, emit, device
        self.provenance = provenance

    def begin(self, index):
        self.emit('stage', stage='infinitetalk-window', index=index + 1, total=len(self.state.plan),
                  fraction=.08 + .84 * index / len(self.state.plan))

    def step(self, index, step, steps):
        self.emit('progress', index=index + 1, total=len(self.state.plan), step=step, steps=steps,
                  fraction=.08 + .84 * (index + min(step / max(steps, 1), 1)) / len(self.state.plan))

    def restore(self, generator, audio, original, device, dtype):
        latest = self.state.latest
        if not latest:
            return None
        if latest['final']:
            raise ValueError('Completed InfiniteTalk windows must not enter the sampler again.')
        from safetensors.torch import load_file
        tensors = load_file(str(contained(self.state.receipts, f'{latest["index"]:06d}.safetensors')), device='cpu')
        required = {'seed_rng', 'cpu_rng', 'cond_frame'}
        if not required.issubset(tensors):
            raise ValueError('InfiniteTalk checkpoint omits RNG or motion context.')
        cond = tensors['cond_frame']
        if tuple(cond.shape) != (1, 3, 9, original.shape[-2], original.shape[-1]):
            raise ValueError('InfiniteTalk checkpoint motion context has the wrong shape.')
        restored = dict(latest['counters'])
        # Native decoded videos/next cond_frame live on CPU. Preserve the exact
        # saved dtype too; converting float32 pixels to model BF16 here would
        # change the accepted handoff even with an identical RNG state.
        restored['cond_frame'] = cond
        restored['original_images'] = original[:, :, :1].repeat(1, 1, restored['current_condframe_index'] + 1, 1, 1)
        restored_audio = list(audio)
        for index, feature in enumerate(restored_audio):
            padding = tensors.get('audio_padding_' + str(index))
            if padding is not None:
                if padding.ndim != feature.ndim or tuple(padding.shape[1:]) != tuple(feature.shape[1:]) or padding.shape[0] > 84:
                    raise ValueError('Invalid InfiniteTalk checkpoint audio boundary.')
                restored_audio[index] = self.torch.cat([feature, padding.to(feature)], dim=0)
        restored['audio_embedding'] = restored_audio
        generator.set_state(tensors['seed_rng'])
        self.torch.set_rng_state(tensors['cpu_rng'])
        backend = getattr(self.torch, device.type, None)
        if device.type != 'cpu' and backend is not None and hasattr(backend, 'set_rng_state'):
            if 'device_rng' not in tensors:
                raise ValueError('InfiniteTalk checkpoint omits the accelerator RNG state.')
            backend.set_rng_state(tensors['device_rng'], device)
        self.emit('cached', windows=latest['index'], frames=latest['frameCount'], fraction=.08 + .84 * latest['index'] / len(self.state.plan))
        return restored

    def commit(self, values, final):
        from safetensors.torch import save_file
        torch = self.torch
        tensors = {'seed_rng': values['seed_g'].get_state().clone().cpu(),
                   'cpu_rng': torch.get_rng_state().clone().cpu()}
        device = self.device
        if values.get('cond_frame') is not None:
            tensors['cond_frame'] = values['cond_frame'].detach().cpu().contiguous().clone()
        backend = getattr(torch, device.type, None)
        if device.type != 'cpu' and backend is not None and hasattr(backend, 'get_rng_state'):
            tensors['device_rng'] = backend.get_rng_state(device).clone().cpu()
        for index, feature in enumerate(values['audio_embedding']):
            if len(feature) > self.state.total_frames:
                tensors['audio_padding_' + str(index)] = feature[self.state.total_frames:].detach().cpu().contiguous().clone()
        counters = {key: values[key] for key in COUNTERS}
        self.stamp_frames(counters['img_counter'])
        receipt = self.state.commit(counters, final, lambda path: save_file(tensors, str(path)))
        self.emit('chunk', index=receipt['index'], total=len(self.state.plan), frames=receipt['frameCount'],
                  final=final, fraction=.08 + .84 * receipt['index'] / len(self.state.plan))

    def stamp_frames(self, frame_count):
        from PIL import Image, PngImagePlugin
        facts = dict(self.provenance, createdUtc=datetime.now(timezone.utc).isoformat())
        metadata = PngImagePlugin.PngInfo()
        metadata.add_text('Software', 'LOPATA ' + str(facts['programVersion']))
        metadata.add_itxt('LOPATA_VIDEO', json.dumps(facts, ensure_ascii=False, separators=(',', ':')))
        for index in range(self.state.frame_count, frame_count):
            path = contained(self.state.frames, f'frame_{index:05d}.png')
            pending = contained(self.state.frames, path.name + '.' + secrets.token_hex(8) + '.tmp')
            try:
                with Image.open(path) as image:
                    image.save(pending, format='PNG', pnginfo=metadata)
                os.replace(pending, path)
            finally:
                if pending.exists():
                    pending.unlink()


class NativeInfiniteTalk:
    def __init__(self, request, state, emit):
        self.request, self.state, self.emit = request, state, emit
        sys.path[:0] = [p for p in (request.get('extras'), request['libraries'], request['comfy']) if p]
        os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
        sys.argv = ['lopata-infinitetalk', '--offline', '--disable-auto-launch', '--disable-dynamic-vram', '--reserve-vram',
                    str(request['vramReserveGb']), '--use-pytorch-cross-attention']
        device = request['device']
        if device == 'cpu':
            sys.argv.append('--cpu')
        elif device.startswith('cuda:'):
            sys.argv += ['--cuda-device', device.split(':')[1]]
        import comfy.options
        comfy.options.enable_args_parsing()
        from h3_memory import initialize_host_memory, initialize_memory
        initialize_host_memory({'MemoryMode': 'auto', 'VramReserveGb': request['vramReserveGb']})
        import torch
        selected = torch.device(device)
        backend = getattr(torch, selected.type, None)
        if selected.index is not None and hasattr(backend, 'set_device'):
            backend.set_device(selected.index)
        import folder_paths
        import nodes
        import comfy.model_management as memory
        initialize_memory({'MemoryMode': 'auto', 'VramReserveGb': request['vramReserveGb']}, emit)
        actual = memory.get_torch_device()
        if actual.type != selected.type or selected.index is not None and (actual.index or 0) != selected.index:
            raise ValueError('Requested device unavailable; implicit CPU fallback is forbidden.')
        self.torch, self.nodes, self.memory, self.device = torch, nodes, memory, actual
        emit('hardware', device=str(actual), torch=torch.__version__)
        folder_paths.models_dir = str(contained(state.root, 'native-models'))
        for kind in ('input', 'output', 'temp'):
            directory = contained(state.root, 'native-' + kind)
            directory.mkdir(exist_ok=True)
            getattr(folder_paths, 'set_' + kind + '_directory')(str(directory))
        for category, directories in request['modelFolders'].items():
            for directory in directories:
                reject_links(directory)
                folder_paths.add_model_folder_path(category, directory, is_default=True)
                if category == 'diffusion_models':
                    folder_paths.add_model_folder_path('unet_gguf', directory, is_default=True)
        self.paths = folder_paths
        self.headless_events = install_headless_events(emit)
        # Namespace-only package loading avoids importing unrelated optional
        # author workflows. The exact required modules still load unchanged.
        package = types.ModuleType('lopata_wan_wrapper')
        package.__path__ = [request['wrapper']]
        package.__file__ = str(Path(request['wrapper']) / '__init__.py')
        sys.modules[package.__name__] = package
        self.loading = importlib.import_module(package.__name__ + '.nodes_model_loading')
        self.wan_nodes = importlib.import_module(package.__name__ + '.nodes')
        self.talk = importlib.import_module(package.__name__ + '.multitalk.nodes')
        self.sampling = importlib.import_module(package.__name__ + '.nodes_sampler')
        loop = importlib.import_module(package.__name__ + '.multitalk.multitalk_loop')
        provenance = dict(program='LOPATA', programVersion=request['programVersion'],
            scenario='song-to-clip/infinitetalk', operation=request.get('operationId', state.root.name),
            models=request['pins'], nativeSourceRevision=request['sourceRevision'])
        self.checkpoint = WindowCheckpoint(state, torch, emit, actual, provenance)
        self.hook_sha256 = install_hook(loop, self.sampling, self.checkpoint)
        self.verify_model_paths()

    def verify_model_paths(self):
        for key, category in [('model', 'diffusion_models'), ('adapter', 'diffusion_models'),
                              ('encoder', 'text_encoders'), ('vae', 'vae'), ('vision', 'clip_vision'), ('wav2vec', 'wav2vec2')]:
            resolved = self.paths.get_full_path_or_raise(category, self.request[key])
            allowed = [contained(root, self.request[key]).resolve() for root in self.request['modelFolders'][category]]
            reject_links(resolved)
            if Path(resolved).resolve() not in allowed:
                raise ValueError('A native loader resolved outside the audited installation: ' + key)

    def retire(self):
        self.memory.unload_all_models()
        gc.collect()
        self.memory.soft_empty_cache()

    def image(self):
        import numpy as np
        from PIL import Image, ImageOps
        with Image.open(self.request['reference']) as source:
            image = ImageOps.exif_transpose(source).convert('RGB')
            image.thumbnail((2048, 2048))
            return self.torch.from_numpy(np.asarray(image).astype('float32') / 255)[None]

    def text(self, prompts):
        # Native Comfy Wan T5 supports the existing scaled FP8 file; Wrapper's
        # own T5 loader explicitly rejects that format. Its documented bridge
        # preserves the exact conditioning layout expected by native sampling.
        clip = self.nodes.CLIPLoader().load_clip(self.request['encoder'], 'wan', 'default')[0]
        negative = self.nodes.CLIPTextEncode().encode(clip, 'low quality, distorted face, multiple panels, text, watermark')[0]
        outputs = []
        for prompt in prompts:
            positive = self.nodes.CLIPTextEncode().encode(clip, prompt)[0]
            bridge = self.wan_nodes.WanVideoTextEmbedBridge().process(positive, negative)[0]
            outputs.append(bridge['prompt_embeds'][0].detach())
        result = dict(prompt_embeds=outputs,
                      negative_prompt_embeds=[bridge['negative_prompt_embeds'][0].detach()])
        clip = negative = positive = bridge = None
        self.retire()
        return result

    def audio(self):
        import numpy as np
        request = self.request
        result = subprocess.run([request['ffmpeg'], '-v', 'error', '-i', request['song'], '-vn',
            '-ac', '1', '-ar', '16000', '-f', 'f32le', 'pipe:1'], capture_output=True, check=True,
            creationflags=getattr(subprocess, 'CREATE_NO_WINDOW', 0))
        samples = np.frombuffer(result.stdout, dtype='<f4').copy()
        if not len(samples) or not np.isfinite(samples).all():
            raise ValueError('The song could not be decoded for InfiniteTalk conditioning.')
        waveform = self.torch.from_numpy(samples)[None, None]
        audio_precision = 'fp16' if self.memory.should_use_fp16(device=self.device) else 'fp32'
        wav2vec = self.talk.Wav2VecModelLoader().loadmodel(request['wav2vec'], audio_precision, 'offload_device')[0]
        if enable_wav2vec_hidden_states(wav2vec['model']):
            self.emit('compatibility', component='wav2vec2', adapter='native-hf-hidden-state-capture')
        output = self.talk.MultiTalkWav2VecEmbeds().process(wav2vec, True, 25.0, self.state.total_frames,
            dict(waveform=waveform, sample_rate=16000), 1.0, float(request['audioCfg']), 'para')[0]
        features = output['audio_features']
        if len(features) != 1 or not 0 < len(features[0]) <= self.state.total_frames:
            raise ValueError('Invalid native InfiniteTalk audio features.')
        # Upstream floors the audio frame count. Repeat at most the final frame
        # so the very last fraction of the original song is also represented.
        missing = self.state.total_frames - len(features[0])
        if missing > 2:
            raise ValueError('Native audio conditioning does not cover the full song.')
        if missing:
            features[0] = self.torch.cat([features[0], features[0][-1:].repeat(missing, 1, 1)], dim=0)
        wav2vec = waveform = samples = result = None
        self.retire()
        return output

    def run(self, prompts):
        torch, request = self.torch, self.request
        torch.manual_seed(self.state.seed)
        self.emit('stage', stage='infinitetalk-audio', fraction=.03)
        with torch.inference_mode():
            audio = self.audio()
            self.emit('stage', stage='infinitetalk-text', fraction=.05)
            text = self.text(prompts)
            reference = self.image()
            vision = self.nodes.CLIPVisionLoader().load_clip(request['vision'])[0]
            vision_context = self.wan_nodes.WanVideoClipVisionEncode().process(
                vision, reference, 1.0, 1.0, True, 'center', 'average')[0]
            vision = None
            self.retire()
            precision = 'bf16' if self.memory.should_use_bf16(device=self.device) else (
                'fp16' if self.memory.should_use_fp16(device=self.device) else 'fp32')
            vae = self.loading.WanVideoVAELoader().loadmodel(request['vae'], precision)[0]
            image = self.talk.WanVideoImageToVideoMultiTalk().process(vae, request['width'], request['height'],
                81, 9, True, 'disabled', start_image=reference, tiled_vae=True,
                clip_embeds=vision_context, mode='infinitetalk', output_path='')[0]
            image['output_path'] = str(self.state.frames)
            adapter = self.talk.MultiTalkModelLoader().loadmodel(request['adapter'])[0]
            # Native GGUF weights plus conservative author block swapping on
            # smaller devices. No custom quantizer, attention or sampler.
            total_gb = self.memory.get_total_memory(self.device) / 1024**3
            free_gb = self.memory.get_free_memory(self.device) / 1024**3
            usable_gb = min(total_gb, free_gb) - request['vramReserveGb']
            swap_count = 0 if self.device.type == 'cpu' or usable_gb >= 20 else 20
            self.emit('hardware', device=str(self.device), freeGb=free_gb,
                      reserveGb=request['vramReserveGb'], blocksToSwap=swap_count)
            swap = self.loading.WanVideoBlockSwap().setargs(blocks_to_swap=swap_count,
                offload_img_emb=False, offload_txt_emb=False, use_non_blocking=False,
                vace_blocks_to_swap=0, prefetch_blocks=0, block_swap_debug=False)[0]
            model = self.loading.WanVideoModelLoader().loadmodel(request['model'], precision, 'offload_device',
                'disabled', attention_mode='sdpa', block_swap_args=swap, multitalk_model=adapter)[0]
            # Official InfiniteTalk no-LoRA default for the pinned 480p model.
            self.sampling.WanVideoSampler().process(model=model, image_embeds=image, shift=7.0,
                steps=request['steps'], cfg=5.0, seed=self.state.seed, scheduler='unipc',
                riflex_freq_index=0, text_embeds=text, force_offload=True, multitalk_embeds=audio)
        self.retire()
        if not self.state.done:
            raise ValueError('InfiniteTalk returned without committing the entire song.')
