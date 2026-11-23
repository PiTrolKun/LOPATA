"""Local inference on the supported text, image and speech specialists."""
import json
import os
import threading
import time
import wave
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
import torch
from PIL import Image
from torch import nn
from torch.nn import functional as F

from . import layout
from .networks import Codebook, Cut, Fusion, Trio
from .placement import Traffic, devices

POSITIONS = ("сверху слева", "сверху справа", "снизу слева", "снизу справа")


def pixels(image):
    return torch.from_numpy(np.array(image)).permute(2, 0, 1).contiguous().unsqueeze(0).float().div_(255)


def prepare_image(image):
    rgb = image.convert("RGB")
    return rgb if rgb.size == (256, 256) else rgb.resize((256, 256), Image.Resampling.BILINEAR)


def waveform(path):
    with wave.open(str(path), "rb") as sound:
        rate, count = sound.getframerate(), sound.getnframes()
        if (sound.getnchannels(), sound.getsampwidth(), sound.getcomptype()) != (1, 2, "NONE"):
            raise ValueError("Требуется монофонический WAV PCM 16 бит")
        if not 0 < rate <= 384000 or not 0 < count <= rate * 30:
            raise ValueError("Требуется непустая запись длиной до 30 секунд")
        raw = sound.readframes(count)
    if len(raw) != count * 2:
        raise ValueError("Неполный WAV")
    data = np.frombuffer(raw, dtype="<i2").astype(np.float32) / 32768
    length = max(1, round(len(data) * 16000 / rate))
    return np.interp(np.arange(length) * rate / 16000, np.arange(len(data)), data).astype(np.float32)


class Language:
    def __init__(self, path, device="cpu"):
        from transformers import AutoModel, AutoTokenizer
        self.tokenizer = AutoTokenizer.from_pretrained(path, local_files_only=True, trust_remote_code=False)
        self.model = AutoModel.from_pretrained(path, local_files_only=True, trust_remote_code=False,
                                               use_safetensors=True).eval().requires_grad_(False).to(device)

    @torch.no_grad()
    def encode(self, texts):
        batch = self.tokenizer(texts, padding=True, truncation=True, max_length=128, return_tensors="pt").to(self.model.device)
        mask = batch.attention_mask.unsqueeze(-1)
        return (self.model(**batch).last_hidden_state * mask).sum(1) / mask.sum(1).clamp(min=1)


class Speech:
    def __init__(self, path, device="cpu"):
        from transformers import WhisperProcessor, WhisperForConditionalGeneration
        self.proc = WhisperProcessor.from_pretrained(path, local_files_only=True)
        self.net = WhisperForConditionalGeneration.from_pretrained(path, local_files_only=True,
                                                                   use_safetensors=True).eval().requires_grad_(False).to(device)

    def _features(self, signal):
        value = self.proc.feature_extractor(signal, sampling_rate=16000, return_tensors="pt").input_features
        return value.to(self.net.device)

    def _transcribe(self, features):
        tokens = self.net.generate(input_features=features, task="transcribe", max_new_tokens=22,
                                   num_beams=1, do_sample=False)
        return self.proc.batch_decode(tokens, skip_special_tokens=True)[0].strip()

    @torch.no_grad()
    def transcribe_signal(self, signal):
        return self._transcribe(self._features(signal))

    @torch.no_grad()
    def encode_signal(self, signal, transcribe=False):
        features = self._features(signal)
        states = self.net.model.encoder(features).last_hidden_state
        end = max(3, min(states.shape[1], (len(signal) + 319) // 320))
        pooled = torch.cat([chunk.mean(1) for chunk in states[:, :end].tensor_split(3, dim=1)], 1)[0]
        if transcribe:
            return pooled, self._transcribe(features)
        return pooled


class Model:
    def __init__(self, root, device="auto"):
        root = Path(root)
        info = json.loads((root / "manifest.json").read_text("utf-8"))
        self._setup(None, info["kind"], device, root, info)

    @classmethod
    def from_source(cls, source, kind, device="auto", parent_root=None, role_devices=None):
        root = Path(parent_root) if parent_root else None
        info = json.loads((root / "manifest.json").read_text("utf-8")) if root else {}
        model = cls.__new__(cls)
        configuration = dict(info.get("settings", {}))
        if device != "auto":
            configuration["device"] = device
        configuration.update(role_devices or {})
        model._setup(layout.source_root(source), kind, "auto", root, dict(info, settings=configuration))
        return model

    def _setup(self, source, kind, device, parent, info):
        self.kind = kind
        self.settings = layout.settings(info.get("settings"))
        self.topology = info.get("graph", {}).get("topology", layout.CATALOG["topology"])
        self.devices = devices(self.settings, kind, device)
        self.device = self.devices["vision"]
        # Keep the numerical settings used for the pinned TorchScript graph.
        torch.set_num_threads(min(4, os.cpu_count() or 1))
        torch.backends.cudnn.allow_tf32 = False
        torch.backends.cuda.matmul.allow_tf32 = False
        torch._C._set_graph_executor_optimize(False)
        self.repositories = {}
        self.weights = {}
        for role in layout.roles(kind):
            repo = layout.CATALOG["ids"][role]
            supplied = parent and role in layout.roles(info["kind"])
            model_root = parent / "models" if supplied else source
            if not model_root:
                raise ValueError(f"Укажите папку с установленной моделью {repo}")
            self.repositories[role] = model_root / repo
            audited = not supplied and os.environ.get("LOPATA_NEURO_SOURCE_AUDITED") == "1"
            for item in layout.CATALOG["files"]:
                if item["repo"] == repo:
                    layout.checked_component(self.repositories[role] / item["file"], item, audited)
        for role in layout.weights(kind, "feedback" if kind == "pair" else None):
            supplied = parent and role in layout.weights(info["kind"], info.get("settings", {}).get("channel"))
            directory = parent if supplied else layout.REFERENCE
            self.weights[role] = directory / "weights" / f"{role}.pt"
        self.language = Language(self.repositories["language"], self.devices["language"])
        self.vision = torch.jit.load(str(self.repositories["vision"] / "icon_detect_v3/model.pt"),
                                     map_location=self.device).eval()
        for parameter in self.vision.parameters():
            parameter.requires_grad_(False)
        self.cut = Cut(self.vision)
        pair = self._weight("pair")
        if info.get("graph", {}).get("pair_connection", pair["mode"]) != pair["mode"]:
            raise ValueError("Описание точки обмена не соответствует весам пары")
        self.pair = Fusion(pair["channels"], pair["mode"], self.device, self.devices["language"]).eval()
        self.pair.load_state_dict(pair["weights"])
        self.router = nn.Linear(384, 1).to(self.devices["language"]).eval()
        self.router.load_state_dict(self._weight("router")["state"])
        self.byte = Codebook(self.device, self.devices["language"]).eval()
        self.byte.load_state_dict(self._weight("byte")["weights"])
        self.speech = self.trio = None
        self.feedback = None
        self._last_key = self._last_signal = self._last_index = None
        self._speech_key = self._speech_signal = None
        self._lock = threading.RLock()

    @property
    def placement(self):
        return {role: str(device) for role, device in self.devices.items()}

    def _weight(self, name):
        from .archive import load_weights
        return load_weights(self.weights[name])

    def prepare_audio(self):
        if self.kind != "trio":
            raise ValueError("В этой сборке нет модели слуха")
        if self.speech is None:
            data = self._weight("trio")
            speech = Speech(self.repositories["speech"], self.devices["speech"])
            trio = Trio(self.pair, data["mu"], data["sd"], self.topology, self.devices["speech"]).eval()
            trio.audio.load_state_dict(data["audio"])
            trio.ordinal.load_state_dict(data["ordinal"])
            self.speech, self.trio = speech, trio

    @torch.no_grad()
    def image_features(self, image):
        base = self.cut.features(pixels(prepare_image(image)).to(self.device))
        return self.cut.frozen_next(base) if self.pair.mode == "late" else base

    def reset_series(self):
        with self._lock:
            self._last_key = self._last_signal = self._last_index = None
            self._speech_key = self._speech_signal = None

    def _ask_for_details(self, scores):
        if self.feedback is None:
            data = self._weight("feedback")
            gate = nn.Sequential(nn.Linear(13, 16), nn.Tanh(), nn.Linear(16, 1)).to(self.device).eval()
            gate.load_state_dict(data["state"])
            self.feedback, self.threshold = gate, float(data["threshold"])
        confidence = scores.softmax(1).sort(1, descending=True).values
        encoded = F.one_hot(self._last_index.long(), 8).to(scores.dtype)
        features = torch.cat((confidence, confidence[:, :1] - confidence[:, 1:2], encoded), 1)
        return (self.feedback(features).flatten() >= self.threshold).to(torch.uint8)

    @torch.no_grad()
    def predict(self, image, text, audio=()):
        if not isinstance(text, str) or not text.strip():
            raise ValueError("Введите текст команды")
        if not isinstance(image, Image.Image):
            with Image.open(image) as opened:
                return self.predict(opened, text, audio)
        if audio and (self.kind == "pair" or len(audio) != 2):
            raise ValueError("Для двух отдельных WAV нужна сборка со слухом")
        start = time.perf_counter()
        traffic = Traffic()
        cpu = torch.device("cpu")
        requested = False

        def transfer(signal):
            return traffic.move(signal, self.device)

        def speech_transfer(signal):
            if self.settings["send"] == "always":
                return transfer(signal)
            if self._speech_key is None or not traffic.same(signal, self._speech_key):
                self._speech_signal = transfer(signal)
                self._speech_key = signal
            return self._speech_signal

        with self._lock, ThreadPoolExecutor(max_workers=1) as pool:
            vision = pool.submit(self.image_features, image)
            command = self.language.encode([text])
            route = self.kind == "trio" and bool(audio)
            if self.kind == "trio" and self.settings["routing"] != "input":
                decision = self.router(F.normalize(command, dim=-1) * 8) > 0
                route = bool(traffic.move(decision, cpu, link=False).item())
            if route and len(audio) != 2:
                raise ValueError("Для этой команды нужны две отдельные записи WAV")
            run_speech = route or (self.settings["routing"] == "always" and bool(audio))
            if run_speech:
                self.prepare_audio()
                signals = torch.stack([self.speech.encode_signal(waveform(p)) for p in audio]).unsqueeze(0)
                codes, choice = self.trio.codes(signals, command)
                speech_scores = self.trio.choose(vision.result(), codes, choice, self.cut, send=speech_transfer,
                                                 move_selector=traffic.move)
            if route:
                scores = speech_scores
            else:
                channel = self.settings["channel"]
                short = channel in ("byte", "feedback")
                signal = self.byte.ids(command) if short else self.pair.message(command)
                if (self.settings["send"] != "changed" or self._last_key is None or self._last_key[0] != short
                        or not traffic.same(signal, self._last_key[1])):
                    moved = transfer(signal)
                    self._last_index = moved if short else None
                    self._last_signal = self.byte.values[moved.long()] if short else moved
                    self._last_key = (short, signal)
                scores = self.pair.decide(vision.result(), self._last_signal, self.cut)
                if channel == "feedback":
                    if self.kind != "pair":
                        raise ValueError("Обратный запрос доступен для пары языка и зрения")
                    status = self._ask_for_details(scores)
                    requested = bool(traffic.move(status, cpu, link=False, feedback=True).item())
                    if requested:
                        scores = self.pair.decide(vision.result(), transfer(self.pair.message(command)), self.cut)
            outcome = scores.argmax(1).to(torch.int32)
            slot = int(traffic.move(outcome, cpu, link=False).item())
        return dict(slot=slot, position=POSITIONS[slot], route="trio" if route else "pair", device=str(self.device),
                    elapsed_ms=1000 * (time.perf_counter()-start),
                    link_bytes=traffic.link_bytes, feedback_requested=requested,
                    placement=self.placement, transfers=traffic.counts,
                    calls=dict(vision=1, language=1, audio=2 if run_speech else 0))
