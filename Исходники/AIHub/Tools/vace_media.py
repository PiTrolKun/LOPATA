"""Bounded video I/O for VACE; no model, scheduler or sampler lives here."""
import hashlib
import json
import math
import os
from pathlib import Path

FPS = 16


def contained(root, relative):
    root = Path(root).resolve()
    path = (root / relative).resolve()
    if path == root or not path.is_relative_to(root):
        raise ValueError("VideoPrep.VaceInvalidResult")
    return path


def file_hash(path):
    with Path(path).open("rb") as source:
        return hashlib.file_digest(source, "sha256").hexdigest()


def file_record(path, output):
    return {"RelativePath": Path(path).relative_to(output).as_posix(),
            "Size": Path(path).stat().st_size, "Sha256": file_hash(path)}


def grid_length(new_frames, prefix, maximum):
    """The context consumes native frames; each extension must still add >=4."""
    return min(maximum, max(prefix + 4 if prefix else 5,
                            int(math.ceil((new_frames + prefix - 1) / 4)) * 4 + 1))


def tail_frames(path, count):
    import av
    from collections import deque
    frames = deque(maxlen=count)
    with av.open(str(path)) as video:
        for frame in video.decode(video=0):
            frames.append(frame.to_ndarray(format="rgb24"))
    if len(frames) < count:
        raise ValueError("VideoPrep.VaceInvalidResult")
    import numpy as np
    result = np.stack(frames)
    return result, hashlib.sha256(result.tobytes()).hexdigest()


def output_dimensions(width, height, output_height):
    if not output_height:
        return width, height
    if output_height not in (720, 1080):
        raise ValueError("VideoPrep.VaceSettingsInvalid")
    if width < height:
        return output_height, round(output_height * height / width / 2) * 2
    return round(output_height * width / height / 2) * 2, output_height


def encode(path, frames, width, height, crf, provenance, operation):
    """The pinned PyAV wheel contains libx264; the shared LGPL ffmpeg need not."""
    import av
    import numpy as np
    path = Path(path)
    temporary = path.with_name(path.name + ".tmp")
    count = 0
    try:
        with av.open(str(temporary), "w", format="mp4", options={"movflags": "+faststart+use_metadata_tags"}) as output:
            output.metadata.update(metadata(provenance() if callable(provenance) else provenance, operation))
            stream = output.add_stream("libx264", rate=FPS)
            stream.width, stream.height, stream.pix_fmt = width, height, "yuv420p"
            stream.options = {"crf": str(crf), "preset": "medium"}
            for pixels in frames:
                if hasattr(pixels, "detach"):
                    pixels = pixels.detach().to("cpu").numpy()
                if pixels.dtype != np.uint8:
                    pixels = np.clip(pixels * 255.0, 0, 255).round().astype(np.uint8)
                frame = av.VideoFrame.from_ndarray(np.ascontiguousarray(pixels[:, :, :3]), format="rgb24")
                if frame.width != width or frame.height != height:
                    frame = frame.reformat(width=width, height=height, interpolation="LANCZOS")
                frame.pts, frame.time_base = count, __import__("fractions").Fraction(1, FPS)
                for packet in stream.encode(frame):
                    output.mux(packet)
                count += 1
            for packet in stream.encode():
                output.mux(packet)
        if not count:
            raise ValueError("VideoPrep.VaceInvalidResult")
        if callable(provenance):
            # PyAV serializes container metadata when encoding starts. Remux
            # after the expensive encode so the public timing includes it.
            stamp(temporary, metadata(provenance(), operation))
        os.replace(temporary, path)
    finally:
        if temporary.exists():
            temporary.unlink()
    return count


def metadata(facts, operation):
    return {"encoded_by": "LOPATA " + facts["ProgramVersion"],
            "LOPATA_VIDEO": json.dumps(facts, ensure_ascii=False), "LOPATA_OPERATION": operation,
            "LOPATA_GENERATION_SECONDS": format(facts["GenerationSeconds"], ".3f")}


def stamp(path, tags):
    import av
    tagged = path.with_name(path.name + ".tagged")
    try:
        with av.open(str(path)) as source, av.open(str(tagged), "w", format="mp4",
                options={"movflags": "+faststart+use_metadata_tags"}) as destination:
            destination.metadata.update(tags)
            streams = {s.index: destination.add_stream_from_template(s) for s in source.streams}
            for packet in source.demux():
                if packet.dts is not None:
                    packet.stream = streams[packet.stream.index]
                    destination.mux(packet)
        os.replace(tagged, path)
    finally:
        if tagged.exists():
            tagged.unlink()


def assemble(path, takes, output, settings, provenance, operation):
    """Retain the original parent and remove ONLY the child's duplicated prefix."""
    import av
    def delivered():
        for take in takes:
            with av.open(str(contained(output, take["Video"]))) as source:
                count = 0
                for index, frame in enumerate(source.decode(video=0)):
                    count += 1
                    if index >= take["PrefixFrames"]:
                        yield frame.to_ndarray(format="rgb24")
                if count != take["Frames"]:
                    raise ValueError("VideoPrep.VaceInvalidResult")
    width, height = output_dimensions(settings["Width"], settings["Height"], settings["OutputHeight"])
    return encode(path, delivered(), width, height, settings["Crf"], provenance, operation)
