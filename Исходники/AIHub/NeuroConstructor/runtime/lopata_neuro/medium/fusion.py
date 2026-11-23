"""Fit the correspondence between a spoken word and four independently observed image cells."""
import torch
from torch import nn
from torch.nn import functional as F


def ordinal(command, anchors):
    scores = F.normalize(command) @ F.normalize(anchors).T
    return scores.reshape(len(command),2,-1).amax(-1).argmax(-1)


def build_bank(samples, anchors):
    commands = torch.cat([s["text"] for s in samples])
    selected = ordinal(commands,anchors)
    voices = torch.stack([s["words"][selected[i]] for i,s in enumerate(samples)])
    bank = dict(anchors=anchors, voices=voices, voiceMean=voices.mean(0),voiceScale=voices.std(0,unbiased=False).clamp_min(.008))
    for i in range(len(samples[0]["blocks"])):
        all_cells = torch.cat([s["blocks"][i] for s in samples])
        bank[f"mean{i}"] = all_cells.mean(0)
        bank[f"scale{i}"] = all_cells.std(0,unbiased=False).clamp_min(.012)
        bank[f"positive{i}"] = torch.stack([s["blocks"][i][s["slot"]] for s in samples])
    return bank


class Link(nn.Module):
    def __init__(self, bank):
        super().__init__()
        for name,tensor in bank.items():
            self.register_buffer(name,tensor)
        self.count = sum(name.startswith("positive") for name in bank)
        self.expert_weights = nn.Parameter(torch.zeros(self.count))
        self.scale = nn.Parameter(torch.tensor([2.5,2.5]))

    def forward(self, blocks, voice, exclude=None):
        def centered(x,mean,scale):
            return F.normalize((x-mean)/scale,dim=-1)
        if voice is None:
            hearing = self.voices.new_zeros((len(blocks[0]),len(self.voices)))
        else:
            hearing = centered(voice,self.voiceMean,self.voiceScale) @ centered(self.voices,self.voiceMean,self.voiceScale).T
        if exclude is not None:
            hearing = hearing.masked_fill(exclude,-10000)
        strength = self.scale.clamp(0,5).exp()
        log_prior = (hearing*strength[0]).log_softmax(-1)
        mix = self.expert_weights.softmax(0)
        score = 0
        for i,feature in enumerate(blocks):
            mean,scale = getattr(self,f"mean{i}"),getattr(self,f"scale{i}")
            score = score + mix[i]*(centered(feature,mean,scale) @ centered(getattr(self,f"positive{i}"),mean,scale).T)
        return torch.logsumexp(score*strength[1]+log_prior[:,None],dim=-1)

    def scores(self, rows, excluded=None):
        command = torch.cat([r["text"] for r in rows])
        selected = ordinal(command,self.anchors)
        voice = torch.stack([r["words"][selected[i]] for i,r in enumerate(rows)])
        blocks = [torch.stack([r["blocks"][i] for r in rows]) for i in range(self.count)]
        return self(blocks,voice,excluded)
