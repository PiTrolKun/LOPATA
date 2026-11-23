"""Fit only the selected links. Optimizer state and the best dev candidate are checkpointable."""
import math
import time

import torch
from torch.nn import functional as F

from lopata_neuro.networks import Codebook, Fusion, Trio


def copied(module):
    return {name:value.detach().cpu().clone() for name,value in module.state_dict().items()}


class Links:
    def __init__(self, model, kind, mode, channel, train=True):
        self.engine = model
        self.kind, self.mode, self.channel = kind, mode, channel
        self.codebook = None
        if kind == "trio":
            old = model.trio
            self.net = Trio(model.pair, old.mu.clone(), old.sd.clone(), mode, model.devices["speech"])
            self.net.ordinal.load_state_dict(old.ordinal.state_dict())
            self.net.audio.load_state_dict(old.audio.state_dict())
        else:
            old = model.pair
            self.net = Fusion(old.film.out_features // 2, mode, model.device, model.devices["language"])
            self.net.load_state_dict(old.state_dict())
            if channel in ("byte", "cached"):
                self.codebook = Codebook(model.device, model.devices["language"])
                data = torch.load(model.weights["byte"], weights_only=True, map_location="cpu")
                self.codebook.load_state_dict(data["weights"])
        self.parameters = [p for p in self.net.parameters() if p.requires_grad]
        if self.codebook is not None:
            self.parameters += list(self.codebook.parameters())
        self.optimizer = torch.optim.AdamW(self.parameters, lr=0.0005, weight_decay=0.001) if train else None

    def states(self):
        if self.kind == "trio":
            return dict(audio=copied(self.net.audio), ordinal=copied(self.net.ordinal))
        bundle = {"weights":copied(self.net)}
        if self.codebook is not None:
            bundle["byte"] = copied(self.codebook)
        return bundle

    def load(self, states):
        if self.kind == "trio":
            self.net.audio.load_state_dict(states["audio"])
            self.net.ordinal.load_state_dict(states["ordinal"])
        else:
            self.net.load_state_dict(states["weights"])
            if self.codebook is not None:
                self.codebook.load_state_dict(states["byte"])

    def logits(self, rows, variant="normal", training=False):
        model = self.engine
        base = torch.cat([r["base"] for r in rows]).to(model.device)
        inputs = rows[1:] + rows[:1] if variant == "shuffled" else rows
        text = torch.cat([r["text"] for r in inputs]).to(model.devices["language"])
        pair_mode = self.mode if self.kind == "pair" else model.pair.mode
        if pair_mode == "late":
            with torch.no_grad():
                base = model.cut.frozen_next(base)
        if self.kind == "trio":
            words = torch.stack([r["words"] for r in inputs]).to(model.devices["speech"])
            codes, selector = self.net.codes(words, text)
            if variant == "disabled":
                codes, selector = torch.zeros_like(codes), torch.zeros_like(selector)
            return self.net.choose(base, codes, selector, model.cut)
        if self.codebook is None:
            return self.net(base, text, model.cut, disable=variant == "disabled")
        probs = self.codebook.sender(F.normalize(text) * 8).softmax(1)
        if training:
            hard = F.one_hot(probs.argmax(1), probs.shape[1]).to(probs.dtype)
            weights = probs + (hard - probs).detach()
            message = weights.to(model.device) @ self.codebook.values
        else:
            message = self.codebook.values[probs.argmax(1).to(torch.uint8).to(model.device).long()]
        return self.net.decide(base, message, model.cut, disable=variant == "disabled")

    def step(self, rows):
        target = torch.tensor([r["slot"] for r in rows], device=self.engine.device)
        loss = F.cross_entropy(self.logits(rows, training=True), target)
        self.optimizer.zero_grad()
        loss.backward()
        torch.nn.utils.clip_grad_norm_(self.parameters, 5)
        self.optimizer.step()

    @torch.no_grad()
    def score(self, rows, variant="normal"):
        logits = torch.cat([self.logits(rows[i:i+8], variant).cpu() for i in range(0,len(rows),8)])
        target = torch.tensor([r["slot"] for r in rows])
        return int((logits.argmax(1) == target).sum()), float(F.cross_entropy(logits, target))

    def product(self):
        if self.kind == "trio":
            return {"trio.pt":dict(**self.states(), mu=self.net.mu.cpu(), sd=self.net.sd.cpu())}
        output = {"pair.pt":dict(weights=copied(self.net), channels=self.net.film.out_features//2, mode=self.mode)}
        if self.codebook is not None:
            output["byte.pt"] = {"weights":copied(self.codebook)}
        return output


def fit(model, sets, run, job, checkpoint, signature):
    kind = run.get("composition", "pair")
    valid = ("inside", "late") if kind == "pair" else ("early", "late")
    requested = run.get("connection") or valid[0]
    if kind == "trio" and requested == "inside":
        requested = "early"
    modes = valid if requested == "auto" else (requested,)
    if any(mode not in valid for mode in modes):
        raise ValueError("Для выбранного состава эта связь не поддерживается.")
    cycles, attempts = int(run.get("cycles",1)), int(run.get("attempts",1))
    if min(cycles,attempts) < 1:
        raise ValueError("Для обучения нужен хотя бы один проход и одна попытка.")
    steps = math.ceil(len(sets["train"]) / 8)
    job.total = steps * cycles * attempts * len(modes)
    best = checkpoint.get("best") if checkpoint else None
    history = list(checkpoint.get("history", [])) if checkpoint else []
    start = time.monotonic()
    initial = job.step
    previous_wall = job.training_wall
    for k, mode in enumerate(modes):
        for attempt in range(attempts):
            first = (k * attempts + attempt) * cycles * steps
            if job.step >= first + cycles * steps:
                continue
            seed = 2718 + attempt
            torch.manual_seed(seed)
            links = Links(model, kind, mode, run.get("channel","continuous"))
            if checkpoint and job.step > first:
                links.load(checkpoint["weights"])
                links.optimizer.load_state_dict(checkpoint["optimizer"])
            for cycle in range(cycles):
                ids = torch.randperm(len(sets["train"]), generator=torch.Generator().manual_seed(seed+cycle)).tolist()
                for b in range(steps):
                    number = first + cycle * steps + b + 1
                    if number <= job.step:
                        continue
                    job.stage_begin(f"Обучение · попытка {attempt+1} из {attempts} · проход {cycle+1} из {cycles}")
                    batch_start = time.perf_counter()
                    links.step([sets["train"][i] for i in ids[b*8:(b+1)*8]])
                    if b == steps - 1:
                        score, loss = links.score(sets["dev"])
                        history.append(dict(mode=mode, attempt=attempt+1,cycle=cycle+1,correct=score,loss=loss))
                        if best is None or (score,-loss) > (best["score"],-best["loss"]):
                            best = dict(mode=mode, weights=links.states(), score=score,loss=loss)
                    for target in set(model.devices.values()):
                        if target.type in ("cuda", "xpu"):
                            getattr(torch, target.type).synchronize(target)
                        elif target.type == "mps":
                            torch.mps.synchronize()
                    job.training_seconds += time.perf_counter()-batch_start
                    job.step = number
                    job.remaining = round((time.monotonic()-start)/(number-initial)*(job.total-number),1)
                    if job.estimated_training is None or number-initial <= 3:
                        job.estimated_training = round((time.monotonic()-start)/(number-initial)*job.total,2)
                    job.training_wall = previous_wall + time.monotonic() - start
                    job.save(signature, dict(weights=links.states(), optimizer=links.optimizer.state_dict(),
                                             best=best, history=history))
                    job.status()
    if best is None:
        raise ValueError("Не удалось выбрать проверенные веса.")
    chosen = Links(model, kind, best["mode"], run.get("channel","continuous"))
    chosen.load(best["weights"])
    job.training_wall = previous_wall + time.monotonic() - start
    return chosen, history
