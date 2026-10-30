"""Adapter to the pinned ComfyUI/obvpm nodes, without a web server or a custom sampler."""
import importlib.util
import json
import math
import os
from pathlib import Path
import sys
import copy
import gc
import time


def load_package(name, directory):
    spec = importlib.util.spec_from_file_location(name, str(Path(directory) / "__init__.py"),
                                                submodule_search_locations=[str(directory)])
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def raw_pixel_lineage(pins, pinned_head):
    """Keep the native sidecar contract in raw-parent frame coordinates."""
    for pin in pins:
        spec = pin["spec"]
        spec["source_start"] = int(spec["source_start"]) + pinned_head


def continuity_window(requested, available, mode):
    """Prefer an exact AV mask; short cuts use the native guide ladder."""
    if available < 5:
        # Pinned upstream explicitly reserves its 1-frame path for future use.
        raise ValueError("VideoPrep.H3ContextTooShort")
    if mode != "guide":
        shared = [n for n in (39, 90, 141) if n <= min(requested, available)]
        if shared:
            return max(shared), mode
    ladder = [n for n in (5, 22, 39, 56, 73, 90, 107, 124, 141) if n <= min(requested, available)]
    return max(ladder), "guide"


class NativeH3:
    def __init__(self, request, emit):
        self.request, self.emit = request, emit
        self.lite = request.get("ModelId") == "minimax-h3-w4a8"
        self.settings = s = request["Settings"]
        env = request["Environment"]
        sys.path.insert(0, env["Libraries"])
        sys.path.insert(0, env["Comfy"])
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["OBVPM_FFMPEG_PATH"] = request["Ffmpeg"]
        sys.argv = ["lopata-h3", "--offline", "--disable-auto-launch", "--reserve-vram", str(s["VramReserveGb"])]
        device = env["Device"]
        if device == "cpu":
            sys.argv.append("--cpu")
        elif device.startswith("cuda:"):
            sys.argv.extend(["--cuda-device", device.split(":")[1]])
        if s["MemoryMode"] == "gpu":
            sys.argv.extend(["--disable-dynamic-vram", "--highvram"] if self.lite else ["--gpu-only"])
        elif self.lite:
            sys.argv.append("--disable-dynamic-vram")
            if s["MemoryMode"] == "offload":
                sys.argv.append("--lowvram")
        elif s["MemoryMode"] == "offload":
            sys.argv.extend(["--disable-dynamic-vram", "--lowvram"])
        if s["Attention"] != "pytorch":
            raise ValueError("VideoPrep.H3AccelerationUnavailable")
        sys.argv.append("--use-pytorch-cross-attention")
        import comfy.options
        comfy.options.enable_args_parsing()
        from h3_memory import initialize_host_memory
        initialize_host_memory(s)
        import torch
        import folder_paths
        if s["MemoryMode"] == "gpu":
            if device == "cpu":
                raise ValueError("VideoPrep.H3GpuRequired")
            backend = getattr(torch, device.split(":")[0], None)
            if backend is not None and hasattr(backend, "mem_get_info"):
                free, total = backend.mem_get_info(torch.device(device))
                names = request["Models"]
                weights = [("diffusion_models", names["Diffusion"]), ("text_encoders", names["Encoder"]),
                           ("vae", names["VideoVae"]), ("vae", names["AudioVae"])]
                sizes = [(Path(request["ModelFolders"][category]) / name).stat().st_size for category, name in weights]
                required = max(sizes) if self.lite else sum(sizes)
                required += max(2, s["VramReserveGb"]) * 1024**3
                if required > total:
                    raise ValueError("VideoPrep.H3GpuCapacity")
                if required > free:
                    raise ValueError("VideoPrep.H3GpuBusy")
        import nodes
        import comfy.utils
        from h3_memory import initialize_memory
        self.memory_mode = initialize_memory(s, emit)
        self.torch, self.paths, self.nodes = torch, folder_paths, nodes
        folder_paths.set_output_directory(request["Output"])
        folder_paths.set_input_directory(request["Input"])
        folder_paths.set_temp_directory(request["Temp"])
        for category, directory in request["ModelFolders"].items():
            folder_paths.add_model_folder_path(category, directory)
        self.clipproj = load_package("lopata_h3_clipproj", env["ClipProj"]) if self.lite else None
        self.timeline = load_package("lopata_h3_timeline", env["Timeline"])
        from comfy_extras.nodes_minimax_h3 import MiniMaxH3ReferenceToVideo, MiniMaxH3SigmaShift
        from comfy_extras.nodes_lt import LTXVSeparateAVLatent, LTXVConcatAVLatent
        from comfy_extras.nodes_audio import VAEDecodeAudio
        self.encode, self.shift, self.separate, self.audio = MiniMaxH3ReferenceToVideo, MiniMaxH3SigmaShift, LTXVSeparateAVLatent, VAEDecodeAudio
        self.concat = LTXVConcatAVLatent
        # Provenance is written before the native saver hashes the MP4 for its mctx pairing.
        from lopata_h3_timeline import nodes_save
        original_tags = nodes_save._workflow_tags
        def tags(blobs):
            provenance = dict(request["Provenance"])
            elapsed = None
            if request.get("ElapsedBeforeAttempt") is not None and "_AttemptStartedMonotonic" in request:
                elapsed = request["ElapsedBeforeAttempt"] + time.monotonic() - request["_AttemptStartedMonotonic"]
                provenance["GenerationSeconds"] = elapsed
            values = {**original_tags(blobs), "encoded_by": "LOPATA " + request["Provenance"]["ProgramVersion"],
                    "LOPATA_VIDEO": json.dumps(provenance, ensure_ascii=False),
                    "LOPATA_OPERATION": request["Id"]}
            if elapsed is not None:
                values["LOPATA_GENERATION_SECONDS"] = format(elapsed, ".3f")
            return values
        nodes_save._workflow_tags = tags
        # The shared LGPL editor binary intentionally has no libx264. Use the
        # upstream-supported PyAV encoder from the pinned overlay, not PATH tools.
        nodes_save._ffmpeg_exe = lambda: None
        original_encode = nodes_save._encode_with_pyav
        def encode_video(*args, metadata=None, **kwargs):
            values = dict(metadata or {})
            if "LOPATA_VIDEO" in values:
                # Core JSON-serializes metadata values itself. Pass the object,
                # otherwise its MP4 tag would contain a quoted JSON string.
                values["LOPATA_VIDEO"] = json.loads(values["LOPATA_VIDEO"])
            return original_encode(*args, metadata=values, **kwargs)
        nodes_save._encode_with_pyav = encode_video
        from lopata_h3_timeline import mctx, condstore
        write_sidecar, save_conditioning = mctx.write_sidecar, condstore.save_conditioning
        def sidecar(path, video, audio, meta, blobs=None):
            return write_sidecar(path, video, audio, {**meta, **tags({})}, blobs)
        def conditioning_file(path, cond, meta=None):
            return save_conditioning(path, cond, {**(meta or {}), **tags({})})
        mctx.write_sidecar, condstore.save_conditioning = sidecar, conditioning_file
        self.classes = self.timeline.NODE_CLASS_MAPPINGS
        comfy.utils.set_progress_bar_global_hook(lambda value, total, preview=None, *args, **kwargs:
            emit("progress", value=int(value), total=int(total)))
        self.clip = self.model = self.video_vae = self.audio_vae = None
        self._conditioned = self._condition_key = None
        emit("hardware", device=str(torch.device(device)), torch=torch.__version__,
             available=str(__import__("comfy.model_management", fromlist=["get_torch_device"]).get_torch_device()))

    def load_clip(self):
        clip = self.nodes.CLIPLoader().load_clip(self.request["Models"]["Encoder"], "krea2" if self.lite else "minimax", "default")[0]
        if self.lite:
            clip = self.clipproj.NODE_CLASS_MAPPINGS["ClipProjApply"]().apply(clip, self.request["Models"]["Projection"])[0]
        return clip

    def resource_snapshot(self, stage):
        if not self.lite:
            return
        import psutil
        backend = getattr(self.torch, self.request["Environment"]["Device"].split(":")[0], None)
        info = psutil.Process().memory_info()
        values = dict(stage=stage, process_rss=info.rss, process_peak_rss=getattr(info, "peak_wset", info.rss))
        if backend is not None and hasattr(backend, "memory_allocated"):
            device = self.request["Environment"]["Device"]
            values.update(allocated=backend.memory_allocated(device), reserved=backend.memory_reserved(device),
                          peak=backend.max_memory_allocated(device))
        self.emit("resources", **values)

    def load(self, conditioning_only=False):
        n, s = self.nodes, self.settings
        self.emit("stage", text="loading-text-encoder")
        self.clip = self.load_clip()
        self.emit("stage", text="loading-vae")
        self.video_vae = n.VAELoader().load_vae(self.request["Models"]["VideoVae"])[0]
        self.audio_vae = n.VAELoader().load_vae(self.request["Models"]["AudioVae"])[0]
        if not conditioning_only:
            self.load_diffusion()

    def load_diffusion(self):
        n, s = self.nodes, self.settings
        self.emit("stage", text="loading-diffusion")
        model = n.UNETLoader().load_unet(self.request["Models"]["Diffusion"], "default")[0]
        if s["Turbo"] != "off" or s["ContentLoraFile"]:
            raise ValueError("VideoPrep.H3AccelerationUnavailable")
        if s["Spectrum"]:
            raise ValueError("VideoPrep.H3AccelerationUnavailable")
        model = self.shift.execute(model, s["VideoShift"], s["AudioShift"])[0]
        from h3_memory import sampling_headroom
        from lopata_h3_timeline.nodes_joint import _CLASSIC_SLACK_MB
        headroom = sampling_headroom(s, self.memory_mode, _CLASSIC_SLACK_MB)
        self.model = self.classes["H3VramHeadroom"]().patch(model, True, headroom, True)[0]
        if self.lite and s["MemoryMode"] == "gpu":
            import comfy.model_management as memory
            memory.load_models_gpu([self.model], force_full_load=True)
            self.resource_snapshot("diffusion-resident")

    def image_refs(self, paths):
        import numpy as np
        from PIL import Image, ImageOps
        refs = {}
        for i, path in enumerate(paths):
            with Image.open(path) as source:
                image = ImageOps.exif_transpose(source).convert("RGB")
                image.thumbnail((2048, 2048))
                refs["ref_image_" + str(i)] = self.torch.from_numpy(np.array(image).astype("float32") / 255)[None]
        return refs

    def generate(self, item, parent=None):
        if self.model is None:
            self.load(conditioning_only=True)
        s = self.settings
        seconds = max(5.0, min(15.0, s["ClipSeconds"]))
        frames = max(124, math.ceil((seconds * 24 - 5) / 17) * 17 + 5)
        self.emit("stage", text="conditioning")
        # Quantized native loaders register parameters during lazy materialization.
        # no_grad avoids inference tensors while retaining inference-only execution.
        with self.torch.no_grad():
            key = (item["Prompt"], tuple(item["Images"]), s["Width"], s["Height"], frames, s["ReferenceSize"])
            if key != self._condition_key:
                if self.clip is None:
                    self.clip = self.load_clip()
                self._conditioned = self.encode.execute(self.clip, item["Prompt"], s["Width"], s["Height"], frames,
                    s["ReferenceSize"], self.video_vae, self.audio_vae, ref_images=self.image_refs(item["Images"]))
                self._condition_key = key
                self.clip = None
                import comfy.model_management as memory
                memory.unload_all_models()
                gc.collect()
                memory.soft_empty_cache()
                if self.lite:
                    self.clipproj.NODE_CLASS_MAPPINGS["ClipProjFree"]().free("all models")
                    self.resource_snapshot("after-conditioning")
            conditioning, latent = copy.deepcopy(tuple(self._conditioned))
            if self.model is None:
                self.load_diffusion()
            pins = []
            if parent is not None:
                self.emit("stage", text="connecting-plans")
                if "_ParentEndFrame" in item:
                    # Adjacent plans often end at arbitrary 3/4/5 second cuts,
                    # outside the latent slice grid. The native pixel pin route
                    # re-encodes ONLY the window ending at that presented cut.
                    # It records the actual parent/hash in the saved sidecar.
                    window, mode = continuity_window(s["ContextFrames"], item["_ParentEndFrame"], s["PinMode"])
                    if mode != s["PinMode"]:
                        self.emit("stage", text="short-context-guide")
                    specs = [dict(source_kind="clip_pixels", source_path=parent,
                        take_from="at_frame", take_from_frame=item["_ParentEndFrame"],
                        requested_window=window, place="before", place_at_frame=0,
                        audio_window=min(s["AudioContextFrames"], window), mode=mode,
                        mask_ramp_frames=s["MaskRampFrames"], mask_ramp_edge=0.0,
                        mask_hold=s["MaskHold"], handover_edge=False)]
                    self.emit("continuity", parent=parent, end_frame=item["_ParentEndFrame"], grade="pixels", mode=mode, window=window)
                else:
                    mctx = self.classes["H3LoadMCtx"]().load(parent)[0]
                    specs = self.classes["H3MCtxPinSpec"]().build(mctx, str(s["ContextFrames"]), "tail", 0, "before", 0,
                        s["AudioContextFrames"], s["PinMode"], s["MaskRampFrames"], 0.0, s["MaskHold"])[0]
                    self.emit("continuity", parent=parent, grade="latents")
                conditioning, latent, pins = self.classes["H3MCtxApplyPins"]().apply(conditioning=conditioning,
                    latent=latent, snap_window_down_to_available=True, freeze_audio=False, pin_specs=specs,
                    video_vae=self.video_vae, audio_vae=self.audio_vae)
                if "_ParentEndFrame" in item:
                    # Pinned upstream pixel route records a delivered-file
                    # coordinate, whereas its saver/assembly lineage contract
                    # uses RAW parent coordinates. Translate the recipe after
                    # pixels have been sliced, not the requested decode cut.
                    from lopata_h3_timeline import mctx
                    from lopata_h3_timeline.nodes_load import resolve_clip_path
                    header = mctx.read_header(mctx.sidecar_path(resolve_clip_path(parent)))
                    offset = int(header.get("pinned_head_frames", 0) or 0)
                    raw_pixel_lineage(pins, offset)
            self.emit("stage", text="sampling")
            samples = self.nodes.KSampler().sample(self.model, item["Seed"], s["Steps"], 1.0,
                s["Sampler"], s["Scheduler"], conditioning, conditioning, latent, denoise=1.0)[0]
            if self.lite:
                self.resource_snapshot("sampled")
                import comfy.model_management as memory
                memory.unload_all_models()
                memory.soft_empty_cache()
            video, sound = self.separate.execute(samples)
            self.emit("stage", text="decoding")
            images = self.nodes.VAEDecodeTiled().decode(self.video_vae, video, tile_size=512, overlap=64,
                temporal_size=max(8, int(s["DecodeSeconds"] * 24)), temporal_overlap=8)[0]
            audio = self.audio.execute(self.audio_vae, sound)[0]
            path, _, _ = self.classes["H3TrimAndSaveVideoWithMCtx"]().trim_and_save(images, samples, "takes",
                item["Id"], s["Crf"], True, audio=audio if s["AudioMode"] == "native" else None, pins=pins,
                metadata=json.dumps({"seed":item["Seed"], "steps":s["Steps"], "lopata":self.request["Provenance"]}), conditioning=conditioning)
        return path

    def assemble(self, paths):
        from h3_assemble import assemble
        return assemble(self, paths)

    def upscale(self, paths, seed):
        import importlib
        self.request["Provenance"] = self.request["UpscaleProvenance"]
        if self.model is None:
            self.audio_vae = self.nodes.VAELoader().load_vae(self.request["Models"]["AudioVae"])[0]
            self.load_diffusion()
        self.emit("stage", text="upscaling")
        package = load_package("lopata_h3_upscaler", self.request["Environment"]["Upscaler"])
        up = importlib.import_module(package.__name__ + ".nodes.minimax_h3_latent_upscaler_3d")
        from h3_assemble import refinement_groups
        joint_class = self.classes["H3JointLatent"]
        joint = importlib.import_module(joint_class.__module__)
        original, layouts = [], []
        groups = refinement_groups(paths, joint)
        self.emit("refinement-segments", total=len(groups))
        for group in groups:
            latents, records = joint_class().build("\n".join(group))
            original.extend(latents)
            layouts.extend(records)
        for index, layout in enumerate(layouts):
            layout.update(segment=index, segments=len(layouts))
        s = self.settings
        from h3_memory import context_window_model
        from lopata_h3_timeline.nodes_budget import OPTION_KEY
        model = context_window_model(self.model, s, OPTION_KEY)
        model = self.classes["H3ContextWindows"]().patch(model, s["WindowSeconds"], s["OverlapSeconds"])[0]
        from h3_refinement_cache import RefinementCache
        cache = RefinementCache(self.request["Output"], self.request["InputHash"], seed) if self.request.get("InputHash") else None
        samples, conditions = [], []
        with self.torch.no_grad():
            for index, (latent, layout) in enumerate(zip(original, layouts)):
                self.emit("refinement", index=index + 1, total=len(layouts))
                self.emit("stage", text="upscaling")
                condition = self.classes["H3JointConditioning"]().build(layout)[0]
                cached = cache.load(index) if cache is not None else None
                if cached is not None:
                    sample = self.concat.execute({"samples": cached["video"]}, {"samples": cached["audio"]})[0]
                    self.emit("refinement-saved", index=index + 1, total=len(layouts), files=cache.files(index))
                    self.emit("refinement-cached", index=index + 1, total=len(layouts))
                    samples.append(sample); conditions.append(condition)
                    continue
                video, sound = self.separate.execute(latent)
                device = "cuda" if str(self.request["Environment"]["Device"]).startswith("cuda") else "cpu"
                video = up.MinimaxH3LatentUpscaler3D.execute(video, "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors",
                    {"mode":up.UpscaleMode.MEGAPIXELS, "megapixels":s["UpscaleMegapixels"]}, 32, True, True,
                    device, "fp16" if device == "cuda" else "fp32")[0]
                latent = self.concat.execute(video, sound)[0]
                if s["PreserveAudio"]:
                    latent = self.classes["H3JointAudioMask"]().mask(latent, 0.0)[0]
                self.emit("stage", text="refinement-sampling")
                sample = self.nodes.KSampler().sample(model, (seed + index) % (2**53), s["RefineSteps"], 1.0,
                    s["Sampler"], s["Scheduler"], condition, condition, latent, denoise=s["RefineAmount"])[0]
                if cache is not None:
                    picture, audio = self.separate.execute(sample)
                    cache.store(index, picture["samples"], audio["samples"])
                    self.emit("refinement-saved", index=index + 1, total=len(layouts), files=cache.files(index))
                samples.append(sample); conditions.append(condition)
            vae = self.nodes.VAELoader().load_vae("minimax_h3_video_vae_fp16.safetensors")[0]
            self.emit("stage", text="refinement-rendering")
            return self.classes["H3JointRender"]().render(samples, [vae], [self.audio_vae], ["upscale"], ["refined"],
                [s["Crf"]], [s["DecodeSeconds"]], conditioning=conditions, layout=layouts,
                source_audio=original if s["PreserveAudio"] else None)[0]
