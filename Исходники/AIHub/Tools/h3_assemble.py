"""Bounded assembly through native seam resolution and per-clip smart cuts.

H3Assemble also returns an IMAGE tensor for the entire film. Calling it once
for an hour-long project would retain every decoded frame in RAM. Resolve the
native timeline once, smart-cut one original pass at a time, then stream-copy
the resulting compatible MP4 packets with PyAV. No film-sized frame tensor.
"""
import gc
import math
from pathlib import Path
import secrets
from fractions import Fraction


def validate_upscale_budget(settings, items, available):
    # The upstream PyAV fallback gathers the refined film's float RGB frames.
    # Keep this experimental path from attempting a film-sized allocation.
    frames = sum(max(1, round(i["Seconds"] * 24)) for i in items)
    # The last model pass is kept to its natural end, beyond the timing estimate.
    frames += max(124, math.ceil((settings["ClipSeconds"] * 24 - 5) / 17) * 17 + 5)
    pixels = max(settings["Width"] * settings["Height"], settings["UpscaleMegapixels"] * 1024**2)
    if frames * pixels * 12 > available * .5:
        raise ValueError("VideoPrep.H3UpscaleMemory")


def resolved_takes(takes):
    import av
    from lopata_h3_timeline.nodes_assemble import resolve_sequence
    by_path = {t["Video"]: t for t in takes}
    result = []
    for entry in resolve_sequence("\n".join(t["Video"] for t in takes)):
        with av.open(entry["path"]) as video:
            stream = video.streams.video[0]
            count = stream.frames or round(float(stream.duration * stream.time_base) * 24)
        result.append((entry, by_path[entry["clip"]], count if entry["exit"] is None else min(count, entry["exit"])))
    return result


def delivered_by_item(takes):
    result = {}
    for entry, take, hi in resolved_takes(takes):
        result[take["ItemId"]] = result.get(take["ItemId"], 0) + (hi - entry["enter"]) / 24
    return result


def sequence_for(items, takes):
    return [take["Video"] + " @ " + str(lo) + ".." + str(hi)
            for take, lo, hi in planned_spans(items, takes, keep_final_tail=True)]


def refinement_groups(sequence, joint):
    """Refine pixel-linked clips separately, preserving their exact cuts.

    A pixel pin re-encodes its parent's delivered frames: its held latent is
    not the parent's original rows, even if the start happens to be on-grid.
    Moreover arbitrary planned cuts can fall between latent rows; the native
    ownership fallback then keeps the parent's discarded tail over the child.
    Separate these boundaries into native render segments without changing
    the generated clips or their pixel continuity. Latent-only extensions
    keep the native joint-refinement path and all its validation.
    """
    items = joint.sequence_items("\n".join(sequence))
    lines = [line for kind, line in items if kind == "clip"]
    headers = joint.clip_headers([joint.clip_of_line(line) for line in lines])
    groups = []
    for segment in joint.split_segments(items, headers):
        current = []
        for index in segment["clips"]:
            if current:
                pins = joint.mctx.parse_pins(headers[index])
                pixel_link = (len(pins) == 1 and pins[0].get("source_kind") == "clip_pixels"
                              and pins[0].get("place") == "before"
                              and pins[0].get("source_id") in
                              {headers[i].get("self_id") for i in current})
                if pixel_link:
                    groups.append([lines[i] for i in current])
                    current = []
            current.append(index)
        if current:
            groups.append([lines[i] for i in current])
    return groups


def planned_spans(items, takes, keep_final_tail=False):
    """Keep established joins; final assembly may retain the last pass's tail.

    Continuation uses the strict planned cut as its parent endpoint. Extending
    intermediate cuts would show frames the next shot was never conditioned on.
    Only the pass completing the final item has no successor to invalidate.
    """
    remaining = {i["Id"]: max(1, round(i["Seconds"] * 24)) for i in items}
    sequence = []
    for entry, take, hi in resolved_takes(takes):
        size = min(remaining[take["ItemId"]], hi - entry["enter"])
        if size > 0:
            if keep_final_tail and take["ItemId"] == items[-1]["Id"] and size == remaining[take["ItemId"]]:
                size = hi - entry["enter"]
            sequence.append((take, entry["enter"], entry["enter"] + size))
            remaining[take["ItemId"]] -= size
    return sequence


def assemble(native, sequence):
    import av
    import comfy.model_management as memory
    from lopata_h3_timeline.nodes_assemble import resolve_sequence
    native.clip = native.model = native.video_vae = native.audio_vae = None
    memory.unload_all_models()
    gc.collect()
    memory.soft_empty_cache()
    entries = resolve_sequence("\n".join(sequence))
    parts = []
    for index, entry in enumerate(entries):
        native.emit("stage", text="assembling")
        with av.open(entry["path"]) as video:
            stream = video.streams.video[0]
            count = stream.frames or round(float(stream.duration * stream.time_base) * 24)
        hi = count if entry["exit"] is None else min(count, entry["exit"])
        line = entry["clip"] + " @ " + str(entry["enter"]) + ".." + str(hi)
        result = native.classes["H3Assemble"]().assemble(line, "assembly/part_" + str(index), native.settings["Crf"])
        part = Path(result[0])
        parts.append(part if part.is_absolute() else Path(native.request["Output"]) / part)
        del result
        gc.collect()
    target = Path(native.request["Output"]) / ("assembled_" + secrets.token_hex(12) + ".mp4")
    with av.open(str(parts[0])) as first, av.open(str(target), "w", options={"movflags":"+faststart"}) as output:
        streams = {s.type: output.add_stream_from_template(s) for s in first.streams if s.type in ("video", "audio")}
        last_dts, offset = {}, Fraction(0)
        for path in parts:
            with av.open(str(path)) as source:
                video = source.streams.video[0]
                duration = Fraction(video.frames, 24) if video.frames else video.duration * video.time_base
                for packet in source.demux():
                    kind = packet.stream.type
                    if kind not in streams or packet.dts is None or packet.pts is None:
                        continue
                    base = packet.time_base
                    # AAC encoder priming/padding belongs to each source clip;
                    # it must not overlap the next clip's presented samples.
                    if kind == "audio" and (packet.pts < 0 or packet.pts * base >= duration):
                        continue
                    packet.pts += round(offset / base)
                    packet.dts += round(offset / base)
                    instant = packet.dts * base
                    if kind in last_dts and instant <= last_dts[kind]:
                        continue
                    last_dts[kind] = instant
                    packet.stream = streams[kind]
                    output.mux(packet)
                offset += duration
    return str(target)


def output_dimensions(aspect, height):
    if height not in (720, 1080, 2160):
        raise ValueError("VideoPrep.H3SettingsInvalid")
    a, b = (int(v) for v in aspect.split(":"))
    # Height is the usual landscape preset name; portrait swaps the axes.
    if a < b:
        return height, round(height * b / a / 2) * 2
    return round(height * a / b / 2) * 2, height


def scale_output(native, source, height):
    """Bounded ordinary scaling. It creates no new generative detail."""
    import av
    import numpy as np
    width, height = output_dimensions(native.settings["Aspect"], height)
    target = Path(native.request["Output"]) / ("scaled_" + secrets.token_hex(12) + ".mp4")
    native.emit("stage", text="scaling-output")
    with av.open(str(source)) as video, av.open(str(target), "w", options={"movflags": "+faststart"}) as output:
        stream = video.streams.video[0]
        encoder = output.add_stream("libx264", rate=24, options={"crf": str(native.settings["Crf"])})
        encoder.width, encoder.height, encoder.pix_fmt = width, height, "yuv420p"
        ratio = min(width / stream.width, height / stream.height)
        fitted_w = min(width, max(2, round(stream.width * ratio / 2) * 2))
        fitted_h = min(height, max(2, round(stream.height * ratio / 2) * 2))
        left, top = (width - fitted_w) // 2, (height - fitted_h) // 2
        total = stream.frames or 0
        for index, frame in enumerate(video.decode(stream)):
            picture = frame.reformat(fitted_w, fitted_h, format="rgb24", interpolation="LANCZOS").to_ndarray()
            canvas = np.zeros((height, width, 3), dtype=np.uint8)
            canvas[top:top + fitted_h, left:left + fitted_w] = picture
            fitted = av.VideoFrame.from_ndarray(canvas, format="rgb24")
            fitted.pts, fitted.time_base = index, Fraction(1, 24)
            for packet in encoder.encode(fitted):
                output.mux(packet)
            if index % 24 == 0:
                native.emit("progress", value=index + 1, total=total)
        for packet in encoder.encode():
            output.mux(packet)
    return target
