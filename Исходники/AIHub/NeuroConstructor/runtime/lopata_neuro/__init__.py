"""Public interface for standalone offline execution."""
from contextlib import contextmanager
from pathlib import Path
from tempfile import TemporaryDirectory

from .archive import inspect, pack, unpack


def from_directory(root,device="auto"):
    import json
    root = Path(root)
    manifest = json.loads((root/"manifest.json").read_text("utf-8"))
    if manifest["kind"] in ("medium","heavy"):
        from .medium.model import Medium
        return Medium(root/"models",manifest["settings"],device,root/"weights",manifest["kind"])
    from .model import Model
    return Model(root,device)


@contextmanager
def open_model(path, device="auto"):
    """Yield a loaded Model for multiple requests and release its temporary model files on exit."""
    with TemporaryDirectory(prefix="lopata-neuro-") as directory:
        root = Path(directory) / "model"
        unpack(path, root)
        model = from_directory(root,device)
        try:
            yield model
        finally:
            # Some torch mmap-backed tensors can outlive the yielded Python object on Windows.
            if model.kind in ("medium","heavy"):
                model.close()
            else:
                model.language = model.vision = model.cut = model.speech = None

