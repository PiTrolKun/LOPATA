"""Package pinned small H3 inputs without modifying originals; publish only by explicit switch."""
import argparse
import hashlib
import json
from pathlib import Path
import shutil
import subprocess
import zipfile

REPO = "PiTrolKun/LOPATA"
TAG = "h3-components-20261029"


def sha(path):
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest()


def gh(*args):
    return subprocess.check_output(["gh", *args], encoding="utf-8")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--models", required=True, type=Path)
    parser.add_argument("--output", required=True, type=Path)
    parser.add_argument("--publish", action="store_true")
    args = parser.parse_args()
    root = Path(__file__).resolve().parents[1]
    parts = json.loads((root / "Исходники/AIHub/Tools/video-h3-components.json").read_text(encoding="utf-8-sig"))
    catalog = json.loads((root / "Исходники/AIHub/Licenses/catalog.json").read_text(encoding="utf-8-sig"))
    args.output.mkdir(parents=True, exist_ok=True)
    records, licenses = [], {}
    for part in parts:
        if part["Id"] in ("video-h3-models", "video-h3-upscale-model"):
            continue
        terms = next(entry for entry in catalog if entry["Id"] == part["Id"])
        for text in terms["Texts"]:
            path = root / "Исходники/AIHub/Licenses" / text
            if not path.is_file():
                raise ValueError("Missing original licence: " + text)
            licenses[part["Id"] + "/" + path.name] = path
        for item in part["Files"]:
            if item["SizeBytes"] > 32 * 1024**2:
                raise ValueError("Large model cannot enter small-input bundle")
            source = args.models / "Video/H3" / part["Id"] / part["Revision"] / item["RelativePath"]
            if source.stat().st_size != item["SizeBytes"] or sha(source) != item["Sha256"]:
                raise ValueError("Pinned bytes differ: " + str(source))
            name = part["Id"] + "--" + part["Revision"] + "--" + item["RelativePath"].replace("/", "__")
            target = args.output / name
            if target.exists() and sha(target) != item["Sha256"]:
                raise ValueError("Existing package differs")
            if not target.exists():
                shutil.copyfile(source, target)
            records.append(dict(component=part["Id"], revision=part["Revision"], asset=name,
                                size=item["SizeBytes"], sha256=item["Sha256"],
                                upstream=item.get("UpstreamUrl", item["SourceUrl"]),
                                licence=part["License"]))
    manifest = args.output / "h3-small-inputs.json"
    manifest.write_text(json.dumps(records, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    notices = args.output / "h3-small-inputs-notices.zip"
    with zipfile.ZipFile(notices, "w", zipfile.ZIP_DEFLATED) as archive:
        for name, source in sorted(licenses.items()):
            archive.writestr(zipfile.ZipInfo(name), source.read_bytes())
        archive.writestr(zipfile.ZipInfo("README.txt"), "Original licences and pinned upstream sources accompany h3-small-inputs.json.\n"
                         "Source ZIPs are redistributed unmodified. Large H3 weights are not included.\n")
    assets = [args.output / record["asset"] for record in records] + [manifest, notices]
    proof = [{"name": p.name, "size": p.stat().st_size, "sha256": sha(p)} for p in assets]
    (args.output / "expected-assets.json").write_text(json.dumps(proof, indent=2), encoding="utf-8")
    if args.publish:
        feed = json.loads(gh("api", "repos/" + REPO + "/releases?per_page=100"))
        release = next((r for r in feed if r["tag_name"] == TAG), None)
        if release is None:
            gh("release", "create", TAG, "--repo", REPO, "--draft", "--prerelease", "--title", "Pinned H3 small components",
               "--notes", "Unmodified pinned source archives, workflow and small companion files for LOPATA H3. "
               "Original authors, source revisions, SHA256 and licences are included. Large H3 weights are downloaded separately.")
            release = next(r for r in json.loads(gh("api", "repos/" + REPO + "/releases?per_page=100")) if r["tag_name"] == TAG)
        existing = {a["name"]: a for a in release["assets"]}
        for item, path in zip(proof, assets):
            if path.name not in existing:
                if not release["draft"]:
                    raise ValueError("Published assets are immutable")
                gh("release", "upload", TAG, str(path), "--repo", REPO)
        current = json.loads(gh("api", "repos/" + REPO + "/releases/" + str(release["id"])))
        for item in proof:
            found = next(a for a in current["assets"] if a["name"] == item["name"])
            if found["size"] != item["size"] or found["digest"] != "sha256:" + item["sha256"]:
                raise ValueError("Server asset differs: " + item["name"])
        if current["draft"]:
            gh("api", "--method", "PATCH", "repos/" + REPO + "/releases/" + str(release["id"]), "-F", "draft=false")
        print("Published and server-verified", TAG, len(assets), "assets")
    else:
        print("Prepared only:", len(assets), "assets", sum(p["size"] for p in proof), "bytes")


if __name__ == "__main__":
    main()
