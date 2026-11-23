"""Non-executable, allow-listed ZIP64 container. All weights travel with the graph."""
import hashlib
import json
import os
import re
import shutil
import struct
import tempfile
from contextlib import ExitStack
from datetime import datetime, timezone
from pathlib import Path
from zipfile import ZIP_STORED, ZipFile

from . import layout

MANIFEST_LIMIT = 128 * 1024
LINK_LIMIT = 8 * 1024 * 1024


def load_weights(path):
    """Accept only the eager data-only torch.save envelope, never dispatched TorchScript code."""
    import torch
    path = Path(path)
    if path.stat().st_size > LINK_LIMIT:
        raise ValueError(f"Недопустимый размер весов связи {path.name}")
    with ZipFile(path) as archive:
        entries = archive.infolist()
        if (len(entries) > 256 or sum(e.file_size for e in entries) > LINK_LIMIT
                or any(e.compress_type != ZIP_STORED or not re.fullmatch(
                    r"[^/\\]+/(?:data/[0-9]+|data\.pkl|version|byteorder|\.format_version|\.storage_alignment|\.data/serialization_id)",
                    e.filename) for e in entries)):
            raise ValueError(f"Неизвестный формат весов связи {path.name}")
    return torch.load(path, map_location="cpu", weights_only=True)


def _names(kind, channel):
    return (set(layout.components(kind)) | {f"weights/{key}.pt" for key in layout.weights(kind, channel)}
            | {f"checks/{name}" for name in layout.checks(kind)} | {"licenses/Apache-2.0.txt"})


def _inspect(archive):
    info = archive.getinfo("manifest.json")
    if info.file_size > MANIFEST_LIMIT or info.compress_type != ZIP_STORED:
        raise ValueError("Неверный размер или кодирование описания сборки")
    manifest = json.loads(archive.read(info))
    if (manifest.get("format"),manifest.get("version")) == ("lopata-neuro",2):
        from .medium.package import inspect_zip
        return inspect_zip(archive,manifest)
    if (manifest.get("format"), manifest.get("version")) != ("lopata-neuro", 1):
        raise ValueError("Требуется формат lopata-neuro версии 1")
    kind = manifest["kind"]
    layout.settings(manifest["settings"])
    if manifest["graph"] != layout.graph(manifest["graph"]["topology"], manifest["graph"]["pair_connection"],
                                         manifest["settings"], kind):
        raise ValueError("Схема сборки отличается от поддерживаемой")
    names = _names(kind, manifest["settings"]["channel"])
    components = layout.components(kind)
    entries = archive.infolist()
    present = set(archive.namelist())
    for name in sorted(names - (present & set(manifest["files"]))):
        repo = components.get(name, {}).get("repo")
        revision = _revision(repo) if repo else "формат 1"
        raise ValueError(f"Сборка неполна. Требуется {name} ({revision})")
    if (len(entries) != len(names) + 1 or present != names | {"manifest.json"}
            or set(manifest["files"]) != names or set(manifest["models"]) != set(layout.roles(kind))):
        raise ValueError("Внутри модели найдены повторяющиеся или неподдерживаемые файлы")
    for role in layout.roles(kind):
        if manifest["models"].get(role) != _model_info(role):
            raise ValueError(f"Неподдерживаемая редакция модели {role}")
    for name in names:
        meta = manifest["files"][name]
        info = archive.getinfo(name)
        if (info.file_size != meta["size"] or info.compress_type != ZIP_STORED
                or not re.fullmatch(r"[0-9a-f]{64}", meta["sha256"])):
            raise ValueError(f"Неверное описание файла {name}")
        if name in components:
            pinned = components[name]
            if meta["sha256"] != pinned["sha256"] or info.file_size != pinned["size"]:
                raise ValueError(f'Сборка требует другой файл {name}. Поддерживается редакция '
                                 f'{_revision(pinned["repo"])}')
        elif info.file_size > LINK_LIMIT:
            raise ValueError(f"Недопустимый размер {name}")
    return manifest


def _revision(repo):
    return next(data["revision"] for role, data in layout.CATALOG["models"].items()
                if layout.CATALOG["ids"][role] == repo)


def _model_info(role):
    return dict(id=layout.CATALOG["ids"][role], **layout.CATALOG["models"][role])


def _check_directory_size(path):
    # Bound the directory before ZipFile allocates Python objects for its entries.
    # All supported v1 graphs fit in 2 GB and have a plain, comment-free end record.
    size = Path(path).stat().st_size
    if not 22 <= size < 36*1024**3:
        raise ValueError("Неверный размер контейнера .lopatanet")
    with Path(path).open("rb") as source:
        source.seek(-22, os.SEEK_END)
        signature, disk, directory_disk, on_disk, count, length, offset, comment = struct.unpack(
            "<4s4H2IH", source.read(22))
        if size>=2**31:
            source.seek(-42,os.SEEK_END)
            locator,volume,record,volumes = struct.unpack("<4sIQI",source.read(20))
            if locator!=b"PK\x06\x07" or volume or volumes!=1 or record!=size-98:
                raise ValueError("Неверный указатель ZIP64.")
            source.seek(record)
            sig,n,_,_,disk64,dir64,used64,count64,len64,offset64=struct.unpack("<4sQ2H2I4Q",source.read(56))
            if (sig!=b"PK\x06\x06" or n!=44 or disk64 or dir64 or not 0<used64==count64<=256
                or count!=count64 or on_disk!=used64 or length!=len64 or offset not in (offset64,0xffffffff)
                or len64>128*1024 or offset64+len64!=record or signature!=b"PK\x05\x06" or disk or directory_disk or comment):
                raise ValueError("Неверное центральное оглавление ZIP64.")
            return
    if (signature != b"PK\x05\x06" or disk or directory_disk or comment or on_disk != count
            or count > 256 or length > 128 * 1024 or offset + length != size - 22):
        raise ValueError("Неподдерживаемая структура контейнера .lopatanet версии 1")


def inspect(model):
    _check_directory_size(model)
    with ZipFile(model) as archive:
        return _inspect(archive)


def unpack(model, output):
    _check_directory_size(model)
    destination = Path(output)
    if destination.exists() and (destination.is_symlink() or not destination.is_dir() or any(destination.iterdir())):
        raise ValueError("Для распаковки требуется пустая папка")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=destination.parent) as work, ZipFile(model) as archive:
        manifest = _inspect(archive)
        stage = Path(work) / "content"
        for name, meta in manifest["files"].items():
            target = stage / name
            target.parent.mkdir(parents=True, exist_ok=True)
            checksum = hashlib.sha256()
            with archive.open(name) as source, target.open("xb") as file:
                while chunk := source.read(1024 * 1024):
                    checksum.update(chunk)
                    file.write(chunk)
            if checksum.hexdigest() != meta["sha256"]:
                raise ValueError(f"Не совпала контрольная сумма {name}")
        (stage / "manifest.json").write_bytes(archive.read("manifest.json"))
        if destination.exists():
            destination.rmdir()
        stage.rename(destination)
    return manifest


def _copy_to_archive(archive, name, path):
    checksum = hashlib.sha256()
    size = 0
    with path.open("rb") as source, archive.open(name, "w", force_zip64=True) as sink:
        while chunk := source.read(1024 * 1024):
            sink.write(chunk)
            checksum.update(chunk)
            size += len(chunk)
    return dict(sha256=checksum.hexdigest(), size=size)


def pack(source, kind, output, app_version, parent=None, overrides=None, settings=None):
    if kind in ("medium","heavy"):
        from .medium.package import pack as pack_medium
        return pack_medium(kind,source,output,app_version,parent,overrides,settings)
    destination = Path(output).resolve()
    if parent and (destination == Path(parent).resolve()
                   or destination.exists() and destination.samefile(parent)):
        raise ValueError("Сохраните свою версию в другой файл")
    if not isinstance(app_version, str) or not re.fullmatch(r"[0-9A-Za-z.+-]{1,64}", app_version):
        raise ValueError("Требуется версия программы, создавшей сборку")
    destination.parent.mkdir(parents=True, exist_ok=True)
    with ExitStack() as stack:
        work = Path(stack.enter_context(tempfile.TemporaryDirectory(dir=destination.parent)))
        previous = unpack(parent, work / "parent") if parent else {}
        installed = layout.source_root(source)
        paths = {}
        for name, entry in layout.components(kind).items():
            if name in previous.get("files", {}):
                paths[name] = work / "parent" / name
            elif installed:
                paths[name] = installed / entry["repo"] / entry["file"]
            else:
                raise ValueError(f'Укажите папку модели {entry["repo"]} редакции {_revision(entry["repo"])}')
        configuration = layout.settings({**previous.get("settings", {}), **(settings or {})})
        topology = previous.get("graph", {}).get("topology", layout.CATALOG["topology"])
        override_dir = Path(overrides) if overrides else None
        if override_dir is not None and not override_dir.is_dir():
            raise ValueError("Папка обученных связей отсутствует")
        if override_dir and (override_dir / "topology.json").is_file():
            topology = json.loads((override_dir / "topology.json").read_text("utf-8-sig"))["topology"]
        selected_weights = layout.weights(kind, configuration["channel"])
        for role in selected_weights:
            name = f"weights/{role}.pt"
            if override_dir and (override_dir / f"{role}.pt").is_file():
                path = override_dir / f"{role}.pt"
                load_weights(path)
            elif name in previous.get("files", {}):
                path = work / "parent" / name
            else:
                path = layout.REFERENCE / name
                if layout.digest(path) != layout.CATALOG["weights"][role]:
                    raise ValueError(f"Требуется переустановить эталонную связь {role}")
            paths[name] = path
        schema = layout.graph(topology, load_weights(paths["weights/pair.pt"])["mode"], configuration, kind)
        for name in layout.checks(kind):
            paths["checks/" + name] = layout.REFERENCE / "checks" / name
        paths["licenses/Apache-2.0.txt"] = layout.REFERENCE / "licenses/Apache-2.0.txt"
        if destination in (path.resolve() for path in paths.values()):
            raise ValueError("Выберите новый файл вне исходных компонентов")
        manifest = dict(format="lopata-neuro", version=1, kind=kind, graph=schema, settings=configuration,
                        models={k: _model_info(k) for k in layout.roles(kind)}, packages=layout.CATALOG["packages"],
                        provenance=dict(application="LOPATA", app_version=app_version, scenario="Нейроконструктор",
                                        operation="export", created_utc=datetime.now(timezone.utc).isoformat()),
                        files={})
        # A changed graph must reproduce its own pre-export control answers on reopening.
        # Also keep the independent ground-truth labels and original research answers.
        custom = (configuration != layout.DEFAULTS or topology != layout.CATALOG["topology"]
                  or any(layout.digest(paths[f"weights/{r}.pt"]) != layout.CATALOG["weights"][r]
                         for r in selected_weights))
        if custom:
            from .validation import capture
            staged = work / "before"
            for name, path in paths.items():
                target = staged / name
                target.parent.mkdir(parents=True, exist_ok=True)
                try:
                    os.link(path, target)
                except OSError:
                    shutil.copyfile(path, target)
            (staged / "manifest.json").write_text(json.dumps(manifest), "utf-8")
            for suite, rows in capture(staged).items():
                target = work / (suite + ".json")
                target.write_text(json.dumps(rows, ensure_ascii=False), "utf-8")
                paths[f"checks/{suite}.json"] = target
        temporary = work / "model.tmp"
        with ZipFile(temporary, "x", compression=ZIP_STORED, allowZip64=True) as archive:
            for name, path in paths.items():
                if not path.is_file():
                    raise ValueError(f"Отсутствует {name}")
                manifest["files"][name] = _copy_to_archive(archive, name, path)
            archive.writestr("manifest.json", json.dumps(manifest, ensure_ascii=False, indent=2))
        inspect(temporary)
        with temporary.open("r+b") as flushed:
            os.fsync(flushed.fileno())
        os.replace(temporary, destination)
    return manifest
