"""Data-only large model format, with original assets pinned independently of the archive."""
import json
import os
import re
import tempfile
from datetime import datetime,timezone
from pathlib import Path
from zipfile import ZipFile,ZIP_STORED

from .. import layout
from .profiles import catalog,FAMILIES

CHECKS = {"checks/"+n for n in layout.checks("trio")}
ANSWERS = {"checks/pair.json","checks/trio.json"}
FIXED = (CHECKS-ANSWERS)|{"weights/pair.pt","licenses/Apache-2.0.txt"}
GRAPH = dict(topology="late",input="image-command-two-wav",voice="ordinal-acoustic-message",
             vision=["omniparser-detect","omniparser-caption-encoder","qwen3-vl-vision","paddleocr-vl-vision",
                     "pp-ocr-det","pp-ocr-en","pp-ocr-eslav","pp-doclayout"],text_only="reference-pair")


def inspect_zip(archive,manifest):
    from ..archive import LINK_LIMIT
    kind=manifest["kind"]
    if kind not in FAMILIES:
        raise ValueError("Неизвестный профиль модели.")
    pinned=catalog(kind)
    assets={"models/"+f["repo"]+"/"+f["file"]:f for f in pinned["files"]}
    names = set(assets)|FIXED|ANSWERS|{"weights/"+kind+".pt"}
    if (manifest["models"]!=pinned["models"] or manifest["graph"]!=GRAPH
        or len(archive.infolist())!=len(names)+1 or set(archive.namelist())!=names|{"manifest.json"}
        or set(manifest["files"])!=names):
        raise ValueError("Неверный состав среднего набора.")
    layout.settings(manifest["settings"])
    for name in names:
        meta,info = manifest["files"][name],archive.getinfo(name)
        if info.file_size!=meta["size"] or info.compress_type!=ZIP_STORED or not re.fullmatch("[0-9a-f]{64}",meta["sha256"]):
            raise ValueError(f"Неверное описание {name}")
        if name in assets:
            if any(meta[k]!=assets[name][k] for k in ("size","sha256")):
                raise ValueError(f"Неизвестная редакция {name}")
        elif info.file_size>LINK_LIMIT or name in FIXED and meta["sha256"]!=layout.digest(layout.REFERENCE/name):
            raise ValueError(f"Неверный контрольный файл {name}")
    return manifest


def pack(kind,source,output,app_version,parent,overrides,settings):
    from ..archive import _copy_to_archive,inspect,load_weights,unpack
    output = Path(output).resolve()
    if not re.fullmatch("[0-9A-Za-z.+-]{1,64}",app_version):
        raise ValueError("Недопустимая версия приложения.")
    if parent and (output==Path(parent).resolve() or output.exists() and output.samefile(parent)):
        raise ValueError("Сохраните сборку в новый файл.")
    output.parent.mkdir(parents=True,exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output.parent) as temp:
        work = Path(temp)
        prior = unpack(parent,work/"parent") if parent else {}
        if parent and prior["kind"]!=kind:
            raise ValueError("Нужна родительская сборка того же состава.")
        base = work/"parent"/"models" if parent else layout.source_root(source)
        pinned=catalog(kind)
        paths = {"models/"+f["repo"]+"/"+f["file"]:base/f["repo"]/f["file"] for f in pinned["files"]}
        paths.update({name:layout.REFERENCE/name for name in FIXED})
        link="weights/"+kind+".pt"
        for name in ANSWERS|{link}:
            path = Path(overrides)/Path(name).name if overrides else work/"parent"/name
            if not path.is_file():
                raise ValueError("Сначала настройте средний набор и проверьте результат.")
            paths[name]=path
        load_weights(paths[link])
        if any(output==p.resolve() or output.exists() and output.samefile(p) for p in paths.values()):
            raise ValueError("Выберите новый файл вне исходных компонентов.")
        info = dict(format="lopata-neuro",version=2,kind=kind,graph=GRAPH,
                    settings=layout.settings({**prior.get("settings",{}),**(settings or {})}),
                    models=pinned["models"],files={},provenance=dict(application="LOPATA",app_version=app_version,
                    scenario="Нейроконструктор",operation="export",created_utc=datetime.now(timezone.utc).isoformat()))
        staged = work/"model.tmp"
        with ZipFile(staged,"x",compression=ZIP_STORED,allowZip64=True) as archive:
            for name,path in paths.items():
                info["files"][name] = _copy_to_archive(archive,name,path)
            archive.writestr("manifest.json",json.dumps(info,ensure_ascii=False))
        inspect(staged)
        with staged.open("rb+") as f:
            os.fsync(f.fileno())
        os.replace(staged,output)
    return info
