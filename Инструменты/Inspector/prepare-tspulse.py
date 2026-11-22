"""Reproduce the IBM TSPulse overlay for LOPATA's managed Python CPU environment."""

import argparse
import hashlib
import json
import platform
import shutil
import subprocess
import sys
import tempfile
import urllib.request
import zipfile
from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
MODEL_REVISION = "2e64fcdc2a06d3565dfadaf0065c0ab5055f80f2"
MODEL_REPO = "https://huggingface.co/ibm-granite/granite-timeseries-tspulse-r1"
PYTHON_CPU = "py312-torch210-cpu-transformers530-1"
MODEL_FILES = {
    "config.json": "e33230f47c91dfe4e848ee743924a62a26f710fe7f194566d92835f27c1c6d8b",
    "model.safetensors": "57fa03b67d1473a7253ac37801b24c391d1fa931395db986743e2f59e556b9ac",
}


def copy_remote(url, path):
    with urllib.request.urlopen(url, timeout=120) as source, path.open("wb") as output:
        shutil.copyfileobj(source, output)


def digest(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def inputs(tree, prefix):
    for source in sorted(tree.rglob("*")):
        relative = source.relative_to(tree)
        if source.is_file() and "__pycache__" not in relative.parts and source.suffix != ".pyc":
            yield source, prefix + "/" + relative.as_posix()


def bundle(archive, sources):
    receipts = []
    with zipfile.ZipFile(archive, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=3) as output:
        for source, name in sources:
            info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            info.file_size = source.stat().st_size
            sha = hashlib.sha256()
            with source.open("rb") as original, output.open(info, "w", force_zip64=True) as compressed:
                for chunk in iter(lambda: original.read(1024 * 1024), b""):
                    sha.update(chunk)
                    compressed.write(chunk)
            receipts.append({"path": name, "length": info.file_size, "sha256": sha.hexdigest()})
    return receipts


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--model-cache", type=Path, help="Reuse the pinned files after validating both SHA-256 hashes.")
    args = parser.parse_args()
    if sys.platform != "win32" or sys.version_info[:2] != (3, 12) or platform.machine().lower() != "amd64":
        parser.error("The pinned overlay is for Windows x64 CPython 3.12.")
    output = ROOT / "Runtime/Inspector"
    output.mkdir(parents=True, exist_ok=True)
    with tempfile.TemporaryDirectory(dir=output, prefix=".tspulse-") as work:
        stage = Path(work)
        overlay, model = stage / "packages", stage / "model"
        model.mkdir()
        for name, expected in MODEL_FILES.items():
            path = model / name
            if args.model_cache is None:
                copy_remote(f"{MODEL_REPO}/resolve/{MODEL_REVISION}/{name}", path)
            else:
                shutil.copyfile(args.model_cache / name, path)
            if digest(path) != expected:
                raise RuntimeError(f"The IBM file does not match the pinned revision: {name}")
        copy_remote("https://www.apache.org/licenses/LICENSE-2.0.txt", model / "LICENSE.txt")
        (model / "source.txt").write_text(f"{MODEL_REPO}/tree/{MODEL_REVISION}\n", encoding="utf-8")
        lock = Path(__file__).with_name("tspulse-wheels-win-x64.txt")
        subprocess.run([sys.executable, "-m", "pip", "install", "--disable-pip-version-check", "--isolated",
                        "--index-url", "https://pypi.org/simple", "--no-deps", "--no-compile", "--only-binary=:all:",
                        "--require-hashes", "--target", str(overlay), "-r", str(lock)], check=True)
        archive = stage / "tspulse-win-x64.zip"
        files = bundle(archive, (*inputs(overlay, "packages"), *inputs(model, "model")))
        manifest = {
            "modelRevision": MODEL_REVISION,
            "pythonRuntime": PYTHON_CPU,
            "bundleSha256": digest(archive),
            "files": files,
        }
        manifest_path = stage / "tspulse-win-x64.manifest.json"
        manifest_path.write_text(json.dumps(manifest, separators=(",", ":")) + "\n", encoding="utf-8")
        archive.replace(output / archive.name)
        manifest_path.replace(output / manifest_path.name)
    print(f"Prepared {len(files)} pinned files for the inspector CPU component.")


if __name__ == "__main__":
    main()
