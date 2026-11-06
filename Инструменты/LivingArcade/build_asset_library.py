"""Build the reviewed Living Arcade library. Developer-only Pillow + resvg tool.

Originals are copied byte-for-byte. No downloaded program, SVG script, model,
or network connection is executed. Runtime consumes prepared PNGs, not SVG.
"""
from __future__ import annotations

import argparse
import base64
import hashlib
import io
import json
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET
from collections import Counter
from datetime import datetime, timezone
from pathlib import Path

from PIL import Image, ImageDraw, ImageFont, PngImagePlugin

ROOT = Path(__file__).resolve().parents[2]
SVG_NS = "http://www.w3.org/2000/svg"
XLINK_NS = "http://www.w3.org/1999/xlink"
ET.register_namespace("", SVG_NS)
ET.register_namespace("xlink", XLINK_NS)
CREATED = datetime.now(timezone.utc).isoformat()
VERSION = (ROOT / "VERSION").read_text(encoding="utf-8-sig").strip()

PACKS = [
    dict(id="glitch", title="Glitch: inhabitants and cubimals", author="Tiny Speck; SVG conversion by bart", license="CC0-1.0", sourceUrl="https://opengameart.org/content/glitch-inhabitants-and-cubimals-svg", upstreamUrl="https://github.com/tinyspeck/glitch-items"),
    dict(id="jamie", title="8-Bit Dungeon", author="Jamie Cross", license="CC0-1.0", sourceUrl="https://jamiecross.itch.io/8-bit-dungeon-tilesetsprites", upstreamUrl="https://jamiecross.itch.io/8-bit-dungeon-tilesetsprites"),
    dict(id="kenney", title="Shape Characters 1.0", author="Kenney", license="CC0-1.0", sourceUrl="https://kenney.nl/assets/shape-characters", upstreamUrl="https://kenney.nl/assets/shape-characters"),
    dict(id="bluebird", title="Blue Bird", author="mirex", license="CC0-1.0", sourceUrl="https://opengameart.org/content/blue-bird-for-jump-and-run-arcade", upstreamUrl="https://opengameart.org/content/blue-bird-for-jump-and-run-arcade"),
    dict(id="jester", title="Cartoon Jester", author="laetissima", license="CC0-1.0", sourceUrl="https://opengameart.org/content/cartoon-jester", upstreamUrl="https://opengameart.org/content/cartoon-jester"),
    dict(id="cartoon", title="Cartoon Vector Characters", author="tzunghaor; submitted by qubodup", license="CC0-1.0", sourceUrl="https://opengameart.org/content/cartoon-vector-characters", upstreamUrl="https://openclipart.org/user-detail/tzunghaor"),
    dict(id="research", title="LOPATA reviewed visual research", author="LOPATA; generated with Gemma", license="LOPATA-generated", sourceUrl="https://huggingface.co/unsloth/gemma-4-26B-A4B-it-qat-GGUF", upstreamUrl="https://ai.google.dev/gemma/terms"),
]


def digest(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


def slug(text):
    text = re.sub(r"([a-z])([A-Z])", r"\1-\2", text)
    return re.sub(r"[^a-z0-9]+", "-", text.lower()).strip("-")


def title(text):
    return re.sub(r"[ _-]+", " ", re.sub(r"^npc_", "", text)).strip().capitalize()


def safe_svg(path):
    """Sanitize a transient render copy, never the distributed source artwork."""
    raw = path.read_bytes()
    if len(raw) > 4_000_000 or re.search(br"<!ENTITY|<!DOCTYPE[^>]*\[", raw, re.I):
        raise ValueError("SVG size or XML entity limit")
    raw = re.sub(br'<!DOCTYPE\s+svg\s+PUBLIC\s+"[^"]*"\s+"[^"]*"\s*>', b"", raw)
    if re.search(br"<!DOCTYPE|<\?xml-stylesheet", raw, re.I):
        raise ValueError("External XML declaration")
    tree = ET.fromstring(raw)
    if tree.tag.rsplit("}", 1)[-1] != "svg":
        raise ValueError("SVG root missing")
    for parent in list(tree.iter()):
        for child in list(parent):
            if child.tag.rsplit("}", 1)[-1] == "metadata" or (child.tag.startswith("{") and not child.tag.startswith("{" + SVG_NS + "}")) or re.search(r"(?:^|;)\s*display\s*:\s*none(?:\s*;|\s*$)", child.get("style", ""), re.I) or child.get("display") == "none":
                # Hidden editor reference layers need not be resolved. In centaur.svg
                # one refers to its author's local file; removal changes no pixels.
                parent.remove(child)
    nodes = list(tree.iter())
    if len(nodes) > 20000:
        raise ValueError("SVG element limit")
    forbidden = {"script", "foreignObject", "iframe", "a", "animate", "animateTransform", "set", "audio", "video"}
    for node in nodes:
        if node.tag.rsplit("}", 1)[-1] in forbidden:
            raise ValueError("Unsupported active or external SVG element")
        for key, value in list(node.attrib.items()):
            if key.startswith("{") and not key.startswith("{" + XLINK_NS + "}"):
                del node.attrib[key]
                continue
            name = key.rsplit("}", 1)[-1]
            if name.lower().startswith("on") or name in {"src", "cursor"}:
                raise ValueError("Executable SVG attribute")
            if name == "href" and node.tag.rsplit("}", 1)[-1] == "image":
                if not value.startswith("data:image/png;base64,"):
                    raise ValueError("Only embedded PNG images allowed")
                decoded = base64.b64decode(re.sub(r"\s", "", value.partition(",")[2]), validate=True)
                if len(decoded) > 4_000_000:
                    raise ValueError("Embedded PNG size limit")
                with Image.open(io.BytesIO(decoded)) as embedded:
                    if embedded.format != "PNG" or embedded.width * embedded.height > 16_000_000:
                        raise ValueError("Embedded image is not a bounded PNG")
                    embedded.verify()
                continue
            if name == "href" and not re.fullmatch(r"#[^\s]+", value):
                raise ValueError("External SVG reference")
            if re.search(r"(?:https?:|file:|data:|javascript:|@import|expression\s*\()", value, re.I):
                raise ValueError("External SVG value")
            if any(not match.startswith("#") for match in re.findall(r"url\(\s*['\"]?([^)'\"\s]+)", value)):
                raise ValueError("External SVG URL")
        if node.tag.rsplit("}", 1)[-1] == "style" and re.search(r"@import|url\((?!\s*['\"]?#)", node.text or "", re.I):
            raise ValueError("External CSS resource")
    return tree


class Builder:
    def __init__(self, args):
        self.sources, self.research, self.output = args.sources, args.research, args.output
        self.resvg = args.resvg
        self.output.mkdir(parents=True, exist_ok=True)
        self.entries = []
        self.packs = {pack["id"]: dict(pack) for pack in PACKS}
        self.failures = []
        self.source_checks = []
        self.temp = tempfile.TemporaryDirectory(prefix="lopata-library-")
        self.work = Path(self.temp.name)

    def rel(self, path):
        return path.relative_to(self.output).as_posix()

    def copy(self, source, relative):
        target = self.output / relative
        target.parent.mkdir(parents=True, exist_ok=True)
        shutil.copyfile(source, target)
        source_hash = digest(source)
        if digest(target) != source_hash:
            raise ValueError("Original copy hash mismatch")
        self.source_checks.append(dict(path=relative, sha256=source_hash))
        return target

    def original(self, source, pack, name=None):
        return self.copy(source, f"originals/{pack}/{name or source.name}")

    def render(self, source, width=1024, bluebird=False):
        tree = safe_svg(source)
        if bluebird:
            tree.set("viewBox", "0 0 800 600")
            tree.set("width", "6400")
            tree.set("height", "4800")
        svg = self.work / "transient.svg"
        png = self.work / "transient.png"
        svg.write_text(ET.tostring(tree, encoding="unicode"), encoding="utf-8")
        result = subprocess.run([str(self.resvg), "-w", str(width), str(svg), str(png)], capture_output=True, timeout=30)
        if result.returncode != 0:
            raise ValueError("resvg: " + result.stderr.decode("utf-8", errors="replace")[-500:])
        with Image.open(png) as image:
            if image.width * image.height > 40_000_000:
                raise ValueError("Raster dimensions limit")
            return image.convert("RGBA")

    def normalize(self, images, pixel=False, preserve_canvas=False, tile=False):
        if tile:
            return [image.resize((256, 256), Image.Resampling.NEAREST if pixel else Image.Resampling.LANCZOS) for image in images]
        boxes = [image.getchannel("A").getbbox() for image in images]
        if any(box is None for box in boxes):
            raise ValueError("Empty prepared asset")
        if preserve_canvas:
            common = (0, 0, max(image.width for image in images), max(image.height for image in images))
        elif len({image.size for image in images}) == 1:
            common = (min(box[0] for box in boxes), min(box[1] for box in boxes), max(box[2] for box in boxes), max(box[3] for box in boxes))
        else:
            # Authored sheet crops: common scale; each pose has its own bottom anchor.
            common = None
        width = common[2] - common[0] if common else max(box[2] - box[0] for box in boxes)
        height = common[3] - common[1] if common else max(box[3] - box[1] for box in boxes)
        scale = min(216 / width, 208 / height)
        output = []
        for image, box in zip(images, boxes):
            crop = image.crop(common or box)
            scaled = crop.resize((max(1, round(crop.width * scale)), max(1, round(crop.height * scale))), Image.Resampling.NEAREST if pixel else Image.Resampling.LANCZOS)
            canvas = Image.new("RGBA", (256, 256))
            canvas.alpha_composite(scaled, ((256 - scaled.width) // 2, 232 - scaled.height))
            output.append(canvas)
        return output

    def add(self, pack, asset_id, label, source, images, tags, role="character", animation="none", seconds=0.12, extra=None):
        if any(entry["id"] == asset_id for entry in self.entries):
            raise ValueError("Duplicate stable asset ID: " + asset_id)
        origin = dict(author=self.packs[pack]["author"], models=[])
        entry = dict(id=asset_id, packId=pack, title=label, tags=sorted(set(tags)), role=role,
                     source=self.rel(source), sourceSha256=digest(source), frames=[], animation=animation, origin=origin)
        if extra:
            entry.update(extra)
        directory = self.output / "frames" / asset_id
        directory.mkdir(parents=True, exist_ok=True)
        for index, image in enumerate(images):
            path = directory / f"{index:02d}.png"
            provenance = dict(application="LOPATA", applicationVersion=VERSION, operation="living_arcade_library_preparation", createdUtc=CREATED,
                              assetId=asset_id, author=self.packs[pack]["author"], license=self.packs[pack]["license"],
                              sourceUrl=self.packs[pack]["sourceUrl"], sourceFile=source.name, sourceSha256=entry["sourceSha256"],
                              models=entry["origin"].get("models", []), preparation=entry.get("preparation", "rendered original artwork; no model modification"))
            if "sources" in entry:
                provenance["sources"] = entry["sources"]
            if "crop" in entry:
                provenance["crop"] = entry["crop"]
            info = PngImagePlugin.PngInfo()
            info.add_text("LOPATA", json.dumps(provenance, ensure_ascii=False, sort_keys=True))
            image.save(path, pnginfo=info, optimize=True)
            entry["frames"].append(dict(path=self.rel(path), seconds=seconds, sha256=digest(path)))
        self.entries.append(entry)
        return entry

    def safe_add_svg(self, source, pack, label=None, tags=(), role="character", name=None):
        original = self.original(source, pack, name)
        try:
            image = self.render(original)
            if image.getchannel("A").getbbox() is None and pack == "glitch" and source.stem in {"magic_rock", "npc_juju_black", "npc_juju_boss", "npc_juju_green", "npc_juju_red", "npc_juju_yellow"}:
                return self.add(pack, pack + "-" + slug(source.stem), label or title(source.stem), original, [],
                                [*tags, "source-only", "hidden-author-export"], "part", "none",
                                extra=dict(renderable=False, preparation="original authored SVG has only transparent displayed instances (opacity:0); source retained without visual repair"))
            return self.add(pack, pack + "-" + slug(name or source.stem), label or title(source.stem), original,
                            self.normalize([image]), tags, role, "none")
        except Exception as error:
            self.failures.append(dict(packId=pack, source=self.rel(original), error=str(error)))
            return None

    def notices(self):
        cc0 = self.sources / "licenses/CC0-1.0-legalcode.txt"
        self.copy(cc0, "licenses/CC0-1.0-legalcode.txt")
        folders = dict(glitch="glitch", jamie="jamie", kenney="kenney", bluebird="opengameart-blue-bird", jester="opengameart-cartoon-jester", cartoon="opengameart-cartoon-vector")
        for pack_id, folder in folders.items():
            pack = self.packs[pack_id]
            evidence = []
            for filename in ("source-page.html", "upstream-README.md", "extracted/License.txt", "extracted/License.txt.txt"):
                path = self.sources / folder / filename
                if path.exists():
                    destination = f"licenses/{pack_id}/{path.name}"
                    self.copy(path, destination)
                    evidence.append(destination)
            archives = [dict(filename=path.name, bytes=path.stat().st_size, sha256=digest(path))
                        for path in sorted((self.sources / folder).iterdir()) if path.suffix.lower() in {".zip", ".7z"}]
            notice = dict(**pack, evidence=evidence, originalArchives=archives,
                          modifications="Original source files unchanged. Prepared PNGs normalized by LOPATA; animation cycles replay author-drawn poses. Static movement is a runtime transform, not author-drawn walking.",
                          exclusions="Glitch logos and trademarks are not licensed by its CC0 dedication." if pack_id == "glitch" else "")
            target = f"licenses/{pack_id}/notice.json"
            self.write_json(target, notice)
            pack["noticePath"] = target
        pack = self.packs["research"]
        pack["noticePath"] = "licenses/research/notice.json"
        self.write_json(pack["noticePath"], dict(**pack, modifications="Selected non-franchise model outputs from the one-hour local study. These are not CC0 third-party originals; no human authorship of the drawing is asserted. Generation model/revision is recorded in each entry and prepared PNG.", excluded="Mario, Sonic, Mega Man, Pac-Man and other franchise imitations; malformed SVG and visually unconvincing candidates."))

    def glitch(self):
        animal = ("fox", "frog", "chick", "pig", "cat", "kitty", "sloth", "squid", "crab", "salmon", "bunny", "bear", "caterpillar", "horse", "flamingo", "rook")
        human = ("vendor", "master", "bartender", "smuggler", "bureaucrat", "butler", "ranger", "gwendolyn", "helga", "gnome", "ilmenskie", "uncle", "trisor", "rube")
        for source in sorted((self.sources / "glitch/extracted").rglob("*.svg")):
            name = source.stem.lower()
            tags = ["cartoon", "vector", "glitch", "cubimal" if "cubimal" in name else "inhabitant"]
            role = "character"
            if any(term in name for term in ("package", "magic_rock", "magicrock", "cactus", "_mart", "vending_machine")):
                role, tags = "prop", tags + ["background", "decoration"]
            elif "bot" in name or "widget" in name or "deimaginator" in name:
                tags.append("robot")
            elif any(term in name for term in animal):
                tags.append("animal")
            elif any(term in name for term in human):
                tags.append("human")
            else:
                tags.append("creature")
            if any(term in name for term in ("batterfly", "butterfly", "firefly", "apparition", "phantom", "rook")):
                tags.append("flying")
            self.safe_add_svg(source, "glitch", tags=tags, role=role)

    def jamie(self):
        directory = self.sources / "jamie/extracted"
        for source in sorted((directory / "Vector Files/SVG Files").glob("*.svg")):
            self.original(source, "jamie", "svg/" + source.name)
        characters = {"Ghost 01", "Spider 01", "Shroom 01"}
        tile_prefixes = ("Cave", "Brick", "Breakable Brick", "Dirt", "Grass", "Pillar", "Platform", "Lava", "Rocks", "Stalagmite", "Window")
        player_sources = {}
        for source in sorted((directory / "PNG Files").glob("*.png")):
            original = self.original(source, "jamie", "png/" + source.name)
            image = Image.open(original).convert("RGBA")
            name = source.stem
            role = "character" if name.startswith("Player") or name in characters else "tile" if name.startswith(tile_prefixes) else "prop"
            tags = ["pixel", "dungeon", "human" if name.startswith("Player") else "creature" if name in characters else "background"]
            if name.startswith("Player"):
                player_sources[name] = (original, image)
            self.add("jamie", "jamie-" + slug(name), title(name), original,
                     self.normalize([image], pixel=True, preserve_canvas=name.startswith("Player"), tile=role == "tile"), tags, role)
        for action, count, delay in (("Run", 4, 0.10), ("Climb", 2, 0.18), ("Rope", 3, 0.16)):
            items = [player_sources[f"Player {action} {index:02d}"] for index in range(1, count + 1)]
            self.add("jamie", "jamie-player-" + action.lower() + "-cycle", "Player " + action.lower() + " cycle", items[0][0],
                     self.normalize([item[1] for item in items], pixel=True, preserve_canvas=True), ["pixel", "dungeon", "human", action.lower(), "authored-poses"], animation="cycle", seconds=delay,
                     extra=dict(sources=[dict(path=self.rel(item[0]), sha256=digest(item[0])) for item in items], preparation="cycle of original Jamie Cross poses; common canvas, scale and anchor"))

    def kenney(self):
        directory = self.sources / "kenney/extracted"
        parts = {}
        for source in sorted((directory / "PNG/Default").glob("*.png")):
            original = self.original(source, "kenney", "png/" + source.name)
            image = Image.open(original).convert("RGBA")
            parts[source.stem] = (original, image)
            self.add("kenney", "kenney-" + slug(source.stem), title(source.stem), original,
                     self.normalize([image]), ["cartoon", "part", "face" if source.stem.startswith(("face", "facial")) else "body" if "body" in source.stem else "hand"], role="part")
        atlas = directory / "Vector/overview.svg"
        if atlas.exists():
            self.original(atlas, "kenney", "overview.svg")
        colors = ["blue", "green", "pink", "purple", "red", "yellow"]
        shapes = ["circle", "rhombus", "square", "squircle"]
        hands = ["closed", "open", "peace", "point", "rock", "thumb"]
        for index in range(20):
            color, shape = colors[index % len(colors)], shapes[(index // len(colors) + index) % len(shapes)]
            hand_name = f"{color}_hand_{hands[index % 6]}"
            if color == "yellow":
                hand_name = f"hand_yellow_{hands[index % 6]}"
            if hand_name not in parts:
                hand_name = f"{color}_hand_open"
            names = [f"{color}_body_{shape}", "face_" + chr(ord("a") + index % 12), hand_name]
            body, face, hand = [parts[name][1] for name in names]
            canvas = Image.new("RGBA", (160, 128))
            canvas.alpha_composite(body, (40, 24))
            canvas.alpha_composite(face, ((160 - face.width) // 2, 50))
            canvas.alpha_composite(hand.transpose(Image.Transpose.FLIP_LEFT_RIGHT), (8, 62))
            canvas.alpha_composite(hand, (117, 62))
            sources = [dict(path=self.rel(parts[name][0]), sha256=digest(parts[name][0])) for name in names]
            self.add("kenney", f"kenney-character-{index + 1:02d}-{color}-{shape}", f"Shape character {index + 1}: {color} {shape}", parts[names[0]][0],
                     self.normalize([canvas], preserve_canvas=True), ["cartoon", "creature", "shape", color, "assembled"],
                     extra=dict(sources=sources, preparation="assembled from Kenney parts by LOPATA; not model-generated"))

    def bluebird(self):
        source = self.original(self.sources / "opengameart-blue-bird/eateat_bird1.svg", "bluebird")
        sheet = self.render(source, width=6400, bluebird=True)
        boxes = [
            [(4, 2, 23, 34), (26, 2, 44, 34), (47, 2, 68, 34), (68, 2, 89, 34)],
            [(5, 37, 23, 68), (29, 37, 48, 68), (55, 37, 73, 68), (80, 37, 98, 68), (104, 37, 123, 68), (129, 37, 148, 68), (154, 37, 173, 68), (180, 37, 198, 68), (204, 37, 223, 68)],
            [(5, 72, 23, 103), (29, 72, 48, 103), (54, 72, 75, 103), (79, 72, 98, 103)],
        ]
        # Confirm exact last-row author crops from the reviewed study.
        reviewed = json.loads((self.sources.parent / "animation-demos/bluebird-original-cycles.json").read_text(encoding="utf-8"))
        boxes = reviewed["originalFrameBoundingBoxes"]
        all_crops = [sheet.crop(tuple(value * 8 for value in box)) for row in boxes for box in row]
        normalized = self.normalize(all_crops)
        position = 0
        for action, row in zip(("jump", "walk", "eat"), boxes):
            images = normalized[position:position + len(row)]
            position += len(row)
            for index, (image, box) in enumerate(zip(images, row)):
                self.add("bluebird", f"bluebird-{action}-pose-{index + 1:02d}", f"Blue Bird {action}: pose {index + 1}", source, [image],
                         ["animal", "bird", "flying", action, "authored-pose"], extra=dict(crop=list(box), preparation="original mirex pose cropped from sheet; top-row touching poses separated at x=68; no geometry added"))
            self.add("bluebird", f"bluebird-{action}-cycle", "Blue Bird " + action + " cycle", source, images,
                     ["animal", "bird", "flying", action, "authored-poses"], animation="cycle", seconds=0.10 if action == "walk" else 0.16,
                     extra=dict(crop=row, preparation="cycle of original mirex poses; common pixel scale and bottom anchor"))

    def cartoon(self):
        self.safe_add_svg(self.sources / "opengameart-cartoon-jester/jester.svg", "jester", "Cartoon jester", ["human", "jester", "cartoon"])
        for source in sorted((self.sources / "opengameart-cartoon-vector/extracted").rglob("*.svg")):
            name = source.stem.lower()
            category = "robot" if "robot" in name else "creature" if any(word in name for word in ("dragon", "orc", "centaur", "giant", "faun")) else "human"
            tags = ["cartoon", "fantasy", category]
            if "angel" in name or "dragon" in name:
                tags.append("flying")
            if "space" in name:
                tags += ["soldier", "science-fiction"]
            self.safe_add_svg(source, "cartoon", tags=tags)

    def research_assets(self):
        report = json.loads((self.research / "render-report.json").read_text(encoding="utf-8"))
        for case_id, label, tags in (("Gemma-16-own-tank", "Original research tank", ["vehicle", "tank", "research", "generated"]),
                                     ("Gemma-07-dragon", "Original research dragon", ["creature", "dragon", "flying", "research", "generated"])):
            rows = sorted((row for row in report["cases"] if row["id"] == case_id and row.get("status") == "rendered"), key=lambda row: row["frame"])
            if not rows:
                raise ValueError("Reviewed research case missing: " + case_id)
            sources = [self.original(self.research / row["svg"], "research") for row in rows]
            images = [self.render(source) for source in sources]
            origin = dict(author="LOPATA; model-generated drawing", researchCase=case_id,
                          models=[dict(id="unsloth/gemma-4-26B-A4B-it-qat-GGUF", revision=rows[0]["modelRevision"], role="SVG drawing", displayName=rows[0]["model"])])
            self.add("research", "research-" + slug(case_id), label, sources[0], self.normalize(images), tags,
                     animation="cycle" if len(images) > 1 else "none", seconds=0.22,
                     extra=dict(origin=origin, sources=[dict(path=self.rel(source), sha256=digest(source)) for source in sources],
                                preparation="reviewed original non-franchise research SVG, alpha cropped and normalized; not manually repaired",
                                visualLimitations="Research illustration, not a promise of accurate human anatomy or real vehicle engineering."))

    def write_json(self, relative, value):
        path = self.output / relative
        path.parent.mkdir(parents=True, exist_ok=True)
        path.write_text(json.dumps(value, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")

    def validate(self):
        ids = set()
        png_paths = set()
        for entry in self.entries:
            if entry["id"] in ids:
                raise ValueError("Duplicate entry")
            ids.add(entry["id"])
            source = self.output / entry["source"]
            if not source.is_file() or digest(source) != entry["sourceSha256"]:
                raise ValueError("Source hash mismatch")
            for frame in entry["frames"]:
                path = self.output / frame["path"]
                if not path.resolve().is_relative_to(self.output.resolve()) or digest(path) != frame["sha256"]:
                    raise ValueError("Frame path or hash mismatch")
                png_paths.add(path)
                with Image.open(path) as image:
                    if image.size != (256, 256) or image.mode != "RGBA" or image.getchannel("A").getextrema()[1] == 0:
                        raise ValueError("Invalid prepared RGBA frame: " + frame["path"])
                    provenance = json.loads(image.info["LOPATA"])
                    if provenance["application"] != "LOPATA" or provenance["sourceSha256"] != entry["sourceSha256"]:
                        raise ValueError("Missing PNG provenance")
        for copied in self.source_checks:
            if digest(self.output / copied["path"]) != copied["sha256"]:
                raise ValueError("Copied original changed")
        return len(png_paths)

    def contact_sheet(self, path):
        chosen = []
        for pack in self.packs:
            candidates = [entry for entry in self.entries if entry["packId"] == pack and entry["role"] == "character"]
            if pack == "research":
                chosen.extend(candidates)
            elif pack == "bluebird":
                chosen.extend(entry for entry in candidates if entry["animation"] == "cycle")
            elif pack == "kenney":
                chosen.extend(candidates[:6])
            elif pack == "jamie":
                chosen.extend(entry for entry in candidates if entry["animation"] == "cycle" or entry["id"] in {"jamie-ghost-01", "jamie-spider-01"})
            else:
                chosen.extend(candidates[::max(1, len(candidates) // 12)][:12])
        columns, cw, ch = 8, 180, 205
        rows = (len(chosen) + columns - 1) // columns
        sheet = Image.new("RGB", (columns * cw, rows * ch + 84), "#121a28")
        draw = ImageDraw.Draw(sheet)
        font_path = Path("C:/Windows/Fonts/segoeui.ttf")
        font = ImageFont.truetype(str(font_path), 15)
        heading = ImageFont.truetype(str(font_path), 24)
        draw.text((20, 14), "LOPATA · Living Arcade · reviewed asset library", font=heading, fill="white")
        draw.text((20, 48), f"{len(self.entries)} entries · author poses + assembled parts + selected model research", font=font, fill="#b7c9db")
        for index, entry in enumerate(chosen):
            x, y = index % columns * cw, 84 + index // columns * ch
            draw.rounded_rectangle((x + 5, y + 5, x + cw - 5, y + ch - 5), 10, fill="#253247")
            sprite = Image.open(self.output / entry["frames"][0]["path"]).convert("RGBA").resize((155, 155), Image.Resampling.LANCZOS)
            sheet.paste(sprite, (x + 12, y + 7), sprite)
            draw.text((x + 10, y + 163), entry["title"][:23], font=font, fill="white")
            draw.text((x + 10, y + 184), entry["packId"] + (" · cycle" if entry["animation"] == "cycle" else ""), font=font, fill="#b7c9db")
        info = PngImagePlugin.PngInfo()
        info.add_text("LOPATA", json.dumps(dict(application="LOPATA", applicationVersion=VERSION, operation="living_arcade_library_review_sheet", createdUtc=CREATED, note="Artwork authors and model provenance are specified per catalog entry")))
        path.parent.mkdir(parents=True, exist_ok=True)
        sheet.save(path, pnginfo=info)

    def build(self, sheet):
        self.notices()
        for action in (self.glitch, self.jamie, self.kenney, self.bluebird, self.cartoon, self.research_assets):
            before = len(self.entries)
            action()
            self.write_json("render-failures.json", self.failures)
            print(f"{action.__name__}: {len(self.entries) - before} entries", flush=True)
        catalog = dict(schemaVersion=1, version="2026-11-06.1", application="LOPATA", createdUtc=CREATED,
                       packs=list(self.packs.values()), entries=self.entries)
        self.write_json("catalog.json", catalog)
        count = self.validate()
        self.contact_sheet(sheet)
        qa = dict(application="LOPATA", version=VERSION, createdUtc=CREATED, entries=len(self.entries), preparedPngs=count,
                  originalCopiesVerified=len(self.source_checks), packs=dict(Counter(entry["packId"] for entry in self.entries)),
                  roles=dict(Counter(entry["role"] for entry in self.entries)), animations=dict(Counter(entry["animation"] for entry in self.entries)),
                  tags=dict(Counter(tag for entry in self.entries for tag in entry["tags"])), failures=self.failures)
        self.write_json("verification.json", qa)
        (self.output / "README.md").write_text(
            "# LOPATA — Living Arcade asset library\n\n"
            "Prepared PNGs are 256 × 256 transparent RGBA. Originals are byte-for-byte copies; do not overwrite them to add provenance.\n\n"
            "`catalog.json` records source authors, licenses, URLs, source SHA-256 and per-frame SHA-256. `files.json` is the integrity manifest for every bundled file except itself.\n\n"
            "Six source packs are CC0-1.0. Original license files and saved source evidence are in `licenses/`. The Glitch CC0 release excludes its logos and trademarks.\n\n"
            "The `research` pack contains selected original non-franchise Gemma outputs; it is not third-party CC0 art. Actual model revision and purpose are embedded in PNG metadata and catalog records.\n\n"
            "Kenney atomic entries are parts, not full enemies. Twenty additional characters are deterministic assemblies of original parts, with no model authorship claimed.\n\n"
            "Jamie run/climb/rope and Blue Bird jump/walk/eat cycles replay author-drawn poses. Other static art remains static; bob/sway are optional runtime transforms and must not be described as genuine walk cycles.\n\n"
            "The library supplements live generation. No franchise imitations, ripped game assets, scripts, or failed model attempts are used by the runtime. Hidden SVG editor reference layers are removed only from transient developer render copies; original author files remain unchanged.\n\n"
            "Rebuild with the developer tool `Инструменты/LivingArcade/build_asset_library.py`, an existing Pillow environment and the pinned resvg-0.47.0 executable. No Pillow/resvg dependency is added to the game runtime.\n", encoding="utf-8")
        files = [dict(path=self.rel(path), bytes=path.stat().st_size, sha256=digest(path))
                 for path in sorted(self.output.rglob("*")) if path.is_file() and path.name != "files.json"]
        self.write_json("files.json", files)
        print(json.dumps(qa, ensure_ascii=False), flush=True)
        if self.failures:
            raise SystemExit("Some source SVGs failed to render; see verification.json")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sources", type=Path, default=ROOT / "_tmp/arcade-asset-references-20261106/sources")
    parser.add_argument("--research", type=Path, default=ROOT / "_tmp/arcade-visual-research-20261106")
    parser.add_argument("--output", type=Path, default=ROOT / "Исходники/AIHub/Assets/LivingArcade/Library")
    parser.add_argument("--resvg", type=Path, default=ROOT / "Runtime/Svg/resvg-0.47.0/resvg.exe")
    parser.add_argument("--sheet", type=Path, default=ROOT / "_tmp/arcade-library-20261106/library-overview.png")
    args = parser.parse_args()
    if args.output.exists() and any(args.output.iterdir()):
        backup = ROOT / "_backups" / ("living-arcade-library-" + datetime.now().strftime("%Y%m%d-%H%M%S"))
        backup.parent.mkdir(parents=True, exist_ok=True)
        shutil.copytree(args.output, backup)
        print("Existing output backed up before rebuilding: " + str(backup), flush=True)
    Builder(args).build(args.sheet)


if __name__ == "__main__":
    main()
