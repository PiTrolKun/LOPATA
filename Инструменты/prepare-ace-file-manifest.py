"""Build the shipped ACE overlay manifest from SHA256-verified wheels, offline."""
import argparse
import hashlib
import json
import pathlib
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument("wheel_root", type=pathlib.Path)
parser.add_argument("catalog", type=pathlib.Path)
parser.add_argument("output", type=pathlib.Path)
args = parser.parse_args()
catalog = json.loads(args.catalog.read_text(encoding="utf-8-sig"))
files, wheels, seen = [], [], set()
for wheel in catalog["Runtime"]:
    path = args.wheel_root / wheel["RelativePath"]
    with path.open("rb") as stream:
        digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if path.stat().st_size != wheel["SizeBytes"] or digest != wheel["Sha256"].lower():
            raise ValueError("Wheel does not match the pinned catalog: " + path.name)
        wheels.append({"path": wheel["RelativePath"], "sha256": digest})
        stream.seek(0)
        with zipfile.ZipFile(stream) as archive:
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                parts = entry.filename.split("/")
                if any(p in ("", ".", "..") for p in parts) or "\\" in entry.filename or ":" in entry.filename:
                    raise ValueError("Unsafe wheel path")
                if (entry.external_attr >> 16) & 0xF000 == 0xA000:
                    raise ValueError("Wheel symlink")
                if parts[0].endswith(".data"):
                    scheme = {"purelib": "Lib/site-packages/", "platlib": "Lib/site-packages/",
                              "scripts": "Scripts/", "data": "", "headers": "Include/" + parts[0] + "/"}
                    destination = scheme[parts[1]] + "/".join(parts[2:])
                else:
                    destination = "Lib/site-packages/" + entry.filename
                if destination.casefold() in seen:
                    raise ValueError("Conflicting wheel path")
                seen.add(destination.casefold())
                with archive.open(entry) as source:
                    sha = hashlib.file_digest(source, "sha256").hexdigest()
                files.append({"path": destination, "size": entry.file_size, "sha256": sha})
args.output.write_text(json.dumps({"wheels": wheels, "files": files}, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(f"Verified {len(wheels)} wheels; {len(files)} files; {sum(f['size'] for f in files)} bytes")
