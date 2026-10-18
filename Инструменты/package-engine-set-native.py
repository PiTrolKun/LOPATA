"""Build pinned native payloads from the verified update stand, without publishing.

The official binaries are reused exactly. Source headers and original license
files travel with each executor; the manifest is a complete file allowlist.
"""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import zipfile

PINNED = {
    "llama": {
        "revision": "620d2225cc436ab0b001e3200e590e3b286ef9ab",
        "version": "b11669-lopata1",
        "source_sha256": "48b431f8984dcc79efd2589de85fc53e42c6193522c97641e8dbc4376ebb3a5a",
        "archives": {
            "cpu": "37b3efcf962847ca869745fd4ccf7f5e7e8dacfeff9fa3523b4ca8cdc66d632b",
            "vulkan": "2c07574e7bde71321cb785282b20cc821e951132149eeceeea32ddebdbf0598e",
        },
    },
    "sd": {
        "revision": "f0aac524d3f29140e60ba78f3cfb16d6acea87db",
        "version": "f0aac52-lopata1",
        "source_sha256": "14e2183fd32fb5b9a86c4608309e4cb2da4a5e3828993a2b185e59f59dbea5ad",
        "archives": {
            "cpu": "8544332b2fe7adb07eab45b61ea76d86f92b0cbbdc9b03585b73453db5b7f52b",
            "vulkan": "64ae41edf331723871ff6925f6f9d8bb46f6407d49e51f2073ef11f646765cb2",
            "cuda12": "68a7b08b87925a39dc1661d872a7625e633df90dc6e3c32165e7cb8815b7febf",
        },
    },
}


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def verify_tested_source(official, verified, records):
    # The stand evidence alone is not a substitute for the official archive.
    # Verify every original entry again, then check stand-added redistributables
    # against the recorded, previously verified byte identities.
    with zipfile.ZipFile(official) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            if len(Path(entry.filename).parts) != 1:
                raise ValueError("Unexpected official archive layout")
            destination = verified / entry.filename
            if not destination.is_file() or sha(destination) != hashlib.sha256(archive.read(entry)).hexdigest():
                raise ValueError("Tested file differs from the official archive: " + str(destination))
    expected = {item["path"]: item for item in records}
    for file in verified.iterdir():
        if not file.is_file():
            raise ValueError("Unexpected directory in tested flat native bundle")
        item = expected.get(file.name)
        if item is None or file.stat().st_size != item["size"] or sha(file) != item["sha256"].lower():
            raise ValueError("Native input differs from the saved stand receipt: " + str(file))


def copy_notices(source_zip, target, family):
    # Retain standalone licenses throughout source plus vendored source headers
    # carrying original copyright/dual-license notices, as in prior packages.
    with zipfile.ZipFile(source_zip) as archive:
        for entry in archive.infolist():
            if entry.is_dir():
                continue
            relative = Path(*Path(entry.filename).parts[1:])
            parts = relative.parts
            if not parts:
                continue
            license_file = any(word in relative.name.upper() for word in ("LICENSE", "COPYING", "PATENTS", "AUTHORS", "NOTICE"))
            vendor_header = relative.suffix in (".h", ".hpp", ".c", ".cpp", ".comp") and (
                parts[0] in ("vendor", "thirdparty") or str(relative).replace("\\", "/").startswith("ggml/src/ggml-vulkan/"))
            if license_file or vendor_header:
                destination = target / "Notices" / "upstream" / relative
                destination.parent.mkdir(parents=True, exist_ok=True)
                destination.write_bytes(archive.read(entry))


def build(root, output):
    stand = root / "Тесты/RuntimeUpdates_20261018"
    sources = root / "Runtime/EngineSetBuild/002"
    result = []
    for family, pin in PINNED.items():
        source_zip = sources / (family + "-source.zip")
        if sha(source_zip) != pin["source_sha256"]:
            raise ValueError("Source archive checksum mismatch: " + family)
        for flavor, expected in pin["archives"].items():
            if family == "llama":
                official = stand / "chat/downloads" / f"llama-b11669-bin-win-{flavor}-x64.zip"
                verified = stand / "chat/runtimes" / f"candidate-{flavor}"
                redist = stand / "chat/runtimes" / f"baseline-{flavor}"
            else:
                official = stand / "image/downloads" / f"sd-master-f0aac52-bin-win-{flavor}-x64.zip"
                verified = stand / "image" / f"candidate973-{flavor}"
                redist = stand / "image" / ("baseline929-vulkan" if flavor == "cuda12" else f"baseline929-{flavor}")
            if sha(official) != expected:
                raise ValueError("Official archive checksum mismatch: " + str(official))
            evidence = json.loads((stand / ("chat/runtime-verification.json" if family == "llama" else "image/runtime-files-final.json")).read_text(encoding="utf-8-sig"))
            receipts = next(row["files"] for row in evidence if
                            (row.get("role") == "candidate" and row.get("backend") == flavor if family == "llama"
                             else row.get("runtime") == "candidate973-" + flavor))
            verify_tested_source(official, verified, receipts)
            destination = output / family / flavor
            if destination.exists():
                raise FileExistsError("Use a fresh payload directory: " + str(destination))
            destination.mkdir(parents=True)
            # These are the exact tested flat directories, including per-backend
            # dispatch DLLs. Keep extra executables separate from automatic use.
            for item in verified.iterdir():
                if item.is_file():
                    shutil.copy2(item, destination / item.name)
            for name in ("msvcp140.dll", "msvcp140_codecvt_ids.dll", "vcruntime140.dll", "vcruntime140_1.dll", "vcomp140.dll"):
                if not (destination / name).exists() and (redist / name).exists():
                    shutil.copy2(redist / name, destination / name)
            if flavor == "vulkan" and not (destination / "vulkan-1.dll").exists():
                shutil.copy2(redist / "vulkan-1.dll", destination / "vulkan-1.dll")
            copy_notices(source_zip, destination, family)
            notices = destination / "Notices"
            notices.mkdir(exist_ok=True)
            shutil.copy2(root / "Исходники/AIHub/Licenses/texts/music-MSVC-RUNTIME.txt", notices / "MSVC-RUNTIME.txt")
            if flavor == "vulkan":
                shutil.copy2(root / "Исходники/AIHub/Licenses/texts/llama-Vulkan-Loader-LICENSE.txt", notices / "Vulkan-Loader-LICENSE.txt")
            if flavor == "cuda12":
                # The verified stand has the original matching CUDA redistributables.
                # Their complete notices accompany the old official archive.
                cuda_terms = root / "Исходники/AIHub/Licenses/texts"
                for license_path in cuda_terms.glob("*CUDA*"):
                    shutil.copy2(license_path, notices / license_path.name)
            files = [{"path": p.relative_to(destination).as_posix(), "size": p.stat().st_size, "sha256": sha(p)}
                     for p in sorted(destination.rglob("*")) if p.is_file()]
            manifest = {"schema": 1, "version": pin["version"], "revision": pin["revision"], "backend": flavor,
                        "officialArchiveSha256": expected, "sourceArchiveSha256": pin["source_sha256"], "files": files}
            path = destination / "runtime-manifest.json"
            path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
            result.append({"family": family, "backend": flavor, "directory": str(destination), "revision": pin["revision"],
                           "version": pin["version"], "fileCount": len(files), "manifestSha256": sha(path),
                           "installedBytes": sum(p.stat().st_size for p in destination.rglob("*") if p.is_file())})
    (output / "native-payloads.json").write_text(json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(json.dumps(result, ensure_ascii=False, indent=2))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", type=Path, required=True)
    parser.add_argument("--output", type=Path, required=True)
    options = parser.parse_args()
    build(options.root.resolve(), options.output.resolve())
