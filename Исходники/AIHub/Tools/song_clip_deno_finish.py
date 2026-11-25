"""Denoise the accepted montage, optionally upscale to UHD, and copy its audio."""

from contextlib import ExitStack
from fractions import Fraction
import json
import logging
import math
from pathlib import Path
import subprocess
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_montage import contained, emit, file_hash


def finish(request):
    config = request["deno"]
    job = Path(request["job"]).resolve()
    receipt = json.loads(contained(job, "worker-result.json").read_text(encoding="utf-8"))
    source = Path(receipt["output"]).resolve()
    source = contained(job, source.relative_to(job))
    if file_hash(source) != receipt["outputSha256"]:
        raise ValueError("The accepted montage changed before RTX finishing.")
    lossless = request.get("lossless", False)
    suffix = "mkv" if lossless else "mp4"
    output = contained(job, f"deno-finished.{suffix}")
    partial = contained(job, f"deno-finish.partial.{suffix}")
    width, height = request["outputWidth"], request["outputHeight"]
    target_width, target_height = config["width"], config["height"]
    prepared = request.get("preparedSource", False)
    valid_size = (width, height) in ((1920, 1080), (1080, 1920))
    if prepared:
        valid_size = min(width, height) >= 288 and max(width, height) <= 1920 and width % 2 == height % 2 == 0
    if not valid_size or (target_width, target_height) not in ((width, height), (2 * width, 2 * height)):
        raise ValueError("Unsupported Deno RTX output dimensions.")
    audio = request.get("audio", True)
    probe = subprocess.check_output([
        request["ffprobe"], "-v", "error", "-select_streams", "v:0", "-show_streams", "-of", "json", str(source)
    ])
    stream = json.loads(probe)["streams"][0]
    rate = Fraction(request["rate"] if lossless else "24/1")
    if (stream["width"], stream["height"]) != (width, height) or Fraction(stream["avg_frame_rate"]) != rate:
        raise ValueError("The accepted video must have the configured dimensions and frame rate.")

    sys.path.insert(0, config["vfx"])
    import nvvfx
    import torch

    device = torch.device(request["device"])
    if device.type != "cuda" or not torch.version.cuda or torch.cuda.get_device_capability(device) < (7, 5):
        raise RuntimeError("SongClip.DenoGpuRequired")
    index = device.index or 0
    frame_bytes = width * height * 3
    expected_frames = int(receipt["frames"])
    if expected_frames <= 0 or (not lossless and expected_frames != math.ceil(receipt["duration"] * 24)):
        raise ValueError("The accepted video has an invalid frame count.")
    decode = [
        request["ffmpeg"], "-nostdin", "-v", "error", "-i", str(source), "-map", "0:v:0",
        "-pix_fmt", "rgb24", "-f", "rawvideo", "pipe:1"
    ]
    encode = [
        request["ffmpeg"], "-nostdin", "-v", "error", "-y", "-f", "rawvideo",
        "-pix_fmt", "rgb24", "-s", f"{target_width}x{target_height}", "-r", str(rate),
        "-i", "pipe:0",
        *(["-i", str(source), "-map", "0:v:0", "-map", "1:a:0"] if audio else ["-map", "0:v:0", "-an"]),
    ]
    if lossless:
        encode += ["-c:v", "ffv1", "-level", "3", "-pix_fmt", "bgr0"]
    else:
        encode += [
            "-c:v", "h264_nvenc", "-gpu", str(index), "-preset", "p4", "-rc", "vbr",
            "-cq", str(max(1, min(51, round(51 - request["quality"] * .41)))), "-b:v", "0", "-pix_fmt", "yuv420p",
            "-vf", "scale=in_range=pc:out_range=tv:out_color_matrix=bt709",
            "-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709",
            "-movflags", "+faststart",
        ]
    encode += [*(["-c:a", "copy"] if audio else []), str(partial)]

    def effect(stack, quality, output_width, output_height):
        value = stack.enter_context(nvvfx.VideoSuperRes(quality=quality, device=index))
        value.output_width, value.output_height = output_width, output_height
        value.load()
        return value

    try:
        with torch.cuda.device(device), ExitStack() as stack:
            level = nvvfx.VideoSuperRes.QualityLevel
            denoise = effect(stack, level.DENOISE_LOW, width, height)
            upscale = effect(stack, level.HIGHBITRATE_HIGH, target_width, target_height) if width != target_width else None
            emit("stage", stage="deno-finish", fraction=0, message="Deno RTX")
            reader = stack.enter_context(subprocess.Popen(decode, stdout=subprocess.PIPE))
            writer = stack.enter_context(subprocess.Popen(encode, stdin=subprocess.PIPE))
            frames = 0
            try:
                while raw := reader.stdout.read(frame_bytes):
                    if len(raw) != frame_bytes or frames >= expected_frames:
                        raise ValueError("Unexpected frame from the montage decoder.")
                    frame = (torch.frombuffer(bytearray(raw), dtype=torch.uint8)
                             .reshape(height, width, 3).permute(2, 0, 1)
                             .to(device=device, dtype=torch.float32).div_(255).contiguous())
                    clean = torch.from_dlpack(denoise.run(frame).image).clone()
                    torch.cuda.synchronize(device)
                    if upscale is not None:
                        clean = torch.from_dlpack(upscale.run(clean).image).clone()
                        torch.cuda.synchronize(device)
                    rgb = clean.permute(1, 2, 0).mul(255).round().clamp(0, 255).to(torch.uint8).cpu()
                    writer.stdin.write(rgb.contiguous().numpy().tobytes())
                    frames += 1
                    if frames % 24 == 0:
                        emit("deno-finish-progress", index=frames, total=expected_frames, fraction=frames / expected_frames)
                writer.stdin.close()
                reader_code, writer_code = reader.wait(), writer.wait()
                if reader_code or writer_code or frames != expected_frames:
                    raise RuntimeError(f"RTX media pipeline failed ({reader_code}, {writer_code}, {frames}/{expected_frames}).")
            finally:
                for process in (reader, writer):
                    if process.poll() is None:
                        process.kill()
        partial.replace(output)
        emit("deno-finish-complete", fraction=1, frames=frames, width=target_width, height=target_height)
    finally:
        partial.unlink(missing_ok=True)


if __name__ == "__main__":
    try:
        finish(json.loads(Path(sys.argv[-1]).read_text(encoding="utf-8-sig")))
    except Exception as error:
        emit("error", fraction=0, message=str(error), type=type(error).__name__)
        logging.exception("Deno RTX finishing failed")
        sys.exit(1)
