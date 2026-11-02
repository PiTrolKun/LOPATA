"""Execute only the exact card and FL2VA subgraphs returned by audited Geekatplay.

Comfy's model loaders/samplers are unchanged. LOPATA owns sequential disk commits,
memory retirement, provenance and the final soundtrack, rather than a UI queue.
"""
import gc
import importlib.util
import os
from pathlib import Path
import sys
from song_clip_motion_state import contained, reject_links


def load_author(request):
    path = Path(request['geekSource']) / 'nodes.py'
    reject_links(path)
    spec = importlib.util.spec_from_file_location('lopata_geek_nodes', str(path))
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module


class NativeGeek:
    def __init__(self, request, job, emit):
        self.request, self.job, self.emit = request, job, emit
        sys.path[:0] = [request['libraries'], request['comfy']]
        os.environ.update(HF_HUB_OFFLINE='1', TRANSFORMERS_OFFLINE='1', HF_HUB_DISABLE_TELEMETRY='1')
        device = request['device']
        sys.argv = ['lopata-geek', '--offline', '--disable-auto-launch', '--reserve-vram', '2', '--use-pytorch-cross-attention']
        if device == 'cpu': sys.argv.append('--cpu')
        elif device.startswith('cuda:'): sys.argv += ['--cuda-device', device.split(':')[1]]
        import comfy.options
        comfy.options.enable_args_parsing()
        from h3_memory import initialize_host_memory, initialize_memory
        initialize_host_memory({'MemoryMode': 'auto', 'VramReserveGb': 2})
        import torch
        selected = torch.device(device)
        backend = getattr(torch, selected.type, None)
        if selected.index is not None and hasattr(backend, 'set_device'): backend.set_device(selected.index)
        import folder_paths
        import nodes
        import comfy.model_management as memory
        initialize_memory({'MemoryMode': 'auto', 'VramReserveGb': 2}, emit)
        actual = memory.get_torch_device()
        if actual.type != selected.type or selected.index is not None and (actual.index or 0) != selected.index:
            raise ValueError('Requested device unavailable; implicit fallback is forbidden.')
        emit('hardware', device=str(actual), torch=torch.__version__)
        self.torch, self.nodes, self.memory = torch, nodes, memory
        # Importing the author module registers no server. Its optional LLM folder
        # must be isolated too; no implicit LLM/download path is ever used.
        folder_paths.models_dir = str(contained(job, 'native-models'))
        for kind in ('input', 'output', 'temp'):
            directory = contained(job, 'native-' + kind); directory.mkdir(exist_ok=True)
            getattr(folder_paths, 'set_' + kind + '_directory')(str(directory))
        for category, directory in request['modelFolders'].items():
            reject_links(directory); folder_paths.add_model_folder_path(category, directory, is_default=True)
        self.paths = folder_paths
        self.author = load_author(request)
        from comfy_extras import nodes_custom_sampler, nodes_minimax_h3, nodes_images
        self.classes = dict(nodes.NODE_CLASS_MAPPINGS)
        for cls in ('KSamplerSelect', 'BasicScheduler', 'RandomNoise', 'BasicGuider', 'SamplerCustomAdvanced'):
            self.classes[cls] = getattr(nodes_custom_sampler, cls)
        self.classes['MiniMaxH3ImageToVideo'] = nodes_minimax_h3.MiniMaxH3ImageToVideo
        self.classes['ImageFromBatch'] = nodes_images.ImageFromBatch
        self.graph = {}; self.cache = {}
        self.phase, self.index, self.total = 'cards', 1, 1
        import comfy.utils
        comfy.utils.set_progress_bar_global_hook(lambda value, total, *a, **k:
            emit('progress', index=self.index, total=self.total, step=int(value), steps=int(total),
                 fraction=(.1 + .22 * ((self.index - 1 + value / max(total, 1)) / self.total)) if self.phase == 'cards'
                 else (.33 + .58 * ((self.index - 1 + value / max(total, 1)) / self.total))))

    def reset(self):
        self.graph.clear(); self.cache.clear()
        self.memory.unload_all_models(); gc.collect(); self.memory.soft_empty_cache()

    def image(self, path):
        import numpy as np
        from PIL import Image, ImageOps
        with Image.open(path) as source:
            image = ImageOps.exif_transpose(source).convert('RGB')
            image.thumbnail((2048, 2048))
            return self.torch.from_numpy(np.array(image).astype('float32') / 255)[None]

    def evaluate(self, id):
        if id in self.cache: return self.cache[id]
        node = self.graph[id]
        name = node['class_type']
        allowed = {'CLIPTextEncode', 'EmptyLatentImage', 'KSampler', 'VAEDecode', 'VAEEncode', 'ImageFromBatch',
                   'ImageScale', 'ImageBatch', 'KSamplerSelect', 'BasicScheduler', 'RandomNoise', 'BasicGuider',
                   'SamplerCustomAdvanced', 'MiniMaxH3ImageToVideo', 'SegmentNowRendering'}
        if name not in allowed: raise ValueError('Unapproved Geekatplay graph node: ' + name)
        def resolve(value):
            if isinstance(value, list) and len(value) == 2 and isinstance(value[0], str) and value[0] in self.graph:
                return self.evaluate(value[0])[value[1]]
            return value
        kwargs = {key: resolve(value) for key, value in node['inputs'].items()}
        if name == 'SegmentNowRendering':
            result = (kwargs['start_image'], kwargs['end_image'])
        else:
            cls = self.classes[name]
            if callable(getattr(cls, 'execute', None)): result = tuple(cls.execute(**kwargs))
            else: result = getattr(cls(), cls.FUNCTION)(**kwargs)
        self.cache[id] = result
        return result

    def card(self, prompt, reference, index, total):
        from comfy.diffusers_load import load_diffusers
        self.phase, self.index, self.total = 'cards', index + 1, total
        model, clip, vae = load_diffusers(self.request['sdxl'])
        refs = self.image(reference['file']) if reference else None
        plan = {'references': [{'id': 'ref', 'kind': reference['kind'], 'name': reference['description']}],
                'assignment': {'0': 'ref'}} if reference else None
        # One card at a time: same author subgraph, bounded RAM, durable resume.
        expanded = self.author.StoryboardCardsRender().render_all(False, model, clip, vae,
            __import__('json').dumps([prompt]), '{"card_count":1,"segments":[]}',
            self.request['width'], self.request['height'], self.request['cardSteps'], 6.0,
            'euler', 'normal', self.request['seed'] + index, 'text, watermark, low quality', 1,
            reference_images=refs, reference_plan=__import__('json').dumps(plan) if plan else '',
            reference_strength=self.request['referenceStrength'])
        self.graph = expanded['expand']; self.cache = {}
        with self.torch.no_grad():
            image = self.evaluate(expanded['result'][0][0])[0].detach().cpu()
        self.reset()
        return image

    def segment(self, first, last, prompt, seconds, index, total):
        import json
        for key, category in [('model', 'diffusion_models'), ('encoder', 'text_encoders'), ('videoVae', 'vae')]:
            expected = contained(self.request['modelFolders'][category], self.request[key])
            resolved = self.paths.get_full_path(category, self.request[key])
            if not resolved or Path(resolved).resolve() != expected.resolve():
                raise ValueError('Model loader resolved outside the audited installation.')
        self.phase, self.index, self.total = 'video', index + 1, total
        model = self.nodes.UNETLoader().load_unet(self.request['model'], 'default')[0]
        from comfy_extras.nodes_minimax_h3 import MiniMaxH3SigmaShift
        model = MiniMaxH3SigmaShift.execute(model, 12.0, 3.0)[0]
        clip = self.nodes.CLIPLoader().load_clip(self.request['encoder'], 'minimax', 'default')[0]
        vae = self.nodes.VAELoader().load_vae(self.request['videoVae'])[0]
        images = self.torch.cat([self.image(first), self.image(last)], dim=0)
        board = json.dumps({'card_count': 2, 'segments': [dict(segment_index=0, start_time=0, end_time=seconds, duration=seconds, lyrics='')]})
        # First is the previous accepted handoff (or card zero); last is the
        # next card. This reproduces upstream seamless_chain across disk queues.
        expanded = self.author.MiniMaxSegmentsRender().render_segments(False, model, clip, vae, None,
            images, json.dumps([prompt]), board, str(contained(self.job, 'graph-only')),
            self.request['width'], self.request['height'], 24, self.request['seed'] + index,
            self.request['steps'], 'res_multistep', 'simple', max_segments=1,
            segments_per_queue=1, resume=False, seamless_chain=True)
        self.graph = expanded['expand']; self.cache = {}
        sample_id = next(key for key, value in self.graph.items() if value['class_type'] == 'SamplerCustomAdvanced')
        with self.torch.no_grad():
            latent = self.evaluate(sample_id)[0]
            # No live model/conditioning graph may occupy VRAM during decoding.
            self.graph.clear(); self.cache.clear(); model = clip = images = None
            self.memory.unload_all_models(); gc.collect(); self.memory.soft_empty_cache()
            # Official decoder accepts the joint H3 latent and selects its video.
            decoded = self.nodes.VAEDecode().decode(vae, latent)[0].detach().cpu()
        self.reset()
        return decoded
