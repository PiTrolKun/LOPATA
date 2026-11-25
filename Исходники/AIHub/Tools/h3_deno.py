"""Deno RTX progressive sampling for native H3 video and audio."""

import copy
from functools import partial, wraps
import importlib
import importlib.util
from pathlib import Path
import sys

from h3_native import NativeH3, load_package

LATENT_MODEL = "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors"
SAGE_KERNEL = "sageattn_qk_int8_pv_fp16_cuda"
PASSES = ((20, 288, 512), (5, 448, 768), (5, 576, 1024))


class ProgressiveH3(NativeH3):
    def __init__(self, request, emit):
        config = request["Deno"]
        overlay = Path(config["torch"]).resolve() if config["torch"] else None
        if overlay is not None:
            sys.path.insert(0, str(overlay))
        super().__init__(request, emit)
        from comfy.model_management import get_torch_device

        self.device = get_torch_device()
        if str(self.device) != request["Environment"]["Device"] or self.device.type != "cuda" or not self.torch.version.cuda:
            raise RuntimeError("SongClip.DenoGpuRequired")
        self.sparse = overlay is not None
        self.calls = 0
        if self.sparse:
            import comfy_kitchen as kitchen
            if (self.torch.__version__ != "2.10.0+cu130"
                    or not Path(self.torch.__file__).resolve().is_relative_to(overlay)
                    or not kitchen.sol_attn_is_available(self.device)):
                raise RuntimeError("SongClip.DenoAccelerateUnavailable")

            def count(kernel):
                @wraps(kernel)
                def call(*args, **kwargs):
                    result = kernel(*args, **kwargs)
                    self.calls += 1
                    return result
                return call

            kitchen.sol_attn = count(kitchen.sol_attn)
            kitchen.sol_attn_chunked = count(kitchen.sol_attn_chunked)
        env = request["Environment"]
        package = load_package("lopata_h3_deno_upscaler", env["Upscaler"])
        self.upscaler = importlib.import_module(package.__name__ + ".nodes.minimax_h3_latent_upscaler_3d")
        expected = Path(request["ModelFolders"]["latent_upscale_models"], LATENT_MODEL).resolve()
        actual = self.paths.get_full_path("latent_upscale_models", LATENT_MODEL)
        if actual is None or Path(actual).resolve() != expected:
            raise ValueError("H3 latent upscaler resolved outside the audited model directory.")

    def load_diffusion(self):
        super().load_diffusion()
        config = self.request["Deno"]
        if config["libraries"] and self.torch.cuda.get_device_capability(self.device)[0] == 8:
            sys.path[:0] = config["libraries"]
            import sageattention

            kernel = getattr(sageattention, SAGE_KERNEL)
            setattr(sageattention, SAGE_KERNEL, partial(kernel, qk_quant_gran="per_warp"))
            path = Path(config["kjNodes"]) / "nodes" / "model_optimization_nodes.py"
            spec = importlib.util.spec_from_file_location("lopata_h3_deno_attention", path)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            self.model = module.PathchSageAttentionKJ().patch(self.model, SAGE_KERNEL, False)[0]
        if self.sparse:
            from comfy_extras.nodes_sparse_attention import BlockSparseAttention

            self.model = BlockSparseAttention.execute(
                self.model, {"selection": "sol-attn", "tau": 1.3}, .2, .9,
                dense_blocks="", min_tokens=12288, extra_tokens=0,
                sink_conditioning="exact_kv_and_rows", verbose=False,
            )[0]

    def sample(self, item, parent, conditioning, latent):
        from comfy_extras import nodes_custom_sampler as sampler_nodes

        if parent is not None and "_ParentEndFrame" not in item:
            import av
            from lopata_h3_timeline.nodes_load import resolve_clip_path

            # The previous take is at the final resolution. Re-encode its exact
            # presented tail against each pass's target grid.
            with av.open(resolve_clip_path(parent)) as source:
                video = source.streams.video[0]
                end_frame = video.frames or round(float(video.duration * video.time_base) * 24)
            item = dict(item, _ParentEndFrame=end_frame)

        settings = self.settings
        noise = sampler_nodes.RandomNoise().get_noise(item["Seed"])[0]
        sampler = sampler_nodes.KSamplerSelect().get_sampler(settings["Sampler"])[0]
        sigmas = sampler_nodes.BasicScheduler().get_sigmas(self.model, settings["Scheduler"], settings["Steps"], 1.0)[0]
        offset, before = 0, self.calls
        passes = [(p["Steps"], p["Width"], p["Height"]) for p in self.request["Deno"]["passes"]] if "passes" in self.request["Deno"] else PASSES
        for steps, width, height in passes:
            if offset:
                video, audio = self.separate.execute(latent)
                up = self.upscaler
                video = up.MinimaxH3LatentUpscaler3D.execute(
                    video, LATENT_MODEL, {"mode": up.UpscaleMode.TARGET_DIMENSIONS, "width": width, "height": height},
                    32, True, True, "cuda", "fp16",
                )[0]
                if video["samples"].shape[-2:] != (height // 16, width // 16):
                    raise RuntimeError("H3 latent upscaler returned unexpected dimensions.")
                audio.pop("noise_mask", None)
                latent = self.concat.execute(video, audio)[0]
            stage_condition, latent, pins = self.apply_pins(item, parent, copy.deepcopy(conditioning), latent)
            self.emit("stage", text="sampling")
            self.emit("deno-pass", offset=offset, steps=steps, width=width, height=height)
            guider = sampler_nodes.BasicGuider().get_guider(self.model, stage_condition)[0]
            latent = sampler_nodes.SamplerCustomAdvanced().sample(
                noise, guider, sampler, sigmas[offset:offset + steps + 1], latent,
            )[1]
            offset += steps
        if self.sparse:
            self.emit("deno-accelerated", calls=self.calls - before)
        return latent, stage_condition, pins
