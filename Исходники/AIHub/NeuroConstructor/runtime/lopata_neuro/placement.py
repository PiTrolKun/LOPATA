"""Select a supported execution device for each specialist and count graph transfers."""
import os

import torch

from .layout import roles


def available(name):
    backend = torch.backends.mps if name == "mps" else getattr(torch, name, None)
    return backend is not None and backend.is_available()


def accelerator(preferred):
    if preferred not in ("cpu", "auto", "gpu"):
        return preferred
    for name in ("cuda", "xpu", "mps"):
        if available(name):
            backend = getattr(torch, name)
            if hasattr(backend, "mem_get_info") and backend.device_count() > 1:
                index = max(range(backend.device_count()), key=lambda i: backend.mem_get_info(i)[0])
                return f"{name}:{index}"
            return name
    return "cpu"


def concrete(value):
    device = torch.device(value)
    if device.type == "cpu" and device.index is None:
        return device
    if device.type not in ("cuda", "xpu", "mps") or not available(device.type):
        raise ValueError(f"Устройство {value} недоступно для исполнения модели.")
    if device.type == "mps":
        if device.index not in (None, 0):
            raise ValueError(f"Видеокарта {value} недоступна.")
        return torch.device("mps:0")
    backend = getattr(torch, device.type)
    index = backend.current_device() if device.index is None else device.index
    if index >= backend.device_count():
        raise ValueError(f"Видеокарта {value} недоступна.")
    return torch.device(device.type, index)


def devices(configuration, kind, override="auto"):
    preferred = os.environ.get("LOPATA_NEURO_DEVICE", "auto")
    if preferred == "auto":
        preferred = configuration["device"]
    if override != "auto":
        preferred = override
    plan = {}
    for role in roles(kind):
        key = role + "Device"
        value = configuration.get(key, configuration["device"] if role == "vision" else "cpu")
        if override == "cpu":
            value = "cpu"
        elif override != "auto" and (role == "vision" and key not in configuration
                                     or value not in ("cpu", "auto", "gpu")):
            value = override
        if value == "auto":
            value = accelerator(preferred) if role == "vision" and preferred != "cpu" else "cpu"
        elif value == "gpu":
            value = accelerator(preferred)
            if value == "cpu":
                raise ValueError("Для выбранной модели нужна совместимая видеокарта.")
        plan[role] = concrete(value)
    return plan


class Traffic:
    def __init__(self):
        self.counts = {direction: dict(bytes=0, copies=0)
                       for direction in ("toCpu", "toDevice", "betweenDevices", "feedbackToCpu")}
        self.link_bytes = 0

    def move(self, tensor, target, *, link=True, feedback=False):
        if tensor.device != target:
            size = tensor.numel() * tensor.element_size()
            direction = ("toCpu" if target.type == "cpu" else "toDevice"
                         if tensor.device.type == "cpu" else "betweenDevices")
            counter = self.counts[direction]
            counter["bytes"] += size
            counter["copies"] += 1
            if feedback and direction == "toCpu":
                self.counts["feedbackToCpu"]["bytes"] += size
                self.counts["feedbackToCpu"]["copies"] += 1
            if link:
                self.link_bytes += size
        return tensor.to(target)

    def same(self, current, previous):
        if current.device.type == "cpu":
            return torch.equal(current, previous)
        equal = current.eq(previous).all()
        return bool(self.move(equal, torch.device("cpu"), link=False).item())
