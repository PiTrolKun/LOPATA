"""Offline host adapter for the audited Any Song H3 montage workflow.

Reuses the author's beat analysis, cut planner, deterministic reference router
and prompt composer. His near-final-frame policy is decoded with installed PyAV
instead of introducing OpenCV for one image. The desktop owns serial execution,
24 fps timing, checkpoint paths and exact original-song assembly instead of a
Comfy server queue, cloud Director or VHS output-directory discovery.
"""
import ast
from collections import deque
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

sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_motion import (AUDIO_RATE, NativeMotion, SOURCE_REVISION, decode_master_audio,
                              emit, integer, probe_audio_seconds, reference_inputs, reference_receipt)
from song_clip_motion_state import atomic_json, canonical, contained, file_hash, operation_lock, reject_links
from song_clip_montage_state import MontageState, FPS, object_hash, read_document, shot_frames, validate_plan
from song_clip_montage_media import join_video, scale_video


ANY_SONG_REVISION = "37b72ef49d9128e6853f2b7a2d1fa56bc65cfeb1"


def load_module(name, source):
    reject_links(source)
    sys.dont_write_bytecode = True
    spec = importlib.util.spec_from_file_location(name, str(source))
    module = importlib.util.module_from_spec(spec)
    sys.modules[name] = module
    spec.loader.exec_module(module)
    return module


def load_definitions(source, names, dependencies):
    """Execute exact audited definitions without plugin/cloud/server registration."""
    reject_links(source)
    parsed = ast.parse(Path(source).read_text(encoding="utf-8-sig"), filename=str(source))
    selected = [node for node in parsed.body if isinstance(node, (ast.FunctionDef, ast.ClassDef)) and node.name in names]
    if {node.name for node in selected} != set(names):
        raise ValueError("The audited Any Song helper definitions are missing.")
    namespace = dict(dependencies)
    exec(compile(ast.Module(body=selected, type_ignores=[]), str(source), "exec"), namespace)
    return namespace


def source_files(request):
    root = Path(request["anySongSource"]).absolute()
    reject_links(root)
    result = {name: contained(root, name) for name in ("__init__.py", "director_parser.py", "audio_preprocessor.py")}
    if any(not path.is_file() for path in result.values()):
        raise FileNotFoundError("The audited Any Song source is incomplete.")
    return result


def validate_request(request, analysis=False):
    if (request.get("sourceRevision") != SOURCE_REVISION
            or request.get("anySongSourceRevision") != ANY_SONG_REVISION
            or request.get("generationMode", "montage") != "montage"):
        raise ValueError("The montage mode or audited source revision is invalid.")
    for name in ("libraries", "comfy", "motionSource", "anySongSource", "song", "ffmpeg", "ffprobe"):
        if not isinstance(request.get(name), str) or not Path(request[name]).is_absolute():
            raise ValueError("An absolute audited montage path is required: " + name)
        reject_links(request[name])
        if not Path(request[name]).exists():
            raise FileNotFoundError("Missing montage component: " + name)
    if not isinstance(request.get("pins"), (dict, list)) or not request["pins"]:
        raise ValueError("Audited montage component pins are required.")
    job = Path(request["job"])
    if not job.is_absolute():
        raise ValueError("The montage job path must be absolute.")
    reject_links(job)
    source_files(request)
    if "halfTime" in request and type(request["halfTime"]) is not bool:
        raise ValueError("Invalid montage half-time setting.")
    clip = request.get("clipSeconds", 5)
    if isinstance(clip, bool) or not isinstance(clip, (int, float)) or not math.isfinite(clip) or not 2 <= clip <= 15:
        raise ValueError("Montage shot length must be between 2 and 15 seconds.")
    if analysis:
        if not isinstance(request.get("analysisLibraries"), str) or not Path(request["analysisLibraries"]).is_absolute():
            raise ValueError("An absolute audited beat-analysis library path is required.")
        reject_links(request["analysisLibraries"])
        if not Path(request["analysisLibraries"]).is_dir():
            raise FileNotFoundError("The audited beat-analysis library snapshot is missing.")
        return
    for key, default, low, high in (("width", 608, 32, 8192), ("height", 352, 32, 8192),
                                   ("steps", 20, 1, 100), ("quality", 85, 1, 100), ("seed", -1, -1, 2**53 - 1)):
        integer(request, key, default, low, high)
    if request["width"] % 32 or request["height"] % 32:
        raise ValueError("H3 dimensions must be multiples of 32.")
    for key, default in (("outputWidth", request["width"]), ("outputHeight", request["height"])):
        if integer(request, key, default, 2, 8192) % 2:
            raise ValueError("Montage output dimensions must be even.")
    if request.get("memoryMode", "auto") not in ("auto", "offload", "gpu"):
        raise ValueError("Invalid montage memory policy.")
    reserve = request.get("vramReserveGb", 2)
    if isinstance(reserve, bool) or not isinstance(reserve, (int, float)) or not math.isfinite(reserve) or not 0 <= reserve <= 128:
        raise ValueError("Invalid montage VRAM reserve.")
    if request.get("projection") or request.get("turbo"):
        raise ValueError("Montage uses the audited installed Ref2VA INT8/32B set without additional weights.")
    reference_inputs(request)


def grid_slots(ranges, duration):
    """Keep the author's bar-aligned cuts while using the actual native 24 fps."""
    bounds = [0]
    for start, length in ranges[:-1]:
        bound = round((start + length) * FPS)
        if bounds[-1] < bound < math.ceil(duration * FPS):
            bounds.append(bound)
    points = [value / FPS for value in bounds] + [duration]
    return [{"index": index + 1, "startSeconds": start, "endSeconds": end,
             "durationSeconds": end - start} for index, (start, end) in enumerate(zip(points, points[1:]))]


def analyze(request):
    validate_request(request, analysis=True)
    job = Path(request["job"]).absolute()
    job.mkdir(parents=True, exist_ok=True)
    with operation_lock(job):
        # Librosa's Numba kernels must never write executable caches into an
        # audited shared library tree. Match the existing Glitchframe worker.
        os.environ["NUMBA_CACHE_DIR"] = str(contained(job, "numba-cache"))
        sys.path[:0] = [str(Path(request["analysisLibraries"]).resolve()), str(Path(request["libraries"]).resolve())]
        os.environ.update(HF_HUB_OFFLINE="1", TRANSFORMERS_OFFLINE="1", HF_HUB_DISABLE_TELEMETRY="1")
        import torch
        files = source_files(request)
        source_hashes = {name: file_hash(path) for name, path in files.items()}
        original_hash = file_hash(request["song"])
        emit("stage", stage="montage-analysis", fraction=0, message="Analyzing the source song's beats without loading H3")
        audio = decode_master_audio(request["song"], torch)
        duration = probe_audio_seconds(request)
        if duration <= 0:
            raise ValueError("The original song duration cannot be determined.")
        preprocessor = load_module("lopata_anysong_audio", files["audio_preprocessor.py"])
        bpm, positions = preprocessor._analyze_beats(audio, bool(request.get("halfTime", False)), 0)
        beat_data = json.loads(positions)
        beats = beat_data.get("beat_times", [])
        if (not isinstance(beats, list) or any(type(value) not in (int, float) or not math.isfinite(value)
                                              or not 0 <= value <= duration + .1 for value in beats)
                or not math.isfinite(bpm) or bpm <= 0):
            raise ValueError("The Any Song beat analysis returned invalid timing.")
        helpers = load_definitions(files["__init__.py"], {"_chunk_ranges"}, {"json": json, "math": math})
        slots = grid_slots(helpers["_chunk_ranges"](duration, request.get("clipSeconds", 5), bpm, positions), duration)
        if not slots or len(slots) > 1024 or any(item["durationSeconds"] > 15.001 for item in slots):
            raise ValueError("The montage analysis produced an unsupported shot count or duration.")
        if file_hash(request["song"]) != original_hash:
            raise ValueError("The song changed during beat analysis.")
        if {name: file_hash(path) for name, path in files.items()} != source_hashes:
            raise ValueError("The audited Any Song helpers changed during beat analysis.")
        result = {"version": 1, "durationSeconds": duration, "fps": FPS, "framesPerSecond": FPS,
                  "beatTimes": beats, "bpm": bpm, "slots": slots, "analyzerRevision": ANY_SONG_REVISION,
                  "songSha256": original_hash, "sourceSha256": source_hashes,
                  "clipSeconds": request.get("clipSeconds", 5), "halfTime": bool(request.get("halfTime", False))}
        atomic_json(contained(job, "montage-analysis.json"), result)
        emit("analysis", fraction=1, **result)
        return result


def route_shot(selector, shot, references):
    transition = "match_cut" if shot["transition"] == "match" else shot["transition"]
    route = selector.select(None, None, None, None, None,
                            shot.get("shotSize", "medium"), shot.get("cameraAngle", "eye_level"),
                            shot["performerVisible"], transition, "high", strict_identity_mode=True)
    if route[3]:
        raise ValueError("An unexpected FL2VA route requires weights outside the audited set.")
    indices = list(shot["referenceIndices"])
    if not route[4]:
        indices = [index for index in indices if references[index - 1]["kind"] != "performer"]
    if len(indices) + int(route[5]) > 9:
        raise ValueError("This montage shot exceeds H3's nine image references including its previous-frame anchor.")
    return {"strategy": route[7], "description": route[8], "referenceIndices": indices,
            "previousFrame": bool(route[5]), "identityProfile": route[2],
            "index": shot["index"], "performerVisible": shot["performerVisible"], "transition": shot["transition"]}


def compose_prompt(composer, plan, shot, references, route):
    roles = plan.get("referenceRoles", [])
    if references and (not isinstance(roles, list) or len(roles) != len(references)):
        raise ValueError("Translated reference roles are missing from the montage plan.")
    upstream = {"scene_prompt": shot["prompt"], "duration_sec": shot["durationSeconds"],
                "shot_size": shot.get("shotSize", "medium"), "camera_angle": shot.get("cameraAngle", "eye_level"),
                "performance_mode": "narrative", "lighting": "the directed scene lighting",
                "palette": "consistent song-inspired", "motion_prompt": "Follow the scene's directed action"}
    composed = composer.compose(canonical(upstream), route["identityProfile"], route["strategy"], single_identity_mode=True)
    # Keep the author's shared cinematic shot/beat construction, without his
    # hard-coded Arabic lip-sync text, single-person assumption or wardrobe replacement.
    detail = composed[0].split("detailed_description:\n", 1)[1].split("\n\noverall_soundscape:", 1)[0]
    detail = detail.split(" Create symbolic cinematic B-roll only:", 1)[0]
    detail = detail.replace("The target video uses a premium contemporary cinematic music-video style with physically credible live-action "
                            "movement, the directed scene lighting, and a consistent song-inspired palette.",
                            "Follow the global visual style above, the scene's directed lighting and a consistent palette; keep movement physically coherent.")
    detail = detail.replace("The directed wardrobe is directed plain logo-free wardrobe.",
                            "Preserve the selected performer references' defining clothing where present.")
    detail = detail.replace("Do not slim, enlarge, beautify, age, caricature, duplicate, or reinterpret a referenced person.",
                            "Preserve the selected references according to their roles below.")
    if shot["performerVisible"]:
        detail = detail.replace("No visible character lip-syncs; any visible mouth stays naturally closed or remains outside clear view.",
                                "Visible performers follow their stated role, directed action and the supplied song; preserve their identity.")
    definitions, retention = [], []
    names = {"performer": "performer or performers", "world": "setting or event world", "object": "object"}
    preserve = {"performer": "identity, number, defining appearance and clothing; the reference sheet layout is not the scene composition",
                "world": "the setting's identity and defining spatial features; do not turn the setting into a performer",
                "object": "the object's identity and shape while its handling and position may develop"}
    for picture, index in enumerate(route["referenceIndices"], 1):
        reference, role = references[index - 1], roles[index - 1]
        if (not isinstance(role, dict) or role.get("index") != index or role.get("kind") != reference["kind"]
                or not isinstance(role.get("description"), str) or not role["description"].strip()
                or len(role["description"]) > 2000 or any(character in role["description"] for character in "<>\r\n")):
            raise ValueError("Invalid translated montage reference role.")
        definitions.append(f'<Subject {index}> is the {names[reference["kind"]]} from <Picture {picture}>. Role: {role["description"]}')
        retention.append(f'<Subject {index}>: fully_preserved - preserve {preserve[reference["kind"]]}.')
    if route["previousFrame"]:
        picture = len(route["referenceIndices"]) + 1
        definitions.append(f"<Picture {picture}> is the previous shot's near-final frame, an opening composition and screen-direction anchor.")
        retention.append(f"<Picture {picture}>: partially_preserved - retain the planned match/continuation anchor; it is not performer identity evidence.")
    definitions.append("<Audio 1> is the exact current source-song segment and supplies this shot's timing and soundtrack.")
    retention.append("<Audio 1>: fully_copy - retain the complete source segment without replacing vocals or music.")
    if not shot["performerVisible"]:
        detail += " This is performer-free B-roll. Do not introduce the lead performer or a recognizable singer."
    return ("subject_definitions:\n" + "\n".join(definitions) + "\n\nsummary:\n[reference generation + audio reuse] "
            "One continuous song-inspired montage shot follows <Audio 1>.\n\nretention_analysis:\n" + "\n".join(retention)
            + "\n\ndetailed_description:\n" + plan["globalStyle"] + "\n" + detail
            + "\n\noverall_soundscape:\nDo not add unrelated dialogue, narration or a replacement song."
            + "\n\nnon_diegetic_music:\n<Audio 1> is the original source song segment. The host retains the complete original master soundtrack.")


def slice_audio(master, shot):
    """The author's controller PCM slicing, bound to our exact accepted slot."""
    rate = master["sample_rate"]
    start = round(shot["startSeconds"] * rate)
    end = round((shot["startSeconds"] + shot["durationSeconds"]) * rate)
    waveform = master["waveform"][..., start:end]
    if waveform.shape[-1] < 1:
        raise ValueError("A montage shot contains no source-song samples.")
    return {"waveform": waveform, "sample_rate": rate}


def previous_frame(path, torch, width, height, offset=3):
    """Author's count-3 anchor policy, streamed via the installed audited PyAV."""
    import av
    tail = deque(maxlen=offset)
    with av.open(str(path)) as source:
        if not source.streams.video:
            raise ValueError("The preceding montage clip has no video stream.")
        for frame in source.decode(source.streams.video[0]):
            if (frame.width, frame.height) != (width, height):
                raise ValueError("The preceding montage clip changed dimensions.")
            tail.append(frame.to_ndarray(format="rgb24"))
    if not tail:
        raise ValueError("The verified preceding montage clip could not be decoded.")
    return torch.from_numpy(tail[0].astype("float32") / 255)[None]


class NativeMontage(NativeMotion):
    def generate_shot(self, shot, audio, images, prompt, seed):
        with self.torch.no_grad():
            self.clip = self.nodes.CLIPLoader().load_clip(self.request["encoder"], "minimax", "default")[0]
            frame_plan = shot_frames(shot)
            conditioning, latent = tuple(self.encode.execute(self.clip, prompt, self.request["width"], self.request["height"],
                frame_plan["rawFrames"], "match", self.video_vae, self.audio_vae,
                ref_images={"ref_image_" + str(i): image for i, image in enumerate(images)},
                ref_audios={"ref_audio_0": audio}))
            self.clip = None
            self.memory.unload_all_models()
            gc.collect()
            self.memory.soft_empty_cache()
            model = self.nodes.UNETLoader().load_unet(self.request["model"], "default")[0]
            self.model = self.shift.execute(model, 12.0, 3.0)[0]
            noise = self.noise.get_noise(seed)[0]
            guider = self.guider.get_guider(self.model, conditioning)[0]
            sigmas = self.scheduler.get_sigmas(self.model, "simple", self.request.get("steps", 20), 1.0)[0]
            sampler = self.sampler_select.get_sampler("res_multistep")[0]
            result = self.sampler.sample(noise, guider, sampler, sigmas, latent)[0]
            return self.tensors(result)["video"]


def encode_shot(native, shot, video, destination):
    # Comfy's node executor normally supplies this guard. This offline adapter
    # must cover lazy VAE iteration and generator cleanup as well as sampling.
    with native.torch.no_grad():
        _encode_shot_without_gradients(native, shot, video, destination)


def _encode_shot_without_gradients(native, shot, video, destination):
    import av
    import numpy as np
    plan = shot_frames(shot)
    frames = native.streamer._generated_frame_generator(native.video_vae, [video], [plan["rawFrames"]], [0], 0, "lopata_any_song")
    written = 0
    try:
        with av.open(str(destination), "w", format="mp4") as output:
            stream = output.add_stream("libx264", rate=FPS)
            stream.width, stream.height = native.request["width"], native.request["height"]
            stream.pix_fmt = "yuv420p"
            stream.time_base = stream.codec_context.time_base = Fraction(1, FPS)
            stream.options = {"crf": str(max(10, min(35, round(36 - native.request.get("quality", 85) / 5)))), "preset": "medium"}
            for image in frames:
                if written >= plan["outputFrames"]:
                    break
                rgb = (image.clamp(0, 1).numpy() * 255).round().astype(np.uint8)
                frame = av.VideoFrame.from_ndarray(rgb, format="rgb24")
                frame.pts, frame.time_base = written, Fraction(1, FPS)
                for packet in stream.encode(frame):
                    output.mux(packet)
                del image, rgb, frame
                written += 1
            if written != plan["outputFrames"]:
                raise ValueError("The H3 montage shot did not cover its accepted slot.")
            for packet in stream.encode():
                output.mux(packet)
    finally:
        frames.close()
        gc.collect()
        native.memory.soft_empty_cache()


def verify_media(request, path, identity):
    result = subprocess.run([request["ffprobe"], "-v", "error", "-count_frames", "-select_streams", "v:0",
                             "-show_entries", "stream=width,height,avg_frame_rate,nb_read_frames,duration", "-of", "json", str(path)],
                            check=True, stdout=subprocess.PIPE, stderr=subprocess.PIPE, text=True, encoding="utf-8")
    stream = json.loads(result.stdout)["streams"][0]
    facts = {"width": int(stream["width"]), "height": int(stream["height"]),
             "frames": int(stream["nb_read_frames"]), "fps": str(stream["avg_frame_rate"])}
    if (facts["width"] != request["width"] or facts["height"] != request["height"]
            or facts["frames"] != identity["outputFrames"] or Fraction(facts["fps"]) != FPS):
        raise ValueError("The montage MP4 dimensions, frame count or frame rate do not match its receipt.")
    return facts


def assembly_command(request, joined, target, duration):
    return [request["ffmpeg"], "-nostdin", "-v", "error", "-y", "-i", str(joined),
            "-i", request["song"], "-map", "0:v:0", "-map", "1:a:0", "-map_metadata", "-1", "-map_chapters", "-1",
            "-c:v", "copy", "-c:a", "aac", "-b:a", "192k",
            "-t", format(duration, ".9f"), "-movflags", "+faststart+use_metadata_tags", str(target)]


def assemble(request, state, receipts, duration):
    joined = contained(state.root, "montage-joined.partial.mp4")
    scaled = contained(state.root, "montage-scaled.partial.mp4")
    staging, target = contained(state.root, "montage-raw.partial.mp4"), contained(state.root, "montage-raw.mp4")
    try:
        join_video([(state.data_path(item), item["identity"]["outputFrames"]) for item in receipts],
                   joined, FPS, request["width"], request["height"])
        width, height = request.get("outputWidth", request["width"]), request.get("outputHeight", request["height"])
        video = joined
        if (width, height) != (request["width"], request["height"]):
            scale_video(joined, scaled, FPS, width, height, request.get("quality", 85))
            video = scaled
        result = subprocess.run(assembly_command(request, video, staging, duration), stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        if result.returncode:
            raise RuntimeError("Montage assembly failed: " + result.stderr.decode("utf-8", "replace")[-8000:])
        with staging.open("rb+") as stream:
            os.fsync(stream.fileno())
        reject_links(target)
        os.replace(staging, target)
    finally:
        for partial in (staging, joined, scaled):
            if partial.exists():
                partial.unlink()
    return target


def operation_identity(request, duration, files, facts):
    return {"generationMode": "montage", "sourceRevision": SOURCE_REVISION, "anySongSourceRevision": ANY_SONG_REVISION,
            "sourceSha256": {name: file_hash(path) for name, path in files.items()}, "pins": request["pins"],
            "songSha256": file_hash(request["song"]), "references": facts.get("referenceInputs", []),
            "montagePlan": request["montagePlan"], "referencePromptSha256": facts["referencePromptSha256"],
            "adapterSha256": file_hash(__file__),
            "stateAdapterSha256": file_hash(Path(__file__).with_name("song_clip_montage_state.py")),
            "mediaAdapterSha256": file_hash(Path(__file__).with_name("song_clip_montage_media.py")),
            "motionAdapterSha256": file_hash(Path(__file__).with_name("song_clip_motion.py")),
            "sharedStateSha256": file_hash(Path(__file__).with_name("song_clip_motion_state.py")),
            "duration": duration, "framesPerSecond": FPS,
            "settings": {key: request.get(key) for key in ("modelId", "model", "encoder", "videoVae", "audioVae", "width", "height",
                "outputWidth", "outputHeight", "quality", "steps", "seed", "device", "memoryMode", "vramReserveGb", "clipSeconds", "halfTime")}}


def verify_result(request, request_file, state, identity, facts, receipts, previous_hash, duration):
    """Read-only acceptance of an already committed run; never re-mux or rewrite."""
    if request_file is None:
        raise ValueError("A source request file is required for montage receipt verification.")
    result = read_document(contained(state.root, "worker-result.json"))
    target = contained(state.root, "montage-raw.mp4")
    expected = {"output": str(target), "duration": duration, "frames": math.ceil(duration * FPS),
                "outputSha256": file_hash(target), "outputBytes": target.stat().st_size,
                "completedChunks": len(receipts), "completedShots": len(receipts),
                "inputHash": state.input_hash, "seed": state.seed, "sourceRevision": SOURCE_REVISION,
                "anySongSourceRevision": ANY_SONG_REVISION, "generationMode": "montage",
                "montagePlanInputHash": request["montagePlan"]["inputHash"],
                "montagePlanSha256": object_hash(request["montagePlan"]), "checkpointHash": previous_hash,
                "audioMuxRequired": False, "actualAdapterSha256": identity["adapterSha256"],
                "hostRequestSha256": file_hash(request_file), "shotReceipts": receipts, **facts}
    if (not isinstance(result, dict) or any(canonical(result.get(key)) != canonical(value) for key, value in expected.items())
            or type(result.get("seconds")) not in (int, float) or not math.isfinite(result["seconds"]) or result["seconds"] < 0):
        raise ValueError("The completed montage receipt no longer matches its actual inputs, routes, prompts or artifacts.")
    return result


def run(request, request_file=None, verify_only=False):
    started = time.monotonic()
    validate_request(request)
    job = Path(request["job"]).absolute()
    if verify_only and (not job.is_dir() or not contained(job, "montage-state.json").is_file()):
        raise ValueError("The montage verification requires an existing committed state.")
    job.mkdir(parents=True, exist_ok=True)
    with operation_lock(job):
        duration = probe_audio_seconds(request)
        references = reference_inputs(request)
        shots = validate_plan(request.get("montagePlan"), duration, references)
        files = source_files(request)
        director = load_module("lopata_anysong_director", files["director_parser.py"])
        selector, composer = director.MiniMaxH3ReferenceSelector(), director.MiniMaxH3PromptComposer()
        routes = [route_shot(selector, shot, references) for shot in shots]
        prompts = [compose_prompt(composer, request["montagePlan"], shot, references, route) for shot, route in zip(shots, routes)]
        facts = reference_receipt(request)
        facts["referencePromptSha256"] = [hashlib.sha256(prompt.encode("utf-8")).hexdigest() for prompt in prompts]
        identity = operation_identity(request, duration, files, facts)
        state = MontageState(job, identity, request.get("seed", -1), read_only=verify_only)
        native = None
        receipts, previous_hash = [], state.input_hash
        verify = lambda path, item: verify_media(request, path, item)
        emit("plan", inputHash=state.input_hash, seed=state.seed, total=len(shots), duration=duration,
             frames=math.ceil(duration * FPS), generationMode="montage")
        try:
            for shot, route, prompt in zip(shots, routes, prompts):
                receipt = state.load(shot, previous_hash, verify)
                if receipt is None:
                    if verify_only:
                        raise ValueError("The montage verification found an incomplete shot; generation was not started.")
                    if native is None:
                        native = NativeMontage(request, job)
                        native.chunk_count = len(shots)
                        master = decode_master_audio(request["song"], native.torch)
                        native.load_vaes()
                    native.chunk_index = shot["index"]
                    images = []
                    import numpy as np
                    from PIL import Image, ImageOps
                    for index in route["referenceIndices"]:
                        with Image.open(references[index - 1]["file"]) as image:
                            image = ImageOps.exif_transpose(image).convert("RGB")
                            image.thumbnail((2048, 2048))
                            images.append(native.torch.from_numpy(np.array(image).astype("float32") / 255)[None])
                    if route["previousFrame"]:
                        images.append(previous_frame(state.data_path(receipts[-1]), native.torch,
                                                     request["width"], request["height"]))
                    emit("chunk-start", index=shot["index"], total=len(shots), startFrame=shot_frames(shot)["startFrame"],
                         fraction=.08 + .77 * (shot["index"] - 1) / len(shots), message=route["description"])
                    video = native.generate_shot(shot, slice_audio(master, shot), images, prompt,
                                                 (state.seed + shot["index"] - 1) % 2**53)
                    native.release_sampling()
                    if file_hash(request["song"]) != identity["songSha256"] or reference_receipt(request).get("referenceInputs", []) != identity["references"]:
                        raise ValueError("A source attachment changed during montage generation.")
                    receipt = state.commit(shot, previous_hash, lambda path: encode_shot(native, shot, video, path), verify, route)
                    del video, images
                    emit("chunk", index=shot["index"], total=len(shots), checkpoint=receipt, files=state.files(receipt),
                         fraction=.08 + .77 * shot["index"] / len(shots))
                else:
                    if receipt["route"] != route:
                        raise ValueError("The completed montage shot has different routing facts.")
                    emit("cached", index=shot["index"], total=len(shots), checkpoint=receipt,
                         fraction=.08 + .77 * shot["index"] / len(shots))
                receipts.append(receipt)
                previous_hash = receipt["data"]["sha256"]
            if verify_only:
                result = verify_result(request, request_file, state, identity, facts, receipts, previous_hash, duration)
                emit("verified", fraction=1, message="Existing montage checkpoints, prompts and result were verified", **result)
                return result
            emit("stage", stage="montage-assembly", fraction=.9, message="Assembling accepted montage shots with the complete original song")
            output = assemble(request, state, receipts, duration)
            if file_hash(request["song"]) != identity["songSha256"] or reference_receipt(request).get("referenceInputs", []) != identity["references"]:
                raise ValueError("A source attachment changed before the montage result was accepted.")
            if {name: file_hash(path) for name, path in files.items()} != identity["sourceSha256"]:
                raise ValueError("The audited Any Song helpers changed during montage generation.")
            result = {"output": str(output), "duration": duration, "frames": math.ceil(duration * FPS),
                      "outputSha256": file_hash(output), "outputBytes": output.stat().st_size,
                      "seconds": time.monotonic() - started, "completedChunks": len(receipts), "completedShots": len(receipts),
                      "inputHash": state.input_hash, "seed": state.seed, "sourceRevision": SOURCE_REVISION,
                      "anySongSourceRevision": ANY_SONG_REVISION, "generationMode": "montage",
                      "montagePlanInputHash": request["montagePlan"]["inputHash"],
                      "montagePlanSha256": object_hash(request["montagePlan"]), "checkpointHash": previous_hash,
                      "audioMuxRequired": False, "actualAdapterSha256": identity["adapterSha256"],
                      "shotReceipts": receipts, **facts}
            if request_file is not None:
                result["hostRequestSha256"] = file_hash(request_file)
            atomic_json(contained(job, "worker-result.json"), result)
            emit("complete", fraction=1, message="The complete montage music video is ready", **result)
            return result
        finally:
            if native is not None:
                native.release_sampling()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    try:
        analysis_mode = len(sys.argv) == 3 and sys.argv[1] == "--analyze"
        verify_mode = len(sys.argv) == 3 and sys.argv[1] == "--verify"
        source = sys.argv[2] if analysis_mode or verify_mode else sys.argv[1]
        payload = json.loads(Path(source).read_text(encoding="utf-8-sig"))
        analyze(payload) if analysis_mode else run(payload, source, verify_only=verify_mode)
    except Exception as error:
        emit("error", fraction=0, message=str(error), type=type(error).__name__)
        logging.exception("H3 Any Song montage failed")
        sys.exit(1)
