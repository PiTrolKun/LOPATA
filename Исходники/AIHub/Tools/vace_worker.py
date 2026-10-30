"""Durable complete-pass checkpoints for a native VACE firstclip chain."""
import faulthandler
import json
import logging
import math
from pathlib import Path
import secrets
import sys
import time
import traceback
from datetime import datetime, timezone


def emit(kind, **data):
    print("@vace " + json.dumps({"kind": kind, **data}, ensure_ascii=False), flush=True)


def coverage(takes):
    result = {}
    for take in takes:
        result[take["ItemId"]] = result.get(take["ItemId"], 0) + take["Frames"] - take["PrefixFrames"]
    return result


def validate_takes(takes, items, settings):
    previous = ""
    seen = set()
    for take in takes:
        if (take["Id"] in seen or take["ItemId"] not in {i["Id"] for i in items}
                or take["Parent"] != previous or take["Frames"] < 5 or take["Frames"] > settings["Frames"]
                or (take["Frames"] - 1) % 4 or take["PrefixFrames"] < 0
                or take["PrefixFrames"] >= take["Frames"]
                or (previous == "" and take["PrefixFrames"] != 0)
                or (previous != "" and (take["PrefixFrames"] < 5 or (take["PrefixFrames"] - 1) % 4))):
            raise ValueError("VideoPrep.VaceInvalidResult")
        previous = take["Id"]
        seen.add(previous)


def main():
    started = time.monotonic()
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from vace_media import FPS, contained, file_hash, file_record, grid_length, tail_frames, encode, assemble
    from vace_native import NativeVace
    native = NativeVace(request, emit)
    if request.get("Probe"):
        emit("probe", ok=True)
        return
    settings, items = request["Settings"], request["Items"]
    takes = list(request.get("Takes", []))
    validate_takes(takes, items, settings)
    output = Path(request["Output"]).resolve()
    def facts():
        return {**request["Provenance"], "GenerationSeconds": request["ElapsedBeforeAttempt"] + time.monotonic() - started,
                "CreatedUtc": datetime.now(timezone.utc).isoformat()}
    for index, item in enumerate(items):
        desired = math.ceil(item["Seconds"] * FPS)
        done = coverage(takes).get(item["Id"], 0)
        while done < desired:
            parent = takes[-1] if takes else None
            prefix = min(settings["ContextFrames"], parent["Frames"]) if parent else 0
            prefix_pixels, prefix_sha = None, ""
            if parent:
                prefix_pixels, prefix_sha = tail_frames(contained(output, parent["Video"]), prefix)
            length = grid_length(desired - done, prefix, settings["Frames"])
            unit = dict(item)
            unit["Seed"] = secrets.randbits(63) if item["Seed"] < 0 else (item["Seed"] + len(takes)) % (2**63)
            count = len(takes) + 1
            remaining = sum(max(0, math.ceil(i["Seconds"] * FPS) - coverage(takes).get(i["Id"], 0)) for i in items[index:])
            total = len(takes) + math.ceil(remaining / max(4, settings["Frames"] - settings["ContextFrames"]))
            emit("pass", index=count, total=max(count, total), item=item["Id"], item_index=index + 1, item_total=len(items), extending=parent is not None)
            pixels = native.generate(unit, length, prefix_pixels)
            take_id = secrets.token_hex(16)
            path = output / ("take_" + take_id + ".mp4")
            frames = encode(path, pixels, settings["Width"], settings["Height"], settings["Crf"], facts, request["Id"])
            del pixels
            take = {"Id": take_id, "ItemId": item["Id"], "Video": path.name, "Seed": unit["Seed"], "Frames": frames,
                    "PrefixFrames": prefix, "Parent": parent["Id"] if parent else ""}
            lineage = path.with_suffix(".lineage.json")
            lineage.write_text(json.dumps({"Schema": 1, "Run": request["Id"], "InputHash": request["InputHash"],
                "Take": take, "ParentVideoSha256": file_hash(contained(output, parent["Video"])) if parent else "",
                "PrefixRgbSha256": prefix_sha, "ContextMode": "native-vace-firstclip-mask", "Fps": FPS}, ensure_ascii=False), encoding="utf-8")
            takes.append(take)
            emit("take", take=take, files=[file_record(path, output), file_record(lineage, output)])
            done = coverage(takes).get(item["Id"], 0)
    native.unload()
    emit("stage", text="assembling")
    path = output / ("whole_" + secrets.token_hex(16) + ".mp4")
    frames = assemble(path, takes, output, settings, facts, request["Id"])
    emit("whole", video=path.name, frames=frames, seconds=frames / FPS,
         generation_seconds=request["ElapsedBeforeAttempt"] + time.monotonic() - started, files=[file_record(path, output)])
    emit("done", complete=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit("error", error=str(error), type=type(error).__name__)
        sys.exit(1)
