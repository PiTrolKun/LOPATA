"""Offline JSON-lines worker for AI HUB image-analysis Kokoro speech."""

from __future__ import annotations

import contextlib
import faulthandler
import importlib.util
import json
import os
import re
import sys
import time
import traceback
import wave
from pathlib import Path

from kokoro_ru_tokenizer_compat import compatible_ru_tokenizer


PROTOCOL_STDOUT = sys.stdout
sys.stdout = sys.stderr


class _CompatibleTokenTypeIdsShim:
    """Keep ruaccent's tokenizer internals visible while restoring token types."""

    def __init__(self, tokenizer) -> None:
        self._wrapped = tokenizer

    def __call__(self, *args, **kwargs):
        import numpy as np

        encoding = self._wrapped(*args, **kwargs)
        if "token_type_ids" not in encoding:
            encoding["token_type_ids"] = np.zeros_like(encoding["input_ids"])
        return encoding

    def __getattr__(self, name):
        return getattr(self._wrapped, name)


class KokoroWorker:
    def __init__(self) -> None:
        self.device = 'cpu'
        self.language = ""
        self.model = None
        self.voice = None
        self.voice_name = ""
        self.pipeline = None
        self.g2p = None
        self.torch = None
        self.np = None
        self.last_diagnostics = "worker_created"

    def load(self, language: str, model_directory: str, device: str = 'cpu') -> tuple[bool, int]:
        self.device = device
        language = "en" if str(language).lower().startswith("en") else "ru"
        root = Path(model_directory).resolve()
        self.last_diagnostics = f"stage=load; language={language}; modelDirectory={root}; device={device}"
        if self.model is not None and self.language == language and str(self.model.device) == device:
            return True, 0

        started = time.perf_counter()
        tokenizer_compat = {"status": "not_applicable"}
        try:
            import numpy as np
            import torch
            from kokoro import KModel, KPipeline
        except Exception as exc:
            raise RuntimeError(
                "Kokoro Python runtime is unavailable. Required packages: "
                "kokoro 0.9.4 and its language dependencies."
            ) from exc

        if language == "en":
            config = root / "config.json"
            weights = root / "kokoro-v1_0.pth"
            voice_path = root / "voices" / "af_heart.pt"
        else:
            config = root / "kokoro-config.json"
            weights = root / "kokoro-ru-v2-base.pth"
            voice_path = root / "voices" / "sveta.pt"
        for required in (config, weights, voice_path):
            if not required.is_file():
                raise FileNotFoundError(f"Required Kokoro file is missing: {required.name}")

        with contextlib.redirect_stdout(sys.stderr):
            model = KModel(
                repo_id="hexgrad/Kokoro-82M" if language == "en" else "zaakirio/kokoro-ru",
                config=str(config),
                model=str(weights),
            ).to(device).eval()
            voice = torch.load(str(voice_path), map_location="cpu", weights_only=False)
            if language == "en":
                pipeline = KPipeline(
                    lang_code="a",
                    repo_id="hexgrad/Kokoro-82M",
                    model=False,
                    device="cpu",
                )
                g2p = None
            else:
                module_path = root / "ru_g2p.py"
                if not module_path.is_file():
                    raise FileNotFoundError("Required Kokoro file is missing: ru_g2p.py")
                spec = importlib.util.spec_from_file_location("aihub_kokoro_ru_g2p", module_path)
                if spec is None or spec.loader is None:
                    raise RuntimeError("The Russian Kokoro frontend could not be loaded.")
                module = importlib.util.module_from_spec(spec)
                spec.loader.exec_module(module)
                import ruaccent
                from ruaccent import RUAccent

                accent_root = (root / "ruaccent").resolve()
                if str(accent_root) not in ruaccent.__path__:
                    ruaccent.__path__.append(str(accent_root))
                original_load = RUAccent.load

                def offline_load(instance, *args, **kwargs):
                    instance.koziev_paths = []
                    kwargs["workdir"] = str(accent_root)
                    return original_load(instance, *args, **kwargs)

                RUAccent.load = offline_load
                module._TokenTypeIdsShim = _CompatibleTokenTypeIdsShim
                pipeline = None
                try:
                    with compatible_ru_tokenizer(accent_root) as tokenizer_compat:
                        g2p = module.RuG2P()
                finally:
                    RUAccent.load = original_load

        self.language = language
        self.model = model
        self.voice = voice
        self.voice_name = voice_path.stem
        self.pipeline = pipeline
        self.g2p = g2p
        self.torch = torch
        self.np = np
        voice_shape = tuple(voice.shape) if hasattr(voice, "shape") else type(voice).__name__
        frontend = type(pipeline).__name__ if pipeline is not None else type(g2p).__name__
        self.last_diagnostics = (
            f"stage=loaded; language={language}; device={self.device}; "
            f"torch={torch.__version__}; frontend={frontend}; "
            f"voice={self.voice_name}; voiceShape={voice_shape}; "
            f"ruTokenizerCompat={tokenizer_compat['status']}"
        )
        return False, round((time.perf_counter() - started) * 1000)

    def synthesize(
        self,
        text: str,
        output_path: str,
        volume: float = 1.0,
        speed: float = 1.0,
    ) -> int:
        if self.model is None or self.voice is None or self.torch is None or self.np is None:
            raise RuntimeError("Kokoro is not loaded.")
        started = time.perf_counter()
        volume = min(max(float(volume), 0.0), 1.0)
        speed = min(max(float(speed), 0.7), 1.6)
        chunks = split_text(text)
        self.last_diagnostics = (
            f"stage=synthesize; language={self.language}; device={self.device}; "
            f"voice={self.voice_name}; "
            f"textChars={len(text)}; textChunks={len(chunks)}"
        )
        audio_parts = []
        phoneme_count = 0
        for chunk in chunks:
            phoneme_chunks = self._phonemize(chunk)
            for phonemes in phoneme_chunks:
                if not phonemes:
                    continue
                if len(phonemes) > 510:
                    phonemes = phonemes[:510]
                phoneme_count += len(phonemes)
                style_index = min(max(len(phonemes) - 1, 0), len(self.voice) - 1)
                with self.torch.no_grad():
                    output = self.model(
                        phonemes,
                        self.voice[style_index],
                        speed,
                        return_output=True,
                    )
                audio_parts.append(output.audio.detach().cpu().numpy())

        if not audio_parts:
            raise RuntimeError("Kokoro produced no audio.")
        silence = self.np.zeros(2400, dtype=self.np.float32)
        joined = []
        for index, audio in enumerate(audio_parts):
            if index:
                joined.append(silence)
            joined.append(self.np.asarray(audio, dtype=self.np.float32))
        audio = self.np.concatenate(joined) * volume
        write_wave(Path(output_path), audio, self.np)
        self.last_diagnostics = (
            f"stage=synthesized; language={self.language}; device={self.device}; "
            f"voice={self.voice_name}; "
            f"textChars={len(text)}; textChunks={len(chunks)}; "
            f"phonemeChars={phoneme_count}; audioParts={len(audio_parts)}; "
            f"audioSamples={len(audio)}"
        )
        return round((time.perf_counter() - started) * 1000)

    def _phonemize(self, text: str) -> list[str]:
        if self.language == "ru":
            phonemes, _ = self.g2p(text)
            return [phonemes]
        results = []
        with contextlib.redirect_stdout(sys.stderr):
            for item in self.pipeline(text, voice=None):
                phonemes = getattr(item, "phonemes", None)
                if phonemes is None and isinstance(item, tuple) and len(item) > 1:
                    phonemes = item[1]
                if phonemes:
                    results.append(phonemes)
        return results


def split_text(text: str, maximum: int = 360) -> list[str]:
    normalized = re.sub(r"\s+", " ", text).strip()
    if not normalized:
        return []
    sentences = re.split(r"(?<=[.!?…])\s+", normalized)
    chunks: list[str] = []
    current = ""
    for sentence in sentences:
        pieces = [sentence[i : i + maximum] for i in range(0, len(sentence), maximum)]
        for piece in pieces:
            candidate = f"{current} {piece}".strip()
            if current and len(candidate) > maximum:
                chunks.append(current)
                current = piece
            else:
                current = candidate
    if current:
        chunks.append(current)
    return chunks


def write_wave(path: Path, audio, np) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    pcm = (np.clip(audio, -1.0, 1.0) * 32767.0).astype("<i2")
    with wave.open(str(path), "wb") as target:
        target.setnchannels(1)
        target.setsampwidth(2)
        target.setframerate(24000)
        target.writeframes(pcm.tobytes())


def respond(request_id: int, **values) -> None:
    payload = {"id": request_id, **values}
    PROTOCOL_STDOUT.write(json.dumps(payload, ensure_ascii=False) + "\n")
    PROTOCOL_STDOUT.flush()


def main() -> None:
    worker = KokoroWorker()
    for line in sys.stdin:
        request_id = 0
        command = "decode_request"
        try:
            envelope = json.loads(line)
            request_id = int(envelope.get("id", 0))
            payload = envelope.get("payload") or {}
            command = payload.get("command", "")
            if command == "load":
                already_loaded, elapsed = worker.load(
                    payload.get("languageCode", "ru"),
                    payload.get("modelDirectory", ""),
                )
                respond(
                    request_id,
                    success=True,
                    alreadyLoaded=already_loaded,
                    loadMilliseconds=elapsed,
                    diagnostics=worker.last_diagnostics,
                )
            elif command == "synthesize":
                elapsed = worker.synthesize(
                    payload.get("text", ""),
                    payload.get("outputPath", ""),
                    payload.get("volume", 1.0),
                    payload.get("speed", 1.0),
                )
                respond(
                    request_id,
                    success=True,
                    generationMilliseconds=elapsed,
                    diagnostics=worker.last_diagnostics,
                )
            else:
                raise ValueError(f"Unknown command: {command}")
        except Exception as exc:
            error_code = "runtime_missing" if isinstance(exc, (ImportError, ModuleNotFoundError)) or "runtime is unavailable" in str(exc) else "worker_error"
            error_traceback = traceback.format_exc(limit=8)
            print(error_traceback, file=sys.stderr, flush=True)
            respond(
                request_id,
                success=False,
                errorCode=error_code,
                errorStage=command or "unknown",
                errorType=type(exc).__name__,
                error=str(exc),
                diagnostics=worker.last_diagnostics,
                traceback=error_traceback,
            )


if __name__ == "__main__":
    faulthandler.enable(file=sys.stderr, all_threads=True)
    print(f"kokoro_worker_started; pythonPid={os.getpid()}", file=sys.stderr, flush=True)
    os.environ.setdefault("HF_HUB_OFFLINE", "1")
    os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
    main()
