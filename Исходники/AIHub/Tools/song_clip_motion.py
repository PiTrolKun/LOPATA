"""Offline H3 Music Video adapter using the pinned MultiRef nodes in process.

The upstream sampler, audio masks and seam generator are retained. LOPATA adds
serial, input-bound disk commits and a lazy disk-backed video sequence: the UI
example's 20/64 clip limits and whole-chain latent cache are not required here.
No ComfyUI server, downloader, extension updater or custom diffusion is started.
"""
import faulthandler
from fractions import Fraction
import gc
import hashlib
import importlib.util
import json
import logging
import math
import os
from pathlib import Path
import re
import subprocess
import sys
import time

# ManagedPythonLaunch uses isolated runpy rather than executing a script directly.
sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_motion_state import (MotionState, atomic_json, canonical, contained,
                                    file_hash, operation_lock, plan_chunks, reject_links)


SOURCE_REVISION = "361624fb406b63eb6694442eac6c895fc1533a70"
# Only this audited pre-plan adapter may retain its existing checkpoint identity.
# New attempts always record the actual executing adapter hash.
LEGACY_ADAPTER_SHA256 = "5a75c63aa6f1a947689f995e8c5baa5185dc591303f9059024c4b26adf384cfe"
SINGLE_REFERENCE_ADAPTER_SHA256 = "685a48ad75e18c61b337d244054bd9525c7a1a2d1c435ed8cd473c50753ed7b5"
FPS = 24
AUDIO_RATE = 32000


def emit(kind, **data):
    print("@motion " + json.dumps({"kind": kind, **data}, ensure_ascii=False, allow_nan=False), flush=True)


def load_package(name, directory):
    path = Path(directory).resolve(strict=True)
    spec = importlib.util.spec_from_file_location(name, str(path / "__init__.py"),
                                                submodule_search_locations=[str(path)])
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def integer(request, key, default, low, high):
    value = request.get(key, default)
    if isinstance(value, bool) or not isinstance(value, int) or not low <= value <= high:
        raise ValueError("Invalid motion setting: " + key)
    return value


def validate_visual_plan(request, duration=None, plans=None):
    """A provided plan is mandatory input, never an optional generic fallback."""
    if "visualPlan" not in request:
        return None
    visual = request["visualPlan"]
    if (not isinstance(visual, dict) or type(visual.get("version")) is not int or visual["version"] != 1
            or not isinstance(visual.get("globalStyle"), str) or not visual["globalStyle"].strip()
            or not isinstance(visual.get("scenes"), list) or not 1 <= len(visual["scenes"]) <= 256):
        raise ValueError("Invalid H3 visual plan.")
    scenes = visual["scenes"]
    for index, scene in enumerate(scenes, 1):
        if (not isinstance(scene, dict) or type(scene.get("index")) is not int or scene["index"] != index
                or not isinstance(scene.get("prompt"), str) or not scene["prompt"].strip()):
            raise ValueError("Invalid H3 visual scene or prompt.")
        for name in ("startSeconds", "endSeconds"):
            value = scene.get(name)
            if isinstance(value, bool) or not isinstance(value, (int, float)) or not math.isfinite(value) or value < 0:
                raise ValueError("Invalid H3 visual scene timing.")
        if scene["endSeconds"] <= scene["startSeconds"]:
            raise ValueError("The H3 visual scene must cover a positive duration.")
    if plans is not None:
        if len(scenes) != len(plans):
            raise ValueError("H3 visual scene count does not match the native chunks.")
        for scene, plan in zip(scenes, plans):
            start = plan["startFrame"] / FPS
            end = min(duration, start + plan["rawFrames"] / FPS)
            if abs(scene["startSeconds"] - start) > .05 or abs(scene["endSeconds"] - end) > .05:
                raise ValueError("H3 visual scene times do not match the native chunks.")
    return visual


def visual_prompt(request, index):
    visual = validate_visual_plan(request)
    if visual is None:
        return request.get("prompt", "")
    if type(index) is not int or not 1 <= index <= len(visual["scenes"]):
        raise ValueError("Missing H3 visual prompt for the selected chunk.")
    if "references" in request and request["references"]:
        validate_reference_prompts(request)
        return request["referencePrompts"][index - 1]
    return visual["globalStyle"].strip() + "\n" + visual["scenes"][index - 1]["prompt"].strip()


def reference_inputs(request):
    """New role-aware inputs are ordered, owned project images. Missing key is legacy."""
    if "references" not in request:
        return [{"file": request["reference"], "kind": "", "description": ""}] if request.get("reference") else []
    references = request["references"]
    if not isinstance(references, list) or len(references) > 9:
        raise ValueError("Invalid H3 reference list.")
    job = Path(request["job"]).absolute()
    if job.parent.name != "attempts":
        raise ValueError("Role-aware H3 references require a project attempt directory.")
    project = job.parent.parent.resolve()
    paths = []
    for reference in references:
        if (not isinstance(reference, dict) or set(reference) != {"file", "kind", "description"}
                or reference.get("kind") not in ("performer", "world", "object")
                or not isinstance(reference.get("description"), str) or not reference["description"].strip()
                or len(reference["description"]) > 2000
                or not isinstance(reference.get("file"), str) or not Path(reference["file"]).is_absolute()):
            raise ValueError("Invalid H3 reference role.")
        path = Path(reference["file"])
        reject_links(path)
        if path.suffix.lower() not in (".png", ".jpg", ".jpeg", ".webp") or not path.is_file() or not path.resolve().is_relative_to(project):
            raise ValueError("The H3 reference must be an owned image in this project.")
        key = os.path.normcase(str(path.resolve()))
        if key in paths:
            raise ValueError("Duplicate H3 reference image.")
        paths.append(key)
    first = references[0]["file"] if references else ""
    if request.get("reference", "") != first:
        raise ValueError("The primary H3 reference differs from the ordered image list.")
    return references


def reference_identity(request):
    if "references" not in request:
        return file_hash(request["reference"]) if request.get("reference") else None
    facts = reference_receipt(request)["referenceInputs"]
    return hashlib.sha256(canonical(facts).encode("utf-8")).hexdigest() if facts else None


def reference_receipt(request):
    if "references" not in request:
        return {}
    return {"referenceInputs": [
                {"index": index, "kind": reference["kind"], "description": reference["description"],
                 "sha256": file_hash(reference["file"])}
                for index, reference in enumerate(reference_inputs(request), 1)],
            "referencePromptSha256": [hashlib.sha256(prompt.encode("utf-8")).hexdigest()
                                      for prompt in request.get("referencePrompts", [])]}


def compose_reference_prompt(request, index):
    """Mirror the host's fixed Ref2VA template; supplied prompts cannot override its bound plan."""
    visual = validate_visual_plan(request)
    references = reference_inputs(request)
    roles = visual.get("referenceRoles") if visual else None
    if not references or not isinstance(roles, list) or len(roles) != len(references):
        raise ValueError("Missing translated H3 reference roles.")
    sources = {"performer": "the performer or performers shown", "world": "the setting or event world shown", "object": "the object shown"}
    preservation = {
        "performer": "Preserve the referenced performer identities, number, defining appearance and clothing when they appear. Follow their stated performance role; their actions and the surrounding song events may develop. Do not treat the reference sheet layout as the scene composition.",
        "world": "Preserve the referenced setting and its defining spatial features as the world of the events. Characters, actions and camera placement may develop inside it; do not turn the setting into a performer.",
        "object": "Preserve the referenced object's identity and defining shape. Use it according to its stated role in the events; placement, handling and camera framing may change."
    }
    definitions, retention, uses = [], [], []
    for offset, (reference, role) in enumerate(zip(references, roles), 1):
        if (not isinstance(role, dict) or set(role) != {"index", "kind", "description"}
                or type(role.get("index")) is not int or role["index"] != offset or role.get("kind") != reference["kind"]
                or not isinstance(role.get("description"), str) or not 20 <= len(role["description"].strip()) <= 600
                or any(character in role["description"] for character in "<>\r\n")
                or re.search(r"\b(?:subject_definitions|summary|retention_analysis|detailed_description|overall_soundscape|non_diegetic_music)\s*:",
                             role["description"], re.IGNORECASE)):
            raise ValueError("Invalid translated H3 reference role binding.")
        definitions.append(f'<Subject {offset}> is {sources[role["kind"]]} in <Picture {offset}>. Its role in the clip: {role["description"]}')
        retention.append(f'<Subject {offset}>: fully_preserved - {preservation[role["kind"]]}')
        uses.append(f'<Subject {offset}> retains the role defined above.')
    scene = visual["scenes"][index - 1]
    return ("subject_definitions:\n" + "\n".join(definitions)
            + "\n\nsummary:\n[reference generation] A music-video segment using the defined referenced content and the song-inspired events below."
            + "\n\nretention_analysis:\n" + "\n".join(retention)
            + "\n\ndetailed_description:\n" + visual["globalStyle"] + "\n[Shot 1] " + " ".join(uses) + " " + scene["prompt"]
            + "\n\noverall_soundscape:\nThe supplied original song remains the final soundtrack. Do not add spoken narration or unrelated dialogue."
            + "\n\nnon_diegetic_music:\nThe original song is retained by the host without replacing its vocals or music.")


def validate_reference_prompts(request):
    references = reference_inputs(request)
    prompts = request.get("referencePrompts")
    if "references" not in request or not references:
        if "referencePrompts" in request:
            raise ValueError("Unexpected H3 reference prompts without role-aware images.")
        return
    visual = validate_visual_plan(request)
    if not visual or not isinstance(prompts, list) or len(prompts) != len(visual["scenes"]):
        raise ValueError("Missing H3 reference prompts for the complete visual plan.")
    for index, prompt in enumerate(prompts, 1):
        if not isinstance(prompt, str) or len(prompt) > 32000 or prompt != compose_reference_prompt(request, index):
            raise ValueError("H3 reference prompt does not match the reviewed scene and reference roles.")


def checkpoint_identity(job, request, identity):
    """Explicit, bounded compatibility for pre-plan resumes; never trust arbitrary hashes."""
    path = contained(job, "motion-state.json")
    if "references" in request or not path.exists():
        return identity, None
    reject_links(path)
    existing = json.loads(path.read_text(encoding="utf-8"))
    stored = existing.get("identity", {})
    known = stored.get("adapterSha256") if isinstance(stored, dict) else None
    if known not in (SINGLE_REFERENCE_ADAPTER_SHA256, LEGACY_ADAPTER_SHA256):
        return identity, None
    if known == LEGACY_ADAPTER_SHA256 and "visualPlan" in request:
        return identity, None
    compatible = {**identity, "adapterSha256": known}
    if stored != compatible:
        raise ValueError("Legacy motion inputs differ from the audited compatibility contract.")
    # MotionState independently validates the complete request/input hashes and seed.
    return compatible, known


def validate_request(request):
    if request.get("sourceRevision") != SOURCE_REVISION:
        raise ValueError("The H3 Music Video source revision is not the audited revision.")
    for name in ("libraries", "comfy", "motionSource", "song", "ffmpeg", "ffprobe"):
        path = Path(request[name]).absolute()
        reject_links(path)
        if not path.exists():
            raise FileNotFoundError("Missing H3 Music Video input/component: " + name)
    if not isinstance(request.get("pins"), (dict, list)) or not request["pins"]:
        raise ValueError("The motion request must identify its audited component/model revisions.")
    width = integer(request, "width", 1280, 32, 8192)
    height = integer(request, "height", 736, 32, 8192)
    if width % 32 or height % 32:
        raise ValueError("H3 Music Video generation dimensions must be multiples of 32.")
    for name, default in (("outputWidth", width), ("outputHeight", height)):
        if integer(request, name, default, 2, 8192) % 2:
            raise ValueError("H3 Music Video output dimensions must be even.")
    integer(request, "rawFrames", 243, 22, 3600)
    integer(request, "contextFrames", 39, 5, 3599)
    integer(request, "steps", 20, 1, 100)
    integer(request, "seed", -1, -1, 2**53 - 1)
    integer(request, "quality", 85, 1, 100)
    plan_chunks(1, request.get("rawFrames", 243), request.get("contextFrames", 39))
    if request.get("memoryMode", "auto") not in ("auto", "offload", "gpu"):
        raise ValueError("Invalid H3 memory policy.")
    reserve = request.get("vramReserveGb", 2)
    if isinstance(reserve, bool) or not isinstance(reserve, (int, float)) or not math.isfinite(reserve) or not 0 <= reserve <= 128:
        raise ValueError("Invalid H3 VRAM reserve.")
    for field in ("prompt", "lyrics", "timings"):
        if not isinstance(request.get(field, ""), str):
            raise ValueError("Invalid motion text input: " + field)
    validate_visual_plan(request)
    validate_reference_prompts(request)
    if request.get("reference"):
        reject_links(request["reference"])
        if not Path(request["reference"]).is_file():
            raise FileNotFoundError("The owned H3 reference image is missing.")
    # This profile deliberately uses the existing full INT8 + 32B model set.
    # A lightweight profile must be introduced explicitly, with its paired projection.
    if request.get("projection") or request.get("turbo"):
        raise ValueError("This H3 Music Video profile does not apply ClipProj or Turbo LoRA.")


def probe_audio_seconds(request):
    result = subprocess.run([request["ffprobe"], "-v", "error", "-show_entries",
                             "stream=codec_type,duration:format=duration", "-of", "json", request["song"]],
                            check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True,
                            encoding="utf-8", errors="replace")
    facts = json.loads(result.stdout)
    audio = next((stream for stream in facts.get("streams", []) if stream.get("codec_type") == "audio"), None)
    if audio is None:
        raise ValueError("The song has no audio stream.")
    raw = audio.get("duration", facts.get("format", {}).get("duration"))
    try:
        seconds = float(raw)
    except (ValueError, TypeError):
        seconds = 0.0
    return seconds if math.isfinite(seconds) and seconds > 0 else 0.0


def decode_master_audio(path, torch):
    """Decode only the selected original song; pad conditioning, never this source."""
    import av
    import numpy as np
    pieces = []
    with av.open(str(path)) as source:
        if not source.streams.audio:
            raise ValueError("The song has no audio stream.")
        stream = source.streams.audio[0]
        resampler = av.AudioResampler(format="fltp", layout="stereo", rate=AUDIO_RATE)
        for frame in source.decode(stream):
            pieces.extend(out.to_ndarray() for out in resampler.resample(frame))
        pieces.extend(out.to_ndarray() for out in resampler.resample(None))
    if not pieces:
        raise ValueError("The song decodes to no audio samples.")
    samples = np.concatenate(pieces, axis=-1).astype("float32", copy=False)
    if samples.ndim != 2 or samples.shape[0] != 2 or samples.shape[1] < 1 or not np.isfinite(samples).all():
        raise ValueError("The song did not decode to finite stereo PCM.")
    return {"waveform": torch.from_numpy(samples)[None], "sample_rate": AUDIO_RATE}


class NativeMotion:
    def __init__(self, request, job):
        self.request, self.job = request, job
        sys.path[:0] = [str(Path(request["libraries"]).resolve()), str(Path(request["comfy"]).resolve())]
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
        settings = {"MemoryMode": request.get("memoryMode", "auto"),
                    "VramReserveGb": request.get("vramReserveGb", 2)}
        device = request.get("device", "cpu")
        sys.argv = ["lopata-h3-music", "--offline", "--disable-auto-launch", "--reserve-vram",
                    str(settings["VramReserveGb"]), "--use-pytorch-cross-attention"]
        if device == "cpu":
            sys.argv.append("--cpu")
        elif device.startswith("cuda:"):
            sys.argv.extend(["--cuda-device", device.split(":", 1)[1]])
        if settings["MemoryMode"] == "gpu":
            if device == "cpu":
                raise ValueError("The GPU-only H3 memory policy cannot run on CPU.")
            sys.argv.append("--gpu-only")
        elif settings["MemoryMode"] == "offload":
            sys.argv.extend(["--disable-dynamic-vram", "--lowvram"])
        import comfy.options
        comfy.options.enable_args_parsing()
        from h3_memory import initialize_host_memory, initialize_memory
        initialize_host_memory(settings)
        import torch
        selected_device = torch.device(device)
        backend = getattr(torch, selected_device.type, None)
        if selected_device.index is not None and backend is not None and hasattr(backend, "set_device"):
            # main.py normally applies --cuda-device before importing the manager.
            # This adapter runs in process, so select the requested torch device here.
            backend.set_device(selected_device.index)
        import folder_paths
        import nodes
        import comfy.model_management as memory
        self.torch, self.paths, self.nodes, self.memory = torch, folder_paths, nodes, memory
        initialize_memory(settings, emit)
        actual = memory.get_torch_device()
        if (actual.type != selected_device.type or
                (selected_device.index is not None and (actual.index or 0) != selected_device.index)):
            raise ValueError("The selected H3 device is unavailable; refusing an implicit device fallback.")
        emit("hardware", device=str(actual), torch=torch.__version__)
        for key, subdir in (("output", "native-output"), ("input", "native-input"), ("temp", "native-temp")):
            path = contained(job, subdir)
            path.mkdir(exist_ok=True)
            getattr(folder_paths, "set_" + key + "_directory")(str(path))
        folders = request["modelFolders"]
        for category, directory in folders.items():
            reject_links(directory)
            folder_paths.add_model_folder_path(category, directory, is_default=True)
        for field, category in (("model", "diffusion_models"), ("encoder", "text_encoders"),
                                ("videoVae", "vae"), ("audioVae", "vae")):
            name = request[field]
            expected = contained(folders[category], name)
            if not expected.is_file() or Path(folder_paths.get_full_path(category, name)).resolve() != expected:
                raise ValueError("An H3 loader resolved a model outside its audited model directory.")
        self.motion = load_package("lopata_song_motion", request["motionSource"])
        from lopata_song_motion import h3_streaming_vhs
        self.streamer = h3_streaming_vhs
        from comfy_extras.nodes_minimax_h3 import (MiniMaxH3ReferenceToVideo, MiniMaxH3SigmaShift,
                                                  EmptyMiniMaxH3LatentAV)
        from comfy_extras.nodes_custom_sampler import (RandomNoise, BasicGuider, BasicScheduler,
                                                       KSamplerSelect, SamplerCustomAdvanced)
        self.encode, self.shift = MiniMaxH3ReferenceToVideo, MiniMaxH3SigmaShift
        self.empty_latent = EmptyMiniMaxH3LatentAV
        self.noise, self.guider, self.scheduler = RandomNoise, BasicGuider, BasicScheduler
        self.sampler_select, self.sampler = KSamplerSelect, SamplerCustomAdvanced
        self.chunk_index, self.chunk_count = 0, 1
        import comfy.utils
        comfy.utils.set_progress_bar_global_hook(lambda value, total, *args, **kwargs:
            emit("progress", index=self.chunk_index, total=self.chunk_count, step=int(value), steps=int(total),
                 fraction=min(.85, .08 + .77 * (max(0, self.chunk_index - 1) + value / max(1, total)) / self.chunk_count)))
        self.clip = self.model = self.video_vae = self.audio_vae = self.conditioned = None

    def load_vaes(self):
        emit("stage", stage="loading-vae", fraction=.03, message="Loading audited H3 audio/video VAEs")
        self.video_vae = self.nodes.VAELoader().load_vae(self.request["videoVae"])[0]
        raw_audio = self.nodes.VAELoader().load_vae(self.request["audioVae"])[0]
        self.audio_vae = self.motion.NODE_CLASS_MAPPINGS["MiniMaxH3AudioVAECompatibility"]().prepare(raw_audio)[0]

    def condition(self, prompt):
        if self.conditioned is not None and getattr(self, "conditioned_prompt", None) == prompt:
            return self.conditioned
        emit("stage", stage="conditioning", fraction=.04, message="Encoding the music video prompt and reference")
        self.clip = self.nodes.CLIPLoader().load_clip(self.request["encoder"], "minimax", "default")[0]
        refs = {}
        references = reference_inputs(self.request)
        if references:
            import numpy as np
            from PIL import Image, ImageOps
            for index, reference in enumerate(references):
                with Image.open(reference["file"]) as source:
                    image = ImageOps.exif_transpose(source).convert("RGB")
                    image.thumbnail((2048, 2048))
                    refs["ref_image_" + str(index)] = self.torch.from_numpy(np.array(image).astype("float32") / 255)[None]
        self.conditioned = tuple(self.encode.execute(self.clip, prompt,
            self.request["width"], self.request["height"], self.request.get("rawFrames", 243),
            "match", self.video_vae, self.audio_vae, ref_images=refs))
        self.conditioned_prompt = prompt
        self.clip = None
        self.memory.unload_all_models()
        gc.collect()
        self.memory.soft_empty_cache()
        return self.conditioned

    def generate(self, plan, master, previous, seed):
        with self.torch.no_grad():
            conditioning, latent = self.condition(visual_prompt(self.request, plan["index"]))
            if plan["rawFrames"] != self.request.get("rawFrames", 243):
                # Image/text reference conditioning is reusable. Only the empty
                # AV target must change when the terminal raw run is shorter.
                latent = self.empty_latent.execute(self.request["width"], self.request["height"],
                                                   plan["rawFrames"])[0]
            if self.model is None:
                emit("stage", stage="loading-diffusion", fraction=.06, message="Loading audited H3 Ref2VA")
                model = self.nodes.UNETLoader().load_unet(self.request["model"], "default")[0]
                self.model = self.shift.execute(model, 12.0, 3.0)[0]
            prepare = self.motion.NODE_CLASS_MAPPINGS["MiniMaxH3SongMaskedAVContext"]()
            masked = prepare.prepare(latent, self.audio_vae, master,
                clip_start_seconds=plan["startFrame"] / FPS,
                context_length=plan["contextFrames"], source_fps=FPS, crop="disabled",
                source_latent=previous)[0]
            noise = self.noise.get_noise(seed)[0]
            guider = self.guider.get_guider(self.model, conditioning)[0]
            sigmas = self.scheduler.get_sigmas(self.model, "simple", self.request.get("steps", 20), 1.0)[0]
            sampler = self.sampler_select.get_sampler("res_multistep")[0]
            return self.sampler.sample(noise, guider, sampler, sigmas, masked)[0]

    def tensors(self, latent):
        samples = latent["samples"]
        streams = list(samples.unbind()) if hasattr(samples, "unbind") else list(samples)
        if len(streams) != 2:
            raise ValueError("H3 did not produce a joint video/audio latent.")
        return {name: value.detach().cpu().contiguous().clone() for name, value in zip(("video", "audio"), streams)}

    def verify_checkpoint(self, path, identity):
        from safetensors import safe_open
        descriptors = {}
        expected_frames = identity["rawFrames"]
        with safe_open(str(path), framework="pt", device="cpu") as source:
            if set(source.keys()) != {"video", "audio"} or source.metadata().get("LOPATA_MOTION") != canonical(identity):
                raise ValueError("The H3 latent file has no matching completed-chunk identity.")
            for key in ("video", "audio"):
                tensor = source.get_tensor(key)
                shape = list(tensor.shape)
                if not self.torch.isfinite(tensor).all().item() or not tensor.is_floating_point():
                    raise ValueError("The H3 latent checkpoint contains invalid tensor values.")
                if key == "video":
                    if (len(shape) != 5 or shape[0] != 1 or shape[1] != 24
                            or shape[2] != 2 + 5 * ((expected_frames - 5) // 17)
                            or shape[3:] != [self.request["height"] // 16, self.request["width"] // 16]):
                        raise ValueError("The H3 video latent does not match its chunk plan.")
                elif (len(shape) != 4 or shape[0] != 1 or shape[1] != 32 or shape[2] != 2
                      or shape[-1] != round(expected_frames * 40 / FPS)):
                    raise ValueError("The H3 audio latent does not match its chunk plan.")
                descriptors[key] = {"shape": shape, "dtype": str(tensor.dtype)}
        return descriptors

    def load_checkpoint(self, state, facts):
        from safetensors.torch import load_file
        path = state.data_path(facts)
        if self.verify_checkpoint(path, facts["identity"]) != facts["tensors"]:
            raise ValueError("The H3 checkpoint tensor descriptor was changed.")
        tensors = load_file(str(path), device="cpu")
        return {"samples": [tensors["video"], tensors["audio"]]}

    def release_sampling(self):
        self.clip = self.model = self.conditioned = None
        self.memory.unload_all_models()
        gc.collect()
        self.memory.soft_empty_cache()


class DiskVideos:
    """The pinned seam generator needs only len() and iteration, not all latents."""
    def __init__(self, native, state, receipts):
        self.native, self.state, self.receipts = native, state, receipts

    def __len__(self):
        return len(self.receipts)

    def __iter__(self):
        previous_hash = self.state.input_hash
        for facts in self.receipts:
            verified = self.state.load({key: facts["identity"][key] for key in
                ("index", "startFrame", "rawFrames", "contextFrames")}, previous_hash)
            if verified != facts:
                raise ValueError("The H3 checkpoint changed before final assembly.")
            latent = self.native.load_checkpoint(self.state, facts)
            yield latent["samples"][0]
            previous_hash = facts["data"]["sha256"]


def encode_video(native, state, receipts, total_frames):
    import av
    import numpy as np
    request = native.request
    videos = DiskVideos(native, state, receipts)
    raw = [item["identity"]["rawFrames"] for item in receipts]
    contexts = [item["identity"]["contextFrames"] for item in receipts]
    frames = native.streamer._generated_frame_generator(native.video_vae, videos, raw, contexts,
        request.get("contextFrames", 39), "lopata_song_motion")
    target = contained(native.job, "motion-video.mp4")
    staging = contained(native.job, "motion-video.partial.mp4")
    written = 0
    try:
        with av.open(str(staging), "w", format="mp4") as output:
            stream = output.add_stream("libx264", rate=FPS)
            stream.width = request.get("outputWidth", request["width"])
            stream.height = request.get("outputHeight", request["height"])
            stream.pix_fmt = "yuv420p"
            stream.time_base = stream.codec_context.time_base = Fraction(1, FPS)
            stream.options = {"crf": str(max(10, min(35, round(36 - request.get("quality", 85) / 5)))), "preset": "medium"}
            output.metadata["comment"] = "LOPATA H3 music intermediate; source audio/provenance are muxed by the host"
            with native.torch.no_grad():
                for image in frames:
                    if written >= total_frames:
                        break
                    rgb = (image.clamp(0, 1).numpy() * 255).round().astype(np.uint8)
                    frame = av.VideoFrame.from_ndarray(rgb, format="rgb24")
                    frame = frame.reformat(width=stream.width, height=stream.height, format=stream.pix_fmt)
                    frame.pts, frame.time_base = written, Fraction(1, FPS)
                    for packet in stream.encode(frame):
                        output.mux(packet)
                    # A yielded frame is a view of the entire decoded clip.
                    # Drop it before requesting the next clip from the generator.
                    del image, rgb, frame
                    written += 1
                    if written % FPS == 0:
                        emit("stage", stage="render", fraction=.85 + .14 * written / total_frames,
                             message="Assembling the complete music video", frames=written, totalFrames=total_frames)
            if written != total_frames:
                raise ValueError("The H3 renderer did not cover the complete source song.")
            for packet in stream.encode():
                output.mux(packet)
        with staging.open("rb+") as stream:
            os.fsync(stream.fileno())
        reject_links(target)
        os.replace(staging, target)
    finally:
        frames.close()
        if staging.exists():
            staging.unlink()
        gc.collect()
        native.memory.soft_empty_cache()
    return target, written


def mux_original_song(request, job, video, duration):
    """Deliver the full original soundtrack; never use H3's reconstructed audio."""
    target = contained(job, "motion-raw.mp4")
    staging = contained(job, "motion-raw.partial.mp4")
    command = [request["ffmpeg"], "-nostdin", "-v", "error", "-y", "-i", str(video), "-i", request["song"],
               "-map", "0:v:0", "-map", "1:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
               "-c:v", "copy", "-c:a", "aac", "-b:a", "192k", "-t", format(duration, ".9f"),
               "-movflags", "+faststart+use_metadata_tags", str(staging)]
    try:
        result = subprocess.run(command, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode:
            raise RuntimeError("Original-song mux failed: " + result.stderr.decode("utf-8", "replace")[-8000:])
        with staging.open("rb+") as stream:
            os.fsync(stream.fileno())
        reject_links(target)
        os.replace(staging, target)
    finally:
        if staging.exists():
            staging.unlink()
    return target


def run(request):
    started = time.monotonic()
    validate_request(request)
    job = Path(request["job"]).absolute()
    reject_links(job)
    job.mkdir(parents=True, exist_ok=True)
    with operation_lock(job):
        song_hash = file_hash(request["song"])
        reference_hash = reference_identity(request)
        reference_facts = reference_receipt(request)
        # Reject an incompatible explicit plan before allocating the native runtime.
        probed_seconds = None
        if "visualPlan" in request:
            probed_seconds = probe_audio_seconds(request)
            if probed_seconds <= 0:
                raise ValueError("The song duration is required to validate its H3 visual plan.")
            probed_plans = plan_chunks(math.ceil(Fraction(str(probed_seconds)) * FPS),
                                       request.get("rawFrames", 243), request.get("contextFrames", 39))
            validate_visual_plan(request, probed_seconds, probed_plans)
        native = NativeMotion(request, job)
        torch = native.torch
        emit("stage", stage="ingest", fraction=.01, message="Decoding the original master song")
        song = decode_master_audio(request["song"], torch)
        if file_hash(request["song"]) != song_hash:
            raise ValueError("The original song was changed while decoding its immutable attempt input.")
        sample_count = int(song["waveform"].shape[-1])
        decoded_seconds = sample_count / AUDIO_RATE
        duration = max(decoded_seconds, probed_seconds if probed_seconds is not None else probe_audio_seconds(request))
        total_frames = max((sample_count * FPS + AUDIO_RATE - 1) // AUDIO_RATE,
                           math.ceil(Fraction(str(duration)) * FPS))
        plans = plan_chunks(total_frames, request.get("rawFrames", 243), request.get("contextFrames", 39))
        validate_visual_plan(request, duration, plans)
        native.chunk_count = len(plans)
        padded_samples = ((plans[-1]["startFrame"] + plans[-1]["rawFrames"]) * AUDIO_RATE + FPS - 1) // FPS
        padded_samples = max(padded_samples, sample_count)
        conditioning_song = {"sample_rate": AUDIO_RATE,
            "waveform": torch.nn.functional.pad(song["waveform"], (0, padded_samples - sample_count))}
        identity = {"sourceRevision": SOURCE_REVISION, "pins": request["pins"],
                    "songSha256": song_hash, "referenceSha256": reference_hash,
                    "adapterSha256": file_hash(__file__),
                    "stateAdapterSha256": file_hash(Path(__file__).with_name("song_clip_motion_state.py")),
                    "settings": {key: request.get(key) for key in
                        ("modelId", "model", "encoder", "videoVae", "audioVae", "prompt", "lyrics", "timings",
                         "width", "height", "rawFrames", "contextFrames", "steps", "seed",
                         "outputWidth", "outputHeight", "quality", "device", "memoryMode", "vramReserveGb")},
                    "audioSamples": sample_count, "audioRate": AUDIO_RATE, "frames": total_frames}
        if "visualPlan" in request:
            identity["settings"]["visualPlan"] = request["visualPlan"]
        if "references" in request:
            identity["settings"]["references"] = request["references"]
        if "referencePrompts" in request:
            identity["settings"]["referencePrompts"] = request["referencePrompts"]
        actual_adapter_hash = identity["adapterSha256"]
        identity, compatibility_hash = checkpoint_identity(job, request, identity)
        state = MotionState(job, identity, request.get("seed", -1))
        if compatibility_hash is not None:
            emit("compatibility", mode="audited-legacy-adapter", actualAdapterSha256=actual_adapter_hash,
                 identityCompatibilityHash=compatibility_hash)
        emit("plan", inputHash=state.input_hash, seed=state.seed, total=len(plans), frames=total_frames,
             duration=duration, conditioningPadSamples=padded_samples - sample_count)
        native.load_vaes()
        receipts, previous_hash = [], state.input_hash
        for plan in plans:
            native.chunk_index = plan["index"]
            facts = state.load(plan, previous_hash)
            if facts is None:
                previous = native.load_checkpoint(state, receipts[-1]) if receipts else None
                emit("chunk-start", index=plan["index"], total=len(plans), startFrame=plan["startFrame"],
                     fraction=.08 + .77 * (plan["index"] - 1) / len(plans))
                sample = native.generate(plan, conditioning_song, previous,
                                         (state.seed + plan["index"] - 1) % 2**53)
                tensors = native.tensors(sample)
                del sample, previous
                if file_hash(request["song"]) != song_hash or reference_identity(request) != reference_hash:
                    raise ValueError("A source attachment changed before the generated chunk could be accepted.")
                from safetensors.torch import save_file
                def write(path, metadata):
                    save_file(tensors, str(path), metadata={"LOPATA_MOTION": canonical(metadata)})
                facts = state.commit(plan, previous_hash, write, native.verify_checkpoint)
                del tensors
                emit("chunk", index=plan["index"], total=len(plans), checkpoint=facts,
                     files=state.files(facts), fraction=.08 + .77 * plan["index"] / len(plans))
            else:
                if native.verify_checkpoint(state.data_path(facts), facts["identity"]) != facts["tensors"]:
                    raise ValueError("The H3 checkpoint tensor descriptor was changed.")
                emit("cached", index=plan["index"], total=len(plans), checkpoint=facts,
                     fraction=.08 + .77 * plan["index"] / len(plans))
            receipts.append(facts)
            previous_hash = facts["data"]["sha256"]
        native.release_sampling()
        emit("stage", stage="render", fraction=.85, message="Rendering verified music-video chunks from disk")
        output, frames = encode_video(native, state, receipts, total_frames)
        if file_hash(request["song"]) != song_hash or reference_identity(request) != reference_hash:
            raise ValueError("A source attachment was changed during the music-video attempt.")
        emit("stage", stage="mux", fraction=.99, message="Adding the complete original song")
        output = mux_original_song(request, job, output, duration)
        result = {"output": str(output), "duration": duration, "frames": frames,
                  "seconds": time.monotonic() - started, "completedChunks": len(receipts),
                  "inputHash": state.input_hash, "seed": state.seed, "sourceRevision": SOURCE_REVISION,
                  "checkpointHash": previous_hash, "audioMuxRequired": False,
                  "actualAdapterSha256": actual_adapter_hash}
        result.update(reference_facts)
        if compatibility_hash is not None:
            result["identityCompatibilityHash"] = compatibility_hash
        atomic_json(contained(job, "worker-result.json"), result)
        emit("complete", fraction=1, message="H3 music video with the complete original song is ready", **result)


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    try:
        run(json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig")))
    except Exception as error:
        emit("error", fraction=0, message=str(error), type=type(error).__name__)
        logging.exception("H3 music video failed")
        sys.exit(1)
