"""Offline JSON-lines worker for the AI HUB Heavy Qwen2.5-Omni pipeline."""

from __future__ import annotations

import contextlib
import gc
import importlib.metadata
import importlib.util
import json
import os
import queue
import sys
import threading
import time
import traceback
from pathlib import Path


PROTOCOL_STDOUT = sys.stdout
sys.stdout = sys.stderr


def package_version(name: str) -> str:
    try:
        return importlib.metadata.version(name)
    except importlib.metadata.PackageNotFoundError:
        return "missing"


def respond(request_id: int, **values) -> None:
    PROTOCOL_STDOUT.write(json.dumps({"id": request_id, **values}, ensure_ascii=False) + "\n")
    PROTOCOL_STDOUT.flush()


def stream(request_id: int, text: str) -> None:
    if text:
        respond(request_id, event="stream", text=text)


def extract_generated_sequences(result):
    sequences = getattr(result, "sequences", result)
    shape = getattr(sequences, "shape", None)
    if shape is None or len(shape) != 2:
        raise TypeError("Omni Thinker returned an unsupported generation result.")
    return sequences


def non_cuda_device_entries(device_map: dict) -> dict:
    """Return every placement that would make Heavy use CPU or disk offload."""
    rejected = {}
    for module_name, placement in device_map.items():
        normalized = str(placement).strip().lower()
        if placement == 0 or normalized in {"cuda", "cuda:0", "0"}:
            continue
        rejected[str(module_name)] = str(placement)
    return rejected


def resolve_cuda_device_map(model, device_map: dict) -> tuple[dict, dict]:
    """Validate Accelerate's map or infer full-GPU placement from real parameters."""
    if device_map:
        return device_map, non_cuda_device_entries(device_map)

    rejected = {}
    parameter_count = 0
    first_cuda_device = None
    for parameter_name, parameter in model.named_parameters():
        parameter_count += 1
        device = str(getattr(parameter, "device", "missing")).strip().lower()
        if device.startswith("cuda:") or device == "cuda":
            first_cuda_device = first_cuda_device or device
            continue
        if len(rejected) < 24:
            rejected[str(parameter_name)] = device

    if parameter_count == 0:
        rejected["parameters"] = "missing"
    if rejected:
        return {}, rejected
    return {"": first_cuda_device or "cuda:0"}, {}


class OmniWorker:
    def __init__(self) -> None:
        self.model = None
        self.processor = None
        self.process_mm_info = None
        self.torch = None
        self.model_directory = ""
        self.cpu_budget = 0
        self.gpu_budget = 0
        self.device_map = {}
        self.runtime_profile = "unloaded"
        self.attention_implementation = "unavailable"
        self.last_diagnostics = "worker_created"
        self.attention_telemetry = {"calls": {}, "selections": {}}
        self.partial_content = []

    def probe(self, model_directory: str) -> dict:
        root = Path(model_directory).resolve() if model_directory else None
        model_present = bool(root and (root / "config.json").is_file() and (root / "model.safetensors.index.json").is_file())
        result = {
            "pythonVersion": sys.version.split()[0],
            "torchVersion": package_version("torch"),
            "transformersVersion": package_version("transformers"),
            "accelerateVersion": package_version("accelerate"),
            "qwenOmniUtilsVersion": package_version("qwen-omni-utils"),
            "flashAttentionVersion": package_version("flash-attn"),
            "modelPresent": model_present,
            "cudaAvailable": False,
            "cudaDeviceCount": 0,
        }
        try:
            import torch

            result["cudaAvailable"] = bool(torch.cuda.is_available())
            result["cudaDeviceCount"] = int(torch.cuda.device_count())
            result["cudaVersion"] = torch.version.cuda or "none"
            result["torchFlashSdpaEnabled"] = bool(
                torch.cuda.is_available() and torch.backends.cuda.flash_sdp_enabled()
            )
        except Exception:
            result["cudaVersion"] = "unavailable"
            result["torchFlashSdpaEnabled"] = False
        return result

    def warmup(self, model_directory: str, cpu_budget: int, gpu_budget: int) -> dict:
        root = Path(model_directory).resolve()
        required = (root / "config.json", root / "model.safetensors.index.json")
        if not all(path.is_file() for path in required):
            raise FileNotFoundError("The verified Qwen2.5-Omni model directory is incomplete.")
        self.model_directory = str(root)
        self.cpu_budget = max(1, int(cpu_budget))
        self.gpu_budget = max(0, int(gpu_budget))
        return self._load_profile("thinker")

    def generate_text(self, request_id: int, image_path: str, messages: list[dict]) -> dict:
        request_started = time.perf_counter()
        self.partial_content = []
        self.attention_telemetry["calls"].clear()
        self.attention_telemetry["selections"].clear()
        before = self._memory_snapshot()
        cleanup_started = time.perf_counter()
        self._clear_unused_cache()
        cleanup_ms = round((time.perf_counter() - cleanup_started) * 1000)
        if self.torch is not None and self.torch.cuda.is_available():
            self.torch.cuda.reset_peak_memory_stats()
        after_cleanup = self._memory_snapshot()
        result = None
        try:
            result = self._generate_text(request_id, image_path, messages)
            result["memoryBeforeCleanup"] = before
            result["memoryBeforeRequest"] = after_cleanup
            result["cacheCleanupMilliseconds"] = cleanup_ms
            result["attentionTelemetry"] = self.attention_telemetry
            return result
        except Exception as exc:
            # Completed inner frames can otherwise retain their CUDA tensors.
            traceback.clear_frames(exc.__traceback__)
            raise
        finally:
            completed = self._memory_snapshot()
            release_started = time.perf_counter()
            self._clear_unused_cache()
            released = self._memory_snapshot()
            if result is not None:
                result["memoryAfterGeneration"] = completed
                result["memoryAfterCleanup"] = released
                result["postRequestCleanupMilliseconds"] = round((time.perf_counter() - release_started) * 1000)
                result["requestWallMilliseconds"] = round((time.perf_counter() - request_started) * 1000)
            self.last_diagnostics += "; memoryAfterCleanup=" + json.dumps(released)

    def _memory_snapshot(self) -> dict:
        torch = self.torch
        if torch is None or not torch.cuda.is_available():
            return {}
        free, total = torch.cuda.mem_get_info()
        return dict(allocatedBytes=torch.cuda.memory_allocated(),
                    reservedBytes=torch.cuda.memory_reserved(),
                    peakAllocatedBytes=torch.cuda.max_memory_allocated(),
                    freeBytes=free, totalBytes=total)

    def _clear_unused_cache(self) -> None:
        gc.collect()
        if self.torch is not None and self.torch.cuda.is_available():
            self.torch.cuda.empty_cache()

    def _generate_text(self, request_id: int, image_path: str, messages: list[dict]) -> dict:
        profile_switch_milliseconds = self._ensure_profile("thinker")
        conversation = self._build_conversation(image_path, messages)
        started = time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            preprocessing_started = time.perf_counter()
            text = self.processor.apply_chat_template(
                conversation,
                add_generation_prompt=True,
                tokenize=False,
            )
            audios, images, videos = self.process_mm_info(conversation, use_audio_in_video=False)
            inputs = self.processor(
                text=text,
                audio=audios,
                images=images,
                videos=videos,
                return_tensors="pt",
                padding=True,
                use_audio_in_video=False,
            )
            inputs = inputs.to(self.model.device).to(self.model.dtype)
            respond(request_id, event="diagnostic", diagnostics=json.dumps(dict(
                stage="inputs_ready", shapes={k: list(v.shape) for k, v in inputs.items() if hasattr(v, "shape")},
                memory=self._memory_snapshot()), ensure_ascii=False))
            preprocessing_milliseconds = round(
                (time.perf_counter() - preprocessing_started) * 1000
            )
            input_tokens = int(inputs["input_ids"].shape[1])
            max_context = self._max_context_tokens()
            max_new_tokens = self._context_output_budget(input_tokens, max_context)
            # The checkpoint's generation_config.json can omit EOS even though
            # Thinker/tokenizer define it. Pass the same boundary to generation
            # and validation; checking it only after generate() is too late.
            eos_token_ids = self._text_eos_token_ids()
            pad_token_id = getattr(self.processor.tokenizer, "pad_token_id", None)
            if pad_token_id is None:
                pad_token_id = eos_token_ids[0]
            self.last_diagnostics = (
                f"stage=text_generation; profile={self.runtime_profile}; inputTokens={input_tokens}; "
                f"maxNewTokens={max_new_tokens}; eosTokenIds={eos_token_ids}; padTokenId={pad_token_id}"
            )
            from transformers import TextIteratorStreamer

            streamer = TextIteratorStreamer(
                self.processor.tokenizer,
                skip_prompt=True,
                skip_special_tokens=True,
                timeout=0.25,
                clean_up_tokenization_spaces=False,
            )
            generation_result = []
            generation_error = []
            generation_started = time.perf_counter()

            def run_generation() -> None:
                try:
                    generation_result.append(
                        self.model.generate(
                            **inputs,
                            use_audio_in_video=False,
                            max_new_tokens=max_new_tokens,
                            eos_token_id=eos_token_ids,
                            pad_token_id=pad_token_id,
                            return_dict_in_generate=True,
                            streamer=streamer,
                        )
                    )
                except BaseException as exc:  # relayed on the protocol thread
                    generation_error.append(exc)

            generation_thread = threading.Thread(target=run_generation, name="omni-thinker", daemon=False)
            generation_thread.start()
            streamed_parts = self.partial_content
            first_token_at = None
            iterator = iter(streamer)
            next_diagnostic = generation_started + 15
            try:
                while generation_thread.is_alive():
                    if time.perf_counter() >= next_diagnostic:
                        respond(request_id, event="diagnostic", diagnostics=json.dumps(dict(
                            stage="decoding" if first_token_at is not None else "waiting_first_text",
                            elapsedMilliseconds=round((time.perf_counter() - generation_started) * 1000),
                            textCharacters=sum(map(len, streamed_parts)), memory=self._memory_snapshot(),
                            attention=self.attention_telemetry), ensure_ascii=False))
                        next_diagnostic = time.perf_counter() + 15
                    try:
                        part = next(iterator)
                    except queue.Empty:
                        continue
                    except StopIteration:
                        break
                    if part and first_token_at is None:
                        first_token_at = time.perf_counter()
                    streamed_parts.append(part)
                    stream(request_id, part)
            finally:
                # Do not free request tensors while the generation thread still owns them.
                generation_thread.join()
            while not generation_error:
                try:
                    part = next(iterator)
                except (queue.Empty, StopIteration):
                    break
                if part and first_token_at is None:
                    first_token_at = time.perf_counter()
                streamed_parts.append(part)
                stream(request_id, part)
            if generation_error:
                raise generation_error[0]
            if not generation_result:
                raise RuntimeError("Omni generation ended without a result.")
        sequences = extract_generated_sequences(generation_result[0])
        output_ids = sequences[:, input_tokens:]
        generated_tokens = int(output_ids.shape[1])
        self._validate_text_completion(output_ids, eos_token_ids, max_new_tokens)
        decoded_content = self.processor.batch_decode(
            output_ids,
            skip_special_tokens=True,
            clean_up_tokenization_spaces=False,
        )[0].strip()
        content = "".join(streamed_parts).strip() or decoded_content
        generation_milliseconds = round((time.perf_counter() - generation_started) * 1000)
        time_to_first_token_milliseconds = (
            round((first_token_at - generation_started) * 1000)
            if first_token_at is not None
            else generation_milliseconds
        )
        decode_milliseconds = max(0, generation_milliseconds - time_to_first_token_milliseconds)
        decode_tokens = max(0, generated_tokens - 1)
        decode_tokens_per_second = (
            round(decode_tokens * 1000 / decode_milliseconds, 3)
            if decode_milliseconds > 0
            else 0.0
        )
        elapsed = round((time.perf_counter() - started) * 1000)
        return {
            "content": content,
            "elapsedMilliseconds": elapsed,
            "inputTokens": input_tokens,
            "generatedTokens": generated_tokens,
            "maxContextTokens": max_context,
            "finishReason": "eos",
            "eosTokenIds": eos_token_ids,
            "lastTokenId": int(output_ids[0, -1]),
            "preprocessingMilliseconds": preprocessing_milliseconds,
            "generationMilliseconds": generation_milliseconds,
            "timeToFirstTokenMilliseconds": time_to_first_token_milliseconds,
            "decodeTokensPerSecond": decode_tokens_per_second,
            "profileSwitchMilliseconds": profile_switch_milliseconds,
            "runtimeProfile": self.runtime_profile,
            "attentionImplementation": self.attention_implementation,
            "deviceMap": self.device_map,
        }

    def speak(
        self,
        text: str,
        speaker: str,
        output_path: str,
        volume: float,
        speed: float,
    ) -> dict:
        profile_switch_milliseconds = self._ensure_profile("omni")
        if speaker not in {"Ethan", "Chelsie"}:
            raise ValueError("Unsupported Omni speaker.")
        prompt = (
            "Read the following text exactly as written. Do not add, omit, explain, or paraphrase anything.\n\n"
            + text.strip()
        )
        conversation = [
            {
                "role": "system",
                "content": [
                    {
                        "type": "text",
                        "text": (
                            "You are Qwen, a virtual human developed by the Qwen Team, "
                            "Alibaba Group, capable of perceiving auditory and visual inputs, "
                            "as well as generating text and speech."
                        ),
                    }
                ],
            },
            {"role": "user", "content": [{"type": "text", "text": prompt}]},
        ]
        started = time.perf_counter()
        with contextlib.redirect_stdout(sys.stderr):
            rendered = self.processor.apply_chat_template(conversation, add_generation_prompt=True, tokenize=False)
            audios, images, videos = self.process_mm_info(conversation, use_audio_in_video=False)
            inputs = self.processor(
                text=rendered,
                audio=audios,
                images=images,
                videos=videos,
                return_tensors="pt",
                padding=True,
                use_audio_in_video=False,
            )
            inputs = inputs.to(self.model.device).to(self.model.dtype)
            _, audio = self.model.generate(
                **inputs,
                speaker=speaker,
                generation_mode="audio",
                use_audio_in_video=False,
            )
        if audio is None:
            raise RuntimeError("Omni Talker produced no audio.")
        import numpy as np
        import soundfile as sf

        samples = audio.detach().cpu().numpy() if hasattr(audio, "detach") else np.asarray(audio)
        samples = np.asarray(samples, dtype=np.float32).reshape(-1)
        speed = min(max(float(speed), 0.7), 1.6)
        volume = min(max(float(volume), 0.0), 1.0)
        if abs(speed - 1.0) > 0.001 and samples.size > 1:
            source = np.arange(samples.size, dtype=np.float64)
            target = np.arange(0, samples.size - 1, speed, dtype=np.float64)
            samples = np.interp(target, source, samples).astype(np.float32)
        samples = np.clip(samples * volume, -1.0, 1.0)
        path = Path(output_path).resolve()
        path.parent.mkdir(parents=True, exist_ok=True)
        sf.write(str(path), samples, samplerate=24000)
        elapsed = round((time.perf_counter() - started) * 1000)
        return {
            "audioPath": str(path),
            "generationMilliseconds": elapsed,
            "timeToFirstAudioMilliseconds": elapsed + profile_switch_milliseconds,
            "profileSwitchMilliseconds": profile_switch_milliseconds,
            "runtimeProfile": self.runtime_profile,
            "attentionImplementation": self.attention_implementation,
            "deviceMap": self.device_map,
        }

    def shutdown(self) -> None:
        self._release_model()
        self.processor = None
        self.process_mm_info = None
        self.torch = None
        self.model_directory = ""
        self.cpu_budget = 0
        self.gpu_budget = 0
        self.device_map = {}
        self.runtime_profile = "unloaded"
        self.attention_implementation = "unavailable"

    def _load_profile(self, profile: str) -> dict:
        if profile not in {"thinker", "omni"}:
            raise ValueError(f"Unsupported Qwen2.5-Omni runtime profile: {profile}")
        if self.model is not None and self.runtime_profile == profile:
            return self._profile_result(already_loaded=True, load_milliseconds=0)
        if not self.model_directory:
            raise RuntimeError("Qwen2.5-Omni has no verified model directory for profile switching.")

        started = time.perf_counter()
        try:
            import torch
            from qwen_omni_utils import process_mm_info
            from transformers import (
                Qwen2_5OmniForConditionalGeneration,
                Qwen2_5OmniProcessor,
                Qwen2_5OmniThinkerForConditionalGeneration,
            )
        except Exception as exc:
            raise RuntimeError(
                "The isolated Heavy runtime is unavailable. Required packages: CUDA PyTorch, "
                "Transformers with Qwen2.5-Omni support, Accelerate and qwen-omni-utils."
            ) from exc

        self._release_model()
        if not torch.cuda.is_available():
            raise RuntimeError(
                "gpu_only_required: Heavy Qwen2.5-Omni-3B requires a CUDA GPU. "
                "CPU execution and CPU offload are disabled."
            )
        max_memory: dict[object, int] = {"cpu": self.cpu_budget}
        if self.gpu_budget > 0:
            max_memory[0] = self.gpu_budget
        attention_implementation = self._select_attention_implementation(torch)
        model_class = (
            Qwen2_5OmniThinkerForConditionalGeneration
            if profile == "thinker"
            else Qwen2_5OmniForConditionalGeneration
        )
        root = Path(self.model_directory)
        with contextlib.redirect_stdout(sys.stderr):
            model = model_class.from_pretrained(
                str(root),
                dtype=torch.bfloat16,
                device_map="auto",
                max_memory=max_memory,
                low_cpu_mem_usage=True,
                local_files_only=True,
                attn_implementation=attention_implementation,
            )
            if self.processor is None:
                self.processor = Qwen2_5OmniProcessor.from_pretrained(
                    str(root),
                    local_files_only=True,
                )
            model.eval()

        self.model = model
        if attention_implementation == "aihub_omni_sdpa":
            from omni_attention import attach_telemetry
            attach_telemetry(model, self.attention_telemetry)
        self.process_mm_info = process_mm_info
        self.torch = torch
        reported_device_map = getattr(model, "hf_device_map", {}) or {}
        self.device_map, rejected_placements = resolve_cuda_device_map(
            model,
            reported_device_map,
        )
        if rejected_placements:
            placement_summary = json.dumps(
                rejected_placements,
                ensure_ascii=False,
                sort_keys=True,
            )
            self.last_diagnostics = (
                f"stage=warm_rejected; profile={profile}; model={root.name}; "
                f"gpuBudgetBytes={self.gpu_budget}; rejectedPlacements={placement_summary}"
            )
            self._release_model()
            raise RuntimeError(
                "gpu_only_required: Qwen2.5-Omni-3B did not fit entirely on the GPU. "
                "Close another GPU-heavy program and restart Heavy. "
                f"Rejected placements: {placement_summary}"
            )
        self.runtime_profile = profile
        self.attention_implementation = attention_implementation
        elapsed = round((time.perf_counter() - started) * 1000)
        self.last_diagnostics = (
            f"stage=warm; profile={profile}; model={root.name}; torch={torch.__version__}; "
            f"cuda={torch.version.cuda}; devices={torch.cuda.device_count()}; "
            f"attention={attention_implementation}; cpuBudgetBytes={self.cpu_budget}; "
            f"gpuBudgetBytes={self.gpu_budget}; deviceMapEntries={len(self.device_map)}"
        )
        return self._profile_result(already_loaded=False, load_milliseconds=elapsed)

    def _ensure_profile(self, profile: str) -> int:
        result = self._load_profile(profile)
        return int(result["loadMilliseconds"])

    def _profile_result(self, already_loaded: bool, load_milliseconds: int) -> dict:
        return {
            "alreadyLoaded": already_loaded,
            "loadMilliseconds": load_milliseconds,
            "deviceMap": self.device_map,
            "runtimeProfile": self.runtime_profile,
            "attentionImplementation": self.attention_implementation,
        }

    @staticmethod
    def _select_attention_implementation(torch) -> str:
        if torch.cuda.is_available() and importlib.util.find_spec("flash_attn") is not None:
            return "flash_attention_2"
        from omni_attention import register
        return register()

    def _release_model(self) -> None:
        model = self.model
        torch = self.torch
        self.model = None
        self.device_map = {}
        self.runtime_profile = "unloaded"
        if model is not None:
            del model
        gc.collect()
        if torch is not None and torch.cuda.is_available():
            torch.cuda.empty_cache()
            torch.cuda.ipc_collect()

    def _build_conversation(self, image_path: str, messages: list[dict]) -> list[dict]:
        path = Path(image_path).resolve()
        if not path.is_file():
            raise FileNotFoundError("The source image is unavailable.")
        result = []
        for item in messages:
            role = str(item.get("role", "")).strip()
            content = str(item.get("content", ""))
            if role not in {"user", "assistant"} or not content:
                raise ValueError("The hidden conversation contains an invalid message.")
            if bool(item.get("includesImage")):
                payload = [
                    {"type": "image", "image": str(path)},
                    {"type": "text", "text": content},
                ]
            else:
                payload = [{"type": "text", "text": content}]
            result.append({"role": role, "content": payload})
        return result

    def _max_context_tokens(self) -> int:
        candidates = [self.model.config]
        for name in ("thinker_config", "text_config"):
            value = getattr(candidates[-1], name, None)
            if value is not None:
                candidates.append(value)
        for item in reversed(candidates):
            value = getattr(item, "max_position_embeddings", None)
            if isinstance(value, int) and value > 0:
                return value
        return 32768

    def _text_eos_token_ids(self) -> list[int]:
        eos_values = set()
        model_config = getattr(self.model, "config", None)
        thinker = getattr(self.model, "thinker", None)
        candidates = (
            getattr(self.model, "generation_config", None),
            model_config,
            getattr(model_config, "thinker_config", None),
            getattr(thinker, "generation_config", None),
            getattr(self.processor, "tokenizer", None),
        )
        for candidate in candidates:
            eos = getattr(candidate, "eos_token_id", None)
            values = eos if isinstance(eos, (list, tuple, set)) else (eos,)
            eos_values.update(value for value in values if type(value) is int and value >= 0)
        if not eos_values:
            raise RuntimeError("generation_config_invalid: Omni text EOS is unavailable.")
        return sorted(eos_values)

    @staticmethod
    def _context_output_budget(input_tokens: int, max_context: int) -> int:
        boundary = max_context * 95 // 100
        reserve = 4096  # Same policy as OmniContextBudget; includes reasoning and the final answer.
        if input_tokens + reserve >= boundary:
            raise RuntimeError(
                f"context_exhausted: input={input_tokens}; reserve={reserve}; boundary={boundary}; context={max_context}")
        return boundary - input_tokens

    @staticmethod
    def _validate_text_completion(output_ids, eos_token_ids: list[int], max_new_tokens: int) -> None:
        tokens = output_ids[0].tolist()
        eos_positions = [index for index, token in enumerate(tokens) if token in eos_token_ids]
        if eos_positions:
            if eos_positions[0] != len(tokens) - 1:
                raise RuntimeError("generation_boundary_invalid: Omni generated tokens after the first EOS.")
            return
        if len(tokens) >= max_new_tokens:
            raise RuntimeError("context_exhausted: Omni reached the safe context budget before EOS.")
        raise RuntimeError("generation_incomplete: Omni stopped before EOS.")

    def _ensure_loaded(self) -> None:
        if self.model is None or self.processor is None or self.runtime_profile == "unloaded":
            raise RuntimeError("Qwen2.5-Omni is not warmed up.")


def main() -> None:
    worker = OmniWorker()
    for line in sys.stdin:
        request_id = 0
        command = "decode_request"
        try:
            line = line.lstrip("\ufeff")
            envelope = json.loads(line)
            request_id = int(envelope.get("id", 0))
            payload = envelope.get("payload") or {}
            command = str(payload.get("command", ""))
            if command == "probe":
                respond(request_id, success=True, **worker.probe(payload.get("modelDirectory", "")))
            elif command == "plan":
                respond(
                    request_id,
                    success=True,
                    cpuBudgetBytes=int(payload.get("cpuBudgetBytes", 0)),
                    gpuBudgetBytes=int(payload.get("gpuBudgetBytes", 0)),
                )
            elif command == "warmup":
                result = worker.warmup(
                    payload.get("modelDirectory", ""),
                    int(payload.get("cpuBudgetBytes", 0)),
                    int(payload.get("gpuBudgetBytes", 0)),
                )
                respond(request_id, success=True, diagnostics=worker.last_diagnostics, **result)
            elif command in {"analyze", "compose", "revise"}:
                result = worker.generate_text(
                    request_id,
                    payload.get("imagePath", ""),
                    payload.get("messages") or [],
                )
                respond(request_id, success=True, **result)
            elif command == "speak":
                result = worker.speak(
                    payload.get("text", ""),
                    payload.get("speaker", "Ethan"),
                    payload.get("outputPath", ""),
                    float(payload.get("volume", 1.0)),
                    float(payload.get("speed", 1.0)),
                )
                respond(request_id, success=True, **result)
            elif command == "health":
                respond(
                    request_id,
                    success=True,
                    loaded=worker.model is not None,
                    runtimeProfile=worker.runtime_profile,
                    attentionImplementation=worker.attention_implementation,
                    deviceMap=worker.device_map,
                    diagnostics=worker.last_diagnostics,
                )
            elif command == "cancel":
                respond(request_id, success=True, cancelled=True)
            elif command == "shutdown":
                worker.shutdown()
                respond(request_id, success=True, shutdown=True)
                return
            else:
                raise ValueError(f"Unknown command: {command}")
        except Exception as exc:
            error_traceback = traceback.format_exc(limit=10)
            print(error_traceback, file=sys.stderr, flush=True)
            message = str(exc)
            error_code = "context_exhausted" if "context_exhausted" in message else "worker_error"
            if "runtime is unavailable" in message:
                error_code = "runtime_missing"
            if "gpu_only_required" in message:
                error_code = "gpu_only_required"
            respond(
                request_id,
                success=False,
                errorCode=error_code,
                errorStage=command,
                errorType=type(exc).__name__,
                error=message,
                diagnostics=worker.last_diagnostics,
                traceback=error_traceback,
                partialContent="".join(worker.partial_content) if command in {"analyze", "compose", "revise"} else "",
            )


if __name__ == "__main__":
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    os.environ.setdefault("HF_DATASETS_OFFLINE", "1")
    os.environ.setdefault("PYTHONNOUSERSITE", "1")
    main()
