"""Progressive H3 sampling inside the SongClip Any Song worker process."""

import faulthandler
import gc
import importlib
import importlib.util
import json
import logging
from pathlib import Path
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
import song_clip_montage as montage
from song_clip_motion import load_package


SAGE_KERNEL = "sageattn_qk_int8_pv_fp16_cuda"
LATENT_MODEL = "minimax_h3_latent_upscaler_3d_conv_v1_fp16.safetensors"


class DenoMontage(montage.NativeMontage):
    def __init__(self, request, job):
        super().__init__(request, job)
        self.device = self.memory.get_torch_device()
        if self.device.type != "cuda" or not self.torch.version.cuda:
            raise RuntimeError("SongClip.DenoGpuRequired")
        folders = request["modelFolders"]
        expected = montage.contained(folders["latent_upscale_models"], LATENT_MODEL)
        actual = self.paths.get_full_path("latent_upscale_models", LATENT_MODEL)
        if actual is None or Path(actual).resolve() != expected:
            raise ValueError("H3 latent upscaler resolved outside the audited model directory.")
        configuration = request["deno"]
        package = load_package("lopata_deno_upscaler", configuration["upscaler"])
        self.upscaler = importlib.import_module(package.__name__ + ".nodes.minimax_h3_latent_upscaler_3d")
        self.sage_calls = 0
        self.sage_patch = self.prepare_attention(configuration)

    def prepare_attention(self, config):
        capability = self.torch.cuda.get_device_capability(self.device)
        if not config["libraries"] or capability[0] != 8:
            montage.emit("stage", stage="deno-attention", message="Deno RTX: PyTorch SDPA")
            return None
        sys.path[:0] = config["libraries"]
        import sageattention

        kernel = getattr(sageattention, SAGE_KERNEL)

        def per_warp_attention(*args, **kwargs):
            # This compiled CUDA quantizer works with the app's embedded Python.
            result = kernel(*args, **kwargs, qk_quant_gran="per_warp")
            self.sage_calls += 1
            return result

        setattr(sageattention, SAGE_KERNEL, per_warp_attention)
        source = Path(config["kjNodes"]) / "nodes" / "model_optimization_nodes.py"
        spec = importlib.util.spec_from_file_location("lopata_deno_attention", source)
        module = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(module)
        montage.emit("stage", stage="deno-attention", message="Deno RTX: SageAttention")
        return module.PathchSageAttentionKJ()

    def generate_shot(self, shot, audio, images, prompt, seed):
        from comfy_extras.nodes_lt import LTXVConcatAVLatent, LTXVSeparateAVLatent

        frame_plan = montage.shot_frames(shot)
        profiles = [(20, 512, 288), (5, 768, 448), (5, 1024, 576)] if shot["performerVisible"] else [
            (12, 512, 288), (3, 1024, 576)]
        if self.request["width"] < self.request["height"]:
            profiles = [(steps, height, width) for steps, width, height in profiles]
        _, width, height = profiles[0]
        with self.torch.cuda.device(self.device), self.torch.no_grad():
            self.clip = self.nodes.CLIPLoader().load_clip(self.request["encoder"], "minimax", "default")[0]
            conditioning, latent = tuple(self.encode.execute(
                self.clip, prompt, width, height, frame_plan["rawFrames"], "match", self.video_vae, self.audio_vae,
                ref_images={f"ref_image_{index}": image for index, image in enumerate(images)},
                ref_audios={"ref_audio_0": audio},
            ))
            self.clip = None
            self.memory.unload_all_models()
            gc.collect()
            self.memory.soft_empty_cache()
            self.model = self.shift.execute(self.nodes.UNETLoader().load_unet(self.request["model"], "default")[0], 12.0, 3.0)[0]
            before = self.sage_calls
            if self.sage_patch is not None:
                self.model = self.sage_patch.patch(self.model, SAGE_KERNEL, False)[0]
            noise = self.noise.get_noise(seed)[0]
            guider = self.guider.get_guider(self.model, conditioning)[0]
            sigmas = self.scheduler.get_sigmas(self.model, "simple", sum(pass_steps for pass_steps, _, _ in profiles), 1.0)[0]
            sampler = self.sampler_select.get_sampler("res_multistep")[0]
            start = 0
            for steps, width, height in profiles:
                if start:
                    video, sound = LTXVSeparateAVLatent.execute(latent)
                    video = self.upscaler.MinimaxH3LatentUpscaler3D.execute(
                        video, LATENT_MODEL,
                        {"mode": self.upscaler.UpscaleMode.TARGET_DIMENSIONS, "width": width, "height": height},
                        32, True, True, "cuda", "fp16",
                    )[0]
                    if video["samples"].shape[-2:] != (height // 16, width // 16):
                        raise RuntimeError("H3 latent upscaler returned unexpected dimensions.")
                    latent = LTXVConcatAVLatent.execute(video, sound)[0]
                montage.emit("deno-pass", index=shot["index"], offset=start, steps=steps, width=width, height=height)
                latent = self.sampler.sample(noise, guider, sampler, sigmas[start:start + steps + 1], latent)[1]
                start += steps
            if self.sage_patch is not None and self.sage_calls == before:
                raise RuntimeError("SageAttention did not run during H3 sampling.")
            return self.tensors(latent)["video"]


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    try:
        verify = len(sys.argv) == 3 and sys.argv[1] == "--verify"
        source = sys.argv[-1]
        request = json.loads(Path(source).read_text(encoding="utf-8-sig"))
        montage.NativeMontage = DenoMontage
        montage.run(request, source, verify_only=verify)
    except Exception as error:
        montage.emit("error", fraction=0, message=str(error), type=type(error).__name__)
        logging.exception("Deno RTX generation failed")
        sys.exit(1)
