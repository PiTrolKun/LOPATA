"""Run the pinned ComfyUI-piFlow FL2VA graph on storyboard frames."""
import importlib.util
from pathlib import Path
import sys

from song_clip_geek_native import NativeGeek
from song_clip_motion_state import contained

SCHEDULE_START = {2: 0.93, 4: 0.96}


class NativePiH3(NativeGeek):
    def __init__(self, request, job, emit):
        self.pi = request["piH3"]
        super().__init__(request, job, emit, library_prefix=(self.pi["libraries"],))
        # Import only the audited package, in this worker process.
        package = Path(self.pi["extension"])
        entry = contained(package, "__init__.py")
        spec = importlib.util.spec_from_file_location(
            "lopata_piflow", entry, submodule_search_locations=[str(package)])
        module = importlib.util.module_from_spec(spec)
        sys.modules[spec.name] = module
        spec.loader.exec_module(module)
        self.pi_nodes = module.NODE_CLASS_MAPPINGS

    def segment(self, first, last, prompt, seconds, index, total):
        if self.card_models is not None:
            self.card_models = None
            self.reset()
        # Callers supply the generated storyboard/handoff paths. Project
        # reference images are used only by the inherited storyboard method.
        self.phase, self.index, self.total = "video", index + 1, total
        try:
            with self.torch.no_grad():
                return self._transition(first, last, prompt, seconds, self.request["seed"] + index)
        finally:
            self.reset()

    def _transition(self, first, last, prompt, seconds, seed):
        from comfy_extras import nodes_custom_sampler as sampler_nodes
        from comfy_extras.nodes_minimax_h3 import MiniMaxH3ImageToVideo, MiniMaxH3SigmaShift

        request = self.request
        for key, category in (("model", "diffusion_models"), ("encoder", "text_encoders"), ("videoVae", "vae")):
            self.require_model(category, request[key])
        self.require_model("loras", self.pi["adapter"])

        model = self.nodes.UNETLoader().load_unet(request["model"], "default")[0]
        model = self.pi_nodes["Load LakonLab Adapter"]().load_adapter(model, self.pi["adapter"], 1.0)[0]
        model = MiniMaxH3SigmaShift.execute(model, 12.0, 3.0)[0]
        clip = self.nodes.CLIPLoader().load_clip(request["encoder"], "minimax", "default")[0]
        vae = self.nodes.VAELoader().load_vae(request["videoVae"])[0]
        width, height = request["width"], request["height"]
        first_frame = self.nodes.ImageScale().upscale(self.image(first), "lanczos", width, height, "center")[0]
        last_frame = self.nodes.ImageScale().upscale(self.image(last), "lanczos", width, height, "center")[0]
        positive, latent = MiniMaxH3ImageToVideo.execute(
            clip, vae, prompt, width, height, max(5, round(seconds * 24)),
            first_frame=first_frame, last_frame=last_frame)
        steps = request["steps"]
        sigmas = self.pi_nodes["pi-Flow Scheduler"]().get_sigmas(model, steps, 0.0, SCHEDULE_START[steps], 0.6)[0]
        noise = sampler_nodes.RandomNoise.execute(seed)[0]
        guider = sampler_nodes.BasicGuider.execute(model, positive)[0]
        sampler = sampler_nodes.KSamplerSelect.execute("euler")[0]
        sampled = sampler_nodes.SamplerCustomAdvanced.execute(noise, guider, sampler, sigmas, latent)[0]
        # Video VAE encoded the two inputs above. The official DWD head
        # already predicts output pixels in the last sampling step.
        return self.pi_nodes["DWD Decode"]().decode(sampled)[0].detach().cpu()
