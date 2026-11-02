"""Join accepted silent H3 MP4 shots with the already installed PyAV runtime."""
from fractions import Fraction


def join_video(parts, target, fps, width, height):
    """Remux every frame in order; no concat demuxer or additional FFmpeg build."""
    import av

    if not parts:
        raise ValueError("Montage assembly requires accepted shots.")
    offset, last_dts, template = Fraction(0), None, None
    with av.open(str(target), "w", format="mp4", options={"movflags": "+faststart"}) as output:
        destination = None
        for path, expected_frames in parts:
            with av.open(str(path)) as source:
                if len(source.streams.video) != 1 or source.streams.audio:
                    raise ValueError("A montage shot must contain one silent video stream.")
                stream = source.streams.video[0]
                signature = (stream.codec_context.name, stream.width, stream.height,
                             stream.codec_context.extradata)
                if (stream.width != width or stream.height != height or stream.average_rate != fps
                        or stream.frames != expected_frames or expected_frames <= 0
                        or (template is not None and signature != template)):
                    raise ValueError("Montage shot encoding does not match the accepted frame sequence.")
                if destination is None:
                    template = signature
                    destination = output.add_stream_from_template(stream)
                presented = []
                for packet in source.demux(stream):
                    if packet.dts is None or packet.pts is None:
                        continue
                    base = packet.time_base
                    presented.append(packet.pts * base * fps)
                    shift = offset / base
                    if shift.denominator != 1:
                        raise ValueError("Montage shot timestamps do not share an exact frame grid.")
                    packet.pts += int(shift)
                    packet.dts += int(shift)
                    instant = packet.dts * base
                    if last_dts is not None and instant <= last_dts:
                        raise ValueError("Montage decoding timestamps overlap.")
                    last_dts = instant
                    packet.stream = destination
                    output.mux(packet)
                if sorted(presented) != list(range(expected_frames)):
                    raise ValueError("Montage shot has missing, duplicate or displaced frames.")
                offset += Fraction(expected_frames, fps)
    return offset


def scale_video(source, target, fps, width, height, quality):
    """Match the connected H3 renderer: PyAV scales/encodes, FFmpeg only muxes."""
    import av

    with av.open(str(source)) as video, av.open(str(target), "w", format="mp4") as output:
        stream = output.add_stream("libx264", rate=fps)
        stream.width, stream.height, stream.pix_fmt = width, height, "yuv420p"
        stream.time_base = stream.codec_context.time_base = Fraction(1, fps)
        stream.options = {"crf": str(max(10, min(35, round(36 - quality / 5)))), "preset": "medium"}
        count = 0
        for frame in video.decode(video=0):
            frame = frame.reformat(width, height, format="yuv420p", interpolation="LANCZOS")
            frame.pts, frame.time_base = count, Fraction(1, fps)
            for packet in stream.encode(frame):
                output.mux(packet)
            count += 1
        if count != video.streams.video[0].frames:
            raise ValueError("Montage scaling did not preserve all accepted frames.")
        for packet in stream.encode():
            output.mux(packet)
