"""Build the bundled Unicode palette from the pinned, locally downloaded source files."""
import argparse
import bisect
import gzip
import hashlib
import json
import re
import xml.etree.ElementTree as ET
from pathlib import Path

parser = argparse.ArgumentParser()
parser.add_argument("--source", required=True, type=Path)
args = parser.parse_args()
root = Path(__file__).resolve().parents[2]
output = root / "Исходники/AIHub/Tools/OfficeUnicode"
output.mkdir(parents=True, exist_ok=True)
downloads = json.loads((args.source / "downloads.json").read_text(encoding="utf-8"))
for item in downloads:
    data = (args.source / item["file"]).read_bytes()
    if hashlib.sha256(data).hexdigest() != item["sha256"]:
        raise ValueError("Source digest mismatch: " + item["file"])

blocks = []
for line in (args.source / "Blocks.txt").read_text(encoding="utf-8").splitlines():
    line = line.split("#", 1)[0].strip()
    if line:
        points, name = line.split(";", 1)
        first, last = points.strip().split("..")
        blocks.append({"First": int(first, 16), "Last": int(last, 16), "Name": name.strip()})
starts = [b["First"] for b in blocks]

names = {}
for line in (args.source / "DerivedName.txt").read_text(encoding="utf-8").splitlines():
    line = line.split("#", 1)[0].strip()
    if not line:
        continue
    points, name = (s.strip() for s in line.split(";", 1))
    ends = points.split("..")
    for cp in range(int(ends[0], 16), int(ends[-1], 16) + 1):
        names[cp] = name.replace("*", f"{cp:04X}")

russian, english = {}, {}
for filename in ["ru.xml", "ru-derived.xml", "en.xml", "en-derived.xml"]:
    for node in ET.parse(args.source / filename).findall(".//annotation"):
        key = node.attrib["cp"].replace("\ufe0f", "")
        annotations = russian if filename.startswith("ru") else english
        entry = annotations.setdefault(key, {"name": "", "words": set()})
        text = "".join(node.itertext()).strip()
        if node.attrib.get("type") == "tts":
            entry["name"] = text
        else:
            entry["words"].add(text)

entries = {}
first_range = None
for line in (args.source / "UnicodeData.txt").read_text(encoding="utf-8").splitlines():
    fields = line.split(";")
    cp, name, category = int(fields[0], 16), fields[1], fields[2]
    if name.endswith(", First>"):
        first_range = cp
        continue
    begin = first_range if name.endswith(", Last>") else cp
    first_range = None
    if category in {"Cc", "Cs", "Co", "Cn"}:
        continue
    for point in range(begin, cp + 1):
        text = chr(point)
        block = bisect.bisect_right(starts, point) - 1
        entries[text] = [point, text, names.get(point, name), category, block, "", "", False, -1]

emoji_order = 0
for line in (args.source / "emoji-test.txt").read_text(encoding="utf-8").splitlines():
    if not line or line.startswith("#"):
        continue
    data, comment = line.split("#", 1)
    codes, qualification = data.split(";", 1)
    if qualification.strip() not in {"fully-qualified", "component"}:
        continue
    text = "".join(chr(int(s, 16)) for s in codes.split())
    name = re.split(r"\sE[0-9.]+\s", comment.strip(), maxsplit=1)[1]
    point = ord(text[0])
    entry = entries.setdefault(text, [point, text, name, "So", bisect.bisect_right(starts, point) - 1, "", "", True, -1])
    translated = russian.get(text.replace("\ufe0f", ""), {})
    original = english.get(text.replace("\ufe0f", ""), {})
    entry[5] = translated.get("name", "")
    entry[6] = " | ".join([name, original.get("name", ""), *sorted(original.get("words", [])), *sorted(translated.get("words", []))])
    entry[7] = True
    entry[8] = emoji_order
    emoji_order += 1

currency_glyphs = {}
for language in ["ru", "en"]:
    for currency in ET.parse(args.source / ("currencies-" + language + ".xml")).findall(".//currencies/currency"):
        glyphs = {c for n in currency.findall("symbol") for c in (n.text or "") if c in entries and entries[c][3] == "Sc"}
        currency_glyphs.setdefault(currency.attrib["type"], set()).update(glyphs)

for language in ["ru", "en"]:
    for currency in ET.parse(args.source / ("currencies-" + language + ".xml")).findall(".//currencies/currency"):
        names = [n.text for n in currency.findall("displayName") if n.text]
        glyphs = currency_glyphs[currency.attrib["type"]]
        for glyph in glyphs:
            entry = entries[glyph]
            entry[6] += " | " + currency.attrib["type"] + " | " + " | ".join(names)
            if language == "ru" and names and not entry[5]:
                entry[5] = names[0]

rows = sorted(entries.values(), key=lambda e: (e[0], e[1]))
def field(value):
    return str(value).replace("\t", " ").replace("\r", " ").replace("\n", " ")
payload = "\n".join("\t".join([f"{e[0]:X}", *(field(v) for v in e[1:7]), "1" if e[7] else "0", str(e[8])]) for e in rows).encode("utf-8")
compressed = gzip.compress(payload, mtime=0)
(output / "characters.tsv.gz").write_bytes(compressed)
manifest = {"Version": "18.0.0", "Cldr": "49", "Count": len(rows), "Emoji": sum(e[7] for e in rows),
            "Blocks": blocks, "Sources": downloads,
            "Files": [{"Path": "characters.tsv.gz", "Size": len(compressed), "Sha256": hashlib.sha256(compressed).hexdigest()}]}
(output / "manifest.json").write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
print(json.dumps({"charactersAndSequences": len(rows), "emoji": manifest["Emoji"], "bytes": len(compressed), "blocks": len(blocks)}))
