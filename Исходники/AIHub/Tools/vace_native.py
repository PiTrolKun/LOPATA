"""Native pinned ComfyUI VACE node calls. No custom sampler, server or remote API."""
import gc
import os
from pathlib import Path
import sys

NEGATIVE = "overexposed, static, blurred details, subtitles, low quality, JPEG artifacts, malformed hands, deformed limbs, still image, walking backwards"


class NativeVace:
    def __init__(self, request, emit):
        self.request, self.emit = request, emit
        self.settings = s = request["Settings"]
        env = request["Environment"]
        sys.path.insert(0, env["Libraries"])
        sys.path.insert(0, env["Comfy"])
        os.environ["HF_HUB_OFFLINE"] = "1"
        os.environ["TRANSFORMERS_OFFLINE"] = "1"
        os.environ["TOKENIZERS_PARALLELISM"] = "false"
        # Direct node imports do not perform main.py's aimdo initialization.
        # Use the upstream classic smart loader; never enter an uninitialized dynamic allocator.
        sys.argv = ["lopata-vace", "--offline", "--disable-auto-launch", "--disable-dynamic-vram",
                    "--reserve-vram", str(s["VramReserveGb"]), "--use-pytorch-cross-attention"]
        device = env["Device"]
        if s["MemoryMode"] == "cpu" or device == "cpu":
            if s["MemoryMode"] == "gpu":
                raise ValueError("VideoPrep.VaceGpuRequired")
            sys.argv.append("--cpu")
            device = "cpu"
        elif s["MemoryMode"] == "gpu":
            sys.argv.append("--gpu-only")
        elif s["MemoryMode"] == "offload":
            sys.argv.append("--lowvram")
        import comfy.options
        comfy.options.enable_args_parsing()
        import torch
        if device.startswith("cuda:"):
            torch.cuda.set_device(int(device.split(":")[1]))
        elif device.startswith("xpu:"):
            torch.xpu.set_device(int(device.split(":")[1]))
        import nodes
        import folder_paths
        import comfy.utils
        from comfy_extras.nodes_wan import WanVaceToVideo, TrimVideoLatent
        from comfy_extras.nodes_model_advanced import ModelSamplingSD3
        self.torch, self.nodes = torch, nodes
        self.condition, self.trim, self.shift = WanVaceToVideo, TrimVideoLatent, ModelSamplingSD3
        folder_paths.set_input_directory(request["Input"])
        folder_paths.set_temp_directory(request["Temp"])
        folder_paths.set_output_directory(request["Output"])
        for category, directory in request["ModelFolders"].items():
            folder_paths.add_model_folder_path(category, directory, is_default=True)
        comfy.utils.set_progress_bar_global_hook(lambda value, total, preview=None, *args, **kwargs:
            emit("progress", value=int(value), total=int(total)))
        self.model = self.clip = self.vae = None
        emit("hardware", device=device, torch=torch.__version__)
        emit("memory", loader="classic", mode=s["MemoryMode"], reserve_gb=s["VramReserveGb"])

    def load(self):
        names = self.request["Models"]
        self.emit("stage", text="loading-text-encoder")
        self.clip = self.nodes.CLIPLoader().load_clip(names["Encoder"], "wan", "default")[0]
        self.emit("stage", text="loading-vae")
        self.vae = self.nodes.VAELoader().load_vae(names["Vae"])[0]
        self.emit("stage", text="loading-diffusion")
        model = self.nodes.UNETLoader().load_unet(names["Diffusion"], "default")[0]
        # Wan/VACE official CLI uses shift16; Comfy native patch supplies the same flow schedule.
        self.model = self.shift().patch(model, 16.0)[0]

    def reference(self, paths):
        if not paths:
            return None
        if len(paths) > 1:
            # Native reference_image[:1] would silently discard the remainder.
            raise ValueError("VideoPrep.VaceReferencesLimit")
        import numpy as np
        from PIL import Image, ImageOps
        with Image.open(paths[0]) as source:
            pixels = np.array(ImageOps.exif_transpose(source).convert("RGB"), dtype=np.float32) / 255.0
        return self.torch.from_numpy(pixels)[None]

    def generate(self, item, length, prefix_pixels=None):
        if self.model is None:
            self.load()
        s, n, torch = self.settings, self.nodes, self.torch
        # Match the native Comfy executor across conditioning, sampling and decode.
        # Tiled VAE helpers produce inference tensors and process_output mutates
        # them in place; leaving that producer/consumer chain in no_grad fails.
        with torch.inference_mode():
            self.emit("stage", text="conditioning")
            positive = n.CLIPTextEncode().encode(self.clip, item["Prompt"])[0]
            negative = n.CLIPTextEncode().encode(self.clip, NEGATIVE)[0]
            video = mask = None
            if prefix_pixels is not None:
                self.emit("stage", text="connecting-plans")
                prefix = torch.from_numpy(prefix_pixels.astype("float32") / 255.0)
                video = torch.full((length, s["Height"], s["Width"], 3), 0.5)
                video[:len(prefix)] = prefix
                mask = torch.ones((length, s["Height"], s["Width"]))
                mask[:len(prefix)] = 0
            conditioned = self.condition.execute(positive, negative, self.vae, s["Width"], s["Height"], length,
                1, 1.0, control_video=video, control_masks=mask, reference_image=self.reference(item["Images"]))
            self.emit("stage", text="sampling")
            latent = n.KSampler().sample(self.model, item["Seed"], s["Steps"], 5.0, "uni_pc", "simple",
                                         conditioned[0], conditioned[1], conditioned[2], denoise=1.0)[0]
            # This trims the separate reference latent, NOT the preceding video's overlap.
            latent = self.trim.execute(latent, conditioned[3])[0]
            self.emit("stage", text="decoding")
            images = n.VAEDecodeTiled().decode(self.vae, latent, 512, 64, 64, 8)[0]
            if len(images) != length:
                raise ValueError("VideoPrep.VaceInvalidResult")
            return images

    def unload(self):
        self.model = self.clip = self.vae = None
        import comfy.model_management as memory
        memory.unload_all_models()
        gc.collect()
        memory.soft_empty_cache()
