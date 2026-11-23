"""Separated input sets, native expert outputs and a frozen independent comparison."""
from concurrent.futures import ThreadPoolExecutor
from difflib import SequenceMatcher
import hashlib
import json
from pathlib import Path
import threading
import time

import torch
from PIL import Image
from torch.nn import functional as F

from training_state import digest, read_json, write, write_json


def image_input(path):
    from lopata_neuro.model import prepare_image
    with Image.open(path) as im:
        return prepare_image(im)


def dataset(run, project, kind):
    from lopata_neuro.model import waveform
    sample_root = Path(__file__).parent / "training_examples"
    sources = []
    if run.get("includeReference", True):
        sources.extend((sample_root, r) for r in read_json(sample_root / (kind + ".json")))
    sources.extend((project, r) for r in run.get("examples", []))
    result = []
    seen, signatures, contents = {}, [], {}
    for directory, item in sources:
        text = item.get("text", "").strip()
        slot, split = item["slot"], item.get("split", "train")
        if not text or type(slot) != int or slot not in range(4) or split not in ("train", "dev", "test"):
            raise ValueError("Примеру нужны команда, одна из четырёх ячеек и назначение для обучения или проверки.")
        image = (directory / item["image"]).resolve()
        audio = [(directory / name).resolve() for name in item.get("audio") or []]
        if kind == "pair" and audio:
            raise ValueError("В шаблоне без слуха используйте текст и изображение.")
        if kind == "trio" and len(audio) != 2:
            raise ValueError("Для обучения слуха добавьте к каждому примеру две записи WAV.")
        sample = []
        for path in [image, *audio]:
            if path not in contents:
                data = image_input(path).tobytes() if path == image else waveform(path).tobytes()
                contents[path] = hashlib.sha256(data).hexdigest()
            key = contents[path]
            if seen.setdefault(key, split) != split:
                raise ValueError(f"Один и тот же материал попал в разные наборы: {path.name}. Выберите другой пример.")
            sample.append(key)
        result.append(dict(text=text, image=str(image), audio=list(map(str, audio)), slot=slot, split=split,
                           imageId=sample[0], audioIds=sample[1:], own=directory == project))
        signatures.append([text, slot, split, sample])
    for split in ("train", "dev", "test"):
        if not any(r["split"] == split for r in result):
            raise ValueError(f"Нужны примеры во всех трёх наборах. Пуст набор {split}.")
    return result, signatures


def signature(run, sample_ids, model, legacy_device):
    from lopata_neuro.layout import ROLE_DEVICES
    weights = {str(key): digest(Path(path)) for key,path in model.weights.items() if Path(path).is_file()}
    payload = dict(format=2, settings=run, inputs=sample_ids, weights=weights, torch=str(torch.__version__),
                   device=legacy_device)
    if any(key in model.settings for key in ROLE_DEVICES):
        payload["placement"] = model.placement
    return hashlib.sha256(json.dumps(payload, ensure_ascii=False, sort_keys=True).encode()).hexdigest()


def native_descriptor(outputs):
    # A slot gets ordinary detection scores and box distances from its image quadrant.
    heads = []
    for idx, head in enumerate(outputs):
        if idx % 2 == 0:
            head = head.sigmoid()
        h, w = head.shape[-2:]
        parts = [head[..., i*h//2:(i+1)*h//2, j*w//2:(j+1)*w//2] for i in range(2) for j in range(2)]
        pooled = F.adaptive_avg_pool2d(torch.cat(parts), (6,6))
        heads.append(pooled.flatten(1))
    return torch.cat(heads, 1).cpu()


class NativeControl:
    """Match labelled training cells using independent native image, text and ASR outputs."""
    def __init__(self, refs):
        self.refs = refs
        self.language = F.normalize(torch.cat([x["text"] for x in refs]))
        images = torch.cat([x["native"] for x in refs])
        self.mean = images.mean(0)
        self.std = images.std(0, unbiased=False).clamp_min(.003)
        positive = torch.stack([x["native"][x["slot"]] for x in refs])
        self.positive = F.normalize((positive - self.mean) / self.std)

    def answer(self, entry):
        visual = F.normalize((entry["native"] - self.mean) / self.std)
        appearance = visual @ self.positive.T
        prompt = self.language @ F.normalize(entry["text"])[0]
        if entry["transcripts"]:
            a, b = entry["transcripts"]
            speech = [sum(SequenceMatcher(None, x.casefold(), y.casefold()).ratio()
                          for x,y in zip((a,b), ref["transcripts"])) * .5 for ref in self.refs]
            prompt = (prompt + torch.tensor(speech)) / 2
        # Top three matches for each slot suppress isolated accidental neighbours.
        by_slot = appearance + prompt.unsqueeze(0)
        return int(by_slot.topk(min(3, by_slot.shape[1]), dim=1).values.mean(1).argmax())


class Samples:
    def __init__(self, model, rows, job):
        self.model = model
        self.rows = rows
        self.job = job
        self.texts, self.images, self.audio = {}, {}, {}
        self.pool = ThreadPoolExecutor(max_workers=3)
        self.first_forwards = None

    @torch.no_grad()
    def _vision(self, row, native_only):
        from lopata_neuro.model import pixels
        image = pixels(image_input(row["image"])).to(self.model.device)
        descriptor = native_descriptor(self.model.vision(image))
        base = None if native_only else self.model.cut.features(image).detach().cpu()
        return descriptor, base

    @torch.no_grad()
    def _audio(self, paths, measure):
        from lopata_neuro.model import waveform
        result = []
        for path in paths:
            if measure:
                value = None, self.model.speech.transcribe_signal(waveform(Path(path)))
            elif path not in self.audio:
                features, transcript = self.model.speech.encode_signal(waveform(Path(path)), transcribe=True)
                value = features.detach().cpu(), transcript
                self.audio[path] = value
            else:
                value = self.audio[path]
            result.append(value)
        return result

    def extract(self, row, measure=False):
        text, image = row["text"], row["image"]
        tasks = {}
        if measure or text not in self.texts:
            tasks["text"] = lambda: self.model.language.encode([text]).detach().cpu()
        if measure or image not in self.images:
            tasks["image"] = lambda: self._vision(row, measure)
        if row["audio"]:
            tasks["audio"] = lambda: self._audio(row["audio"], measure)
        if tasks:
            gate = threading.Barrier(len(tasks))
            origin = time.perf_counter()
            def collect(action):
                gate.wait()
                start = time.perf_counter()-origin
                result = action()
                return result, {"startSeconds":start,"finishedSeconds":time.perf_counter()-origin}
            futures = {key:self.pool.submit(collect, action) for key,action in tasks.items()}
            finished = {key:f.result() for key,f in futures.items()}
            values = {key:value for key,(value,_) in finished.items()}
            if measure and self.first_forwards is None:
                self.first_forwards = {key:when for key,(_,when) in finished.items()}
        else:
            values = {}
        if not measure:
            if "text" in values:
                self.texts[text] = values["text"]
            if "image" in values:
                self.images[image] = values["image"]
        desc, base = values["image"] if "image" in values else self.images[image]
        clips = values.get("audio", [])
        return dict(text=values["text"] if "text" in values else self.texts[text], native=desc, base=base,
                    words=torch.stack([pair[0] for pair in clips]) if clips and not measure else None,
                    transcripts=[pair[1] for pair in clips], slot=row["slot"])

    def prepare(self, cache):
        sets = {"train":[], "dev":[], "test":[]}
        started = time.monotonic()
        measured = 0
        for i, row in enumerate(self.rows):
            self.job.check()
            saved = cache / f"sample-{i}.pt"
            if saved.is_file():
                value = torch.load(saved, weights_only=True, map_location="cpu")
            else:
                value = self.extract(row)
                write(saved, lambda stream: torch.save(value, stream))
                measured += 1
            sets[row["split"]].append(value)
            self.job.progress("Подготовка примеров", i+1, len(self.rows), started, measured)
        return sets

    def baseline(self, training, cache):
        rule = NativeControl(training)
        path = cache / "native-comparison.json"
        timing = cache / "native-start.json"
        if timing.is_file():
            self.first_forwards = read_json(timing)
        outputs = read_json(path) if path.is_file() else []
        initial = len(outputs)
        phase_start = time.monotonic()
        heldout = [r for r in self.rows if r["split"]=="test"]
        for i, row in enumerate(heldout):
            if i < len(outputs):
                continue
            self.job.check()
            started = time.perf_counter()
            answer = rule.answer(self.extract(row, measure=True))
            outputs.append((answer, time.perf_counter() - started))
            if not timing.is_file():
                write_json(timing, self.first_forwards)
            write_json(path, outputs)
            self.job.progress("Проверка исходных моделей",len(outputs),len(heldout),phase_start,len(outputs)-initial)
        return outputs
