"""The trainable links of the four-cell MiniLM / OmniParser / Whisper graph."""
import torch
from torch import nn
from torch.nn import functional as F


class Cut:
    def __init__(self, detector):
        graph = detector.model.forward.graph.copy()
        nodes = list(graph.nodes())
        matches = []
        for node in nodes:
            if node.kind() == "prim::CallMethod":
                source = next(node.inputs()).node()
                if source.kind() == "prim::GetAttr" and source.s("name") == "41":
                    matches.append(node)
        if len(matches) != 1:
            raise ValueError("OmniParser: ожидается одна точка обмена после узла 41")
        cut = matches[0]
        graph.eraseOutput(0)
        graph.registerOutput(cut.output())
        for node in reversed(nodes[nodes.index(cut) + 1:]):
            node.destroy()
        self.prefix = torch._C._create_function_from_graph("lopata_neuro_fine_neck", graph)
        self.body = detector.model
        conv = getattr(getattr(self.body.model, "49").heads, "0").class_conv
        self.first = getattr(conv, "0")
        self.frozen_next = getattr(conv, "1")
        self.original_logit = getattr(conv, "2")

    @torch.no_grad()
    def features(self, image):
        return self.first(self.prefix(self.body, image))


class Fusion(nn.Module):
    def __init__(self, channels, mode, device="cpu", language_device="cpu"):
        super().__init__()
        if mode not in {"inside", "late"} or channels < 1 or channels > 1024:
            raise ValueError("Неподдерживаемая точка обмена зрения")
        self.mode = mode
        self.encode = nn.Linear(384, 16).to(language_device)
        self.film = nn.Linear(16, 2 * channels).to(device)
        self.rank = nn.Linear(channels * 16, 1).to(device)
        nn.init.zeros_(self.film.weight)
        nn.init.zeros_(self.film.bias)

    def message(self, text):
        return torch.tanh(self.encode(F.normalize(text, dim=-1) * 8))

    def decide(self, base, msg, cut, disable=False):
        scale, bias = self.film(msg).chunk(2, -1)
        if disable:
            scale, bias = scale * 0, bias * 0
        signal = (1 + scale[:, :, None, None]) * base + bias[:, :, None, None]
        linked = cut.frozen_next(signal) if self.mode == "inside" else signal
        grid = F.adaptive_avg_pool2d(linked, (8, 8))
        b, c = grid.shape[:2]
        cells = grid.reshape(b, c, 2, 4, 2, 4).permute(0, 2, 4, 1, 3, 5).reshape(b, 4, c * 16)
        return self.rank(cells).squeeze(-1)

    def forward(self, base, text, cut, disable=False):
        return self.decide(base, self.message(text).to(base.device), cut, disable)


class Trio(nn.Module):
    def __init__(self, pair, mu, sd, mode, speech_device="cpu"):
        super().__init__()
        self.pair = pair.requires_grad_(False)
        self.register_buffer("mu", mu.to(speech_device))
        self.register_buffer("sd", sd.to(speech_device))
        self.audio = nn.Sequential(nn.Linear(1536, 64), nn.LayerNorm(64), nn.GELU(), nn.Linear(64, 16),
                                   nn.Tanh()).to(speech_device)
        self.ordinal = nn.Linear(384, 1).to(pair.encode.weight.device)
        self.mode = mode

    def codes(self, words, ordinal):
        return self.audio(((words - self.mu) / self.sd).clamp(-8, 8)), self.ordinal(F.normalize(ordinal, dim=-1) * 8)

    def choose(self, base, codes, selector, cut, send=None, mode=None, move_selector=None):
        blend = selector.sigmoid()
        move = send or (lambda tensor: tensor.to(base.device))
        if (mode or self.mode) == "early":
            if blend.device != codes.device:
                blend = move_selector(blend, codes.device) if move_selector else blend.to(codes.device)
            return self.pair.decide(base, move(codes[:, 0] * (1 - blend) + codes[:, 1] * blend), cut)
        if blend.device == codes.device:
            message = move(torch.cat((codes[:, 0], codes[:, 1], blend), dim=1))
            blend = message[:, 32:]
        else:
            message = move(codes.flatten(1))
            blend = move_selector(blend, base.device) if move_selector else blend.to(base.device)
        first = self.pair.decide(base, message[:, :16], cut)
        second = self.pair.decide(base, message[:, 16:32], cut)
        return first * (1 - blend) + second * blend


class Codebook(nn.Module):
    def __init__(self, device="cpu", language_device="cpu"):
        super().__init__()
        self.sender = nn.Linear(384, 8).to(language_device)
        self.values = nn.Parameter(torch.zeros(8, 16, device=device))

    def ids(self, text):
        return self.sender(F.normalize(text, dim=-1) * 8).argmax(1).to(torch.uint8)
