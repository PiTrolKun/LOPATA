"""Pinned component inventory and the graph contract for format version 1."""
import hashlib
import json
import re
from pathlib import Path

REFERENCE = Path(__file__).parent / "reference"
CATALOG = json.loads((REFERENCE / "catalog.json").read_text("utf-8"))
DEFAULTS = dict(routing="learned", channel="continuous", send="always", device="auto")
OPTIONS = dict(routing={"learned", "input", "always"}, channel={"continuous", "byte", "feedback"},
               send={"always", "changed"}, device={"auto", "cpu", "cuda"})
ROLE_DEVICES = ("languageDevice", "visionDevice", "speechDevice")


def settings(values=None):
    result = DEFAULTS.copy()
    for key, value in (values or {}).items():
        if (key in (*ROLE_DEVICES, "device") and isinstance(value, str)
                and re.fullmatch(r"auto|cpu|gpu|(cuda|xpu)(:[0-9]{1,3})?|mps(:0)?", value)):
            result[key] = value
            continue
        if key not in OPTIONS or value not in OPTIONS[key]:
            raise ValueError(f"Неизвестная настройка {key} = {value}")
        result[key] = value
    return result


def digest(path):
    with Path(path).open("rb") as file:
        return hashlib.file_digest(file, "sha256").hexdigest()


def roles(kind):
    if kind not in ("pair", "trio"):
        raise ValueError("Неизвестный состав модели")
    return ("language", "vision", "speech") if kind == "trio" else ("language", "vision")


def weights(kind, channel=None):
    if channel == "feedback":
        if kind != "pair":
            raise ValueError("Обратный запрос доступен для пары языка и зрения")
        return ("pair", "byte", "router", "feedback")
    return ("pair", "byte", "router", "trio") if kind == "trio" else ("pair", "byte", "router")


def components(kind):
    ids = {CATALOG["ids"][role] for role in roles(kind)}
    return {f'models/{i["repo"]}/{i["file"]}': i for i in CATALOG["files"] if i["repo"] in ids}


def checks(kind):
    # Every file path is defined in the installed library, never by a model author.
    suites = ("pair", "trio") if kind == "trio" else ("pair",)
    names = set()
    for suite in suites:
        path = REFERENCE / "checks" / f"{suite}.json"
        names.add(path.name)
        for row in json.loads(path.read_text("utf-8")):
            names.add(row["image"])
            names.update(row["audio"])
    return sorted(names)


def source_root(path):
    if path is None:
        return None
    path = Path(path)
    return path / "models" if (path / "models").is_dir() else path


def checked_component(path, entry, audited=False):
    # The host has already applied its installation audit policy. Independently
    # pin the executable TorchScript detector whenever it is about to be loaded.
    must_hash = not audited or entry["file"] == "icon_detect_v3/model.pt"
    if not path.is_file() or path.stat().st_size != entry["size"] or must_hash and digest(path) != entry["sha256"]:
        raise ValueError(f'Требуется {entry["repo"]}/{entry["file"]} редакции '
                         f'{next(v["revision"] for k,v in CATALOG["models"].items() if CATALOG["ids"][k] == entry["repo"])}')


def graph(topology, pair_connection="inside", configuration=None, kind=None):
    if topology not in ("early", "late"):
        raise ValueError("Неизвестный способ соединения речи")
    if pair_connection not in ("inside", "late"):
        raise ValueError("Неизвестное место связи языка и зрения")
    return dict(id="four-cell-icons-v1", topology=topology, pair_connection=pair_connection,
                image="256x256 RGB, four equal cells in row-major order",
                text="UTF-8, MiniLM mean pooling, at most 128 tokens",
                audio="two mono PCM16 WAV clips, up to 30s each, resample to 16 kHz",
                output="zero-based index of cell (top-left, top-right, bottom-left, bottom-right)",
                vision_cut="41 -> 49.heads.0.class_conv",
                language_link="384 -> 16 float32, optional uint8 learned codebook",
                placement=({role: configuration.get(role + "Device", configuration["device"] if role == "vision" else "cpu")
                            for role in roles(kind)}
                           if configuration and any(k in configuration for k in ROLE_DEVICES)
                           else dict(language="cpu", speech="cpu", vision="selected device")))
