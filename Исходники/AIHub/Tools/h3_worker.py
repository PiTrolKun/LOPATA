"""JSON-lines worker for the pinned H3 native node adapter. Outputs commit after native sidecars exist."""
import hashlib
import json
import math
import os
from pathlib import Path
import secrets
import sys
import traceback
import faulthandler
import logging
import time
from datetime import datetime, timezone


def emit(kind, **data):
    print("@h3 " + json.dumps({"kind": kind, **data}, ensure_ascii=False), flush=True)


def contained(root, relative):
    root = Path(root).resolve()
    path = (root / relative).resolve()
    if not path.is_relative_to(root) or path == root:
        raise ValueError("H3 output escapes the operation directory")
    return path


def probe_video(path):
    import av
    with av.open(str(path)) as video:
        stream = video.streams.video[0]
        seconds = float(stream.duration * stream.time_base) if stream.duration else float(video.duration or 0) / av.time_base
        tags = {k.lower(): v for k, v in video.metadata.items()}
        if seconds <= 0 or "lopata_video" not in tags:
            raise ValueError("Generated video has no duration or LOPATA provenance")
        return seconds


def files_for(path, output):
    from lopata_h3_timeline import mctx
    sidecar = Path(mctx.sidecar_path(str(path)))
    if not sidecar.is_file():
        raise ValueError("Native H3 motion context was not committed")
    # Native names are preserved when files are put back from a portable project.
    prefix = path.stem
    files = sorted(p for p in path.parent.glob(prefix + "*") if p.is_file() and not p.name.endswith(".tmp"))
    return [{"RelativePath": p.relative_to(output).as_posix(), "Size": p.stat().st_size,
             "Sha256": file_hash(p)} for p in files]


def file_hash(path):
    with path.open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def main():
    started = time.monotonic()
    started_wall = datetime.now(timezone.utc)
    logging.basicConfig(level=logging.INFO, stream=sys.stderr,
                        format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    request = json.loads(Path(sys.argv[1]).read_text(encoding="utf-8-sig"))
    request["_AttemptStartedMonotonic"] = started
    if request.get("ElapsedBeforeAttempt") is not None and request.get("WorkStartedAtUtc"):
        launched = datetime.fromisoformat(request["WorkStartedAtUtc"].replace("Z", "+00:00"))
        request["ElapsedBeforeAttempt"] += max(0.0, (started_wall - launched).total_seconds())
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    from h3_native import NativeH3
    native = NativeH3(request, emit)
    from h3_assemble import delivered_by_item, sequence_for, planned_spans
    if request.get("Probe"):
        emit("probe", ok=True)
        return
    s = request["Settings"]
    if s["Upscale"]:
        import psutil
        from h3_assemble import validate_upscale_budget
        validate_upscale_budget(s, request["Items"], psutil.virtual_memory().available)
    if s["Continuation"] not in ("auto", "new", "extend"):
        raise ValueError("VideoPrep.H3OperationUnavailable")
    if any(item.get("Audio") or item.get("Video") for item in request["Items"]):
        raise ValueError("VideoPrep.H3MediaUnavailable")
    output = Path(request["Output"]).resolve()
    takes = list(request.get("Takes", []))
    if request.get("FinalizeOnly"):
        coverage = delivered_by_item(takes) if takes else {}
        if any(round(coverage.get(item["Id"], 0) * 24) < round(item["Seconds"] * 24)
               for item in request["Items"]):
            # Recovery may refine/assemble verified completed passes only.
            # Never silently regenerate hours of base sampling on this path.
            raise ValueError("VideoPrep.H3ChangedProject")
    for item_index, item in enumerate(request["Items"]):
        done = delivered_by_item(takes).get(item["Id"], 0) if takes else 0
        while done + 1 / 24 < item["Seconds"]:
            entry = dict(item)
            entry["Id"] = secrets.token_hex(12)
            entry["Seed"] = secrets.randbits(53) if item["Seed"] < 0 else (item["Seed"] + len(takes)) % (2**53)
            last = takes[-1] if takes else None
            operation = item.get("Operation", s["Continuation"])
            extend = last is not None and operation in ("extend", "auto")
            parent = last["Video"] if extend else None
            if extend and last["ItemId"] != item["Id"]:
                # The preceding plan may use only 4s of a 10s raw pass.
                # Continue at its actual presented end, never at the unused tail.
                span = planned_spans(request["Items"], takes)[-1]
                parent = span[0]["Video"]
                entry["_ParentEndFrame"] = span[2]
            coverage = delivered_by_item(takes) if takes else {}
            capacity = max(1 / 24, s["ClipSeconds"] - s["ContextFrames"] / 24)
            left = sum(math.ceil(max(0, i["Seconds"] - coverage.get(i["Id"], 0) - 1 / 24) / capacity)
                       for i in request["Items"][item_index:])
            emit("pass", index=len(takes) + 1, total=len(takes) + left, item=item["Id"],
                 item_index=item_index + 1, item_total=len(request["Items"]), extending=extend)
            relative = native.generate(entry, parent)
            path = contained(output, relative)
            seconds = probe_video(path)
            take = {"Id": entry["Id"], "ItemId": item["Id"], "SceneId": item["SceneId"],
                    "Video": path.relative_to(output).as_posix(), "Seed": entry["Seed"], "Seconds": seconds,
                    "Parent": parent or ""}
            files = files_for(path, output)
            takes.append(take)
            emit("take", take=take, files=files)
            coverage = delivered_by_item(takes)
            done = coverage.get(item["Id"], 0)
            if s["RunMode"] == "review":
                complete = all(coverage.get(i["Id"], 0) + 1 / 24 >= i["Seconds"] for i in request["Items"])
                emit("done", complete=complete)
                return
    if takes:
        emit("stage", text="assembling")
        before = {p.resolve() for p in output.rglob("*") if p.is_file()}
        sequence = sequence_for(request["Items"], takes)
        relative = native.upscale(sequence, takes[-1]["Seed"]) if s["Upscale"] else native.assemble(sequence)
        path = contained(output, relative)
        # Assembly copies native packets; explicitly add container provenance to the derived film.
        import subprocess
        temporary = output / ("final_" + secrets.token_hex(12) + ".mp4")
        scaled = None
        if s.get("OutputHeight", 0):
            from h3_assemble import scale_output
            scaled = scale_output(native, path, s["OutputHeight"])
        before_attempt = request.get("ElapsedBeforeAttempt")
        generation_seconds = None if before_attempt is None else before_attempt + time.monotonic() - started
        facts = request["UpscaleProvenance"] if s["Upscale"] else request["Provenance"]
        provenance = {**facts, "GenerationSeconds": generation_seconds}
        command = [request["Ffmpeg"], "-nostdin", "-v", "error", "-i", str(scaled or path)]
        if scaled:
            command += ["-i", str(path), "-map", "0:v:0", "-map", "1:a:0?"]
        else:
            command += ["-map", "0"]
        command += [
            "-c", "copy", "-metadata", "encoded_by=LOPATA " + request["Provenance"]["ProgramVersion"],
            "-metadata", "LOPATA_VIDEO=" + json.dumps(provenance, ensure_ascii=False),
            "-metadata", "LOPATA_OPERATION=" + request["Id"], "-movflags", "+faststart+use_metadata_tags"]
        if generation_seconds is not None:
            command += ["-metadata", "LOPATA_GENERATION_SECONDS=" + format(generation_seconds, ".3f")]
        if s["AudioMode"] == "mute": command.append("-an")
        command.append(str(temporary))
        subprocess.run(command, check=True)
        seconds = probe_video(temporary)
        generated = ([p for p in output.rglob("*") if p.is_file() and p.resolve() not in before and not p.name.endswith(".tmp")]
                     if s["Upscale"] else [temporary])
        emit("whole", video=temporary.relative_to(output).as_posix(), seconds=seconds, generation_seconds=generation_seconds,
             files=[{"RelativePath": p.relative_to(output).as_posix(), "Size": p.stat().st_size, "Sha256": file_hash(p)} for p in generated])
    emit("done", complete=True)


if __name__ == "__main__":
    try:
        main()
    except Exception as error:
        traceback.print_exc(file=sys.stderr)
        emit("error", error=str(error), type=type(error).__name__)
        sys.exit(1)
