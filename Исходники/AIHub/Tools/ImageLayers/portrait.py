import json
import sys
from pathlib import Path

import cv2
import numpy as np


def read_image(path):
    # imdecode also accepts file names outside the Windows ANSI code page.
    image = cv2.imdecode(np.frombuffer(path.read_bytes(), dtype=np.uint8), cv2.IMREAD_UNCHANGED)
    if image is None or image.ndim != 3 or image.shape[2] not in (3, 4):
        raise ValueError("Expected an RGB or RGBA source")
    alpha = image[:, :, 3] if image.shape[2] == 4 else None
    colors = cv2.cvtColor(image, cv2.COLOR_BGRA2RGB if alpha is not None else cv2.COLOR_BGR2RGB)
    if alpha is None or np.all(alpha == 255):
        return colors, None
    return colors, alpha


def on_white(rgb, alpha):
    if alpha is None:
        return rgb
    opacity = alpha[:, :, None].astype(np.uint16)
    return ((rgb.astype(np.uint16) * opacity + 255 * (255 - opacity) + 127) // 255).astype(np.uint8)


def parts(folder, weights):
    import mediapipe as mp

    options = mp.tasks.vision.ImageSegmenterOptions(
        base_options=mp.tasks.BaseOptions(model_asset_path=str(weights)),
        output_category_mask=True, output_confidence_masks=False,
    )
    paths = json.loads((folder / "crops.json").read_text(encoding="utf-8"))
    with mp.tasks.vision.ImageSegmenter.create_from_options(options) as segmenter:
        for index, file in enumerate(paths):
            rgb, alpha = read_image(folder / file)
            inputs = mp.Image(image_format=mp.ImageFormat.SRGB, data=on_white(rgb, alpha))
            labels = segmenter.segment(inputs).category_mask.numpy_view()
            if labels.ndim == 3 and labels.shape[2] == 1:
                labels = labels[:, :, 0]
            if labels.shape != rgb.shape[:2] or labels.max() > 5:
                raise ValueError("Unexpected MediaPipe category mask")
            (folder / f"categories-{index}.bin").write_bytes(labels.astype(np.uint8, copy=False).tobytes())


def encode_cutout(folder, rgb, alpha, source_alpha):
    if source_alpha is not None:
        alpha = ((alpha.astype(np.uint16) * source_alpha + 127) // 255).astype(np.uint8)
    bgra = np.dstack((rgb[:, :, ::-1], alpha))
    ok, encoded = cv2.imencode(".png", bgra)
    if not ok:
        raise ValueError("Could not encode cutout")
    (folder / "cutout.png").write_bytes(encoded.tobytes())
    return alpha


def trimap_regions(hard):
    offsets = np.arange(-4, 5)
    disk = (offsets[:, None] ** 2 + offsets[None, :] ** 2 <= 16).astype(np.uint8)
    foreground = cv2.erode(hard, disk, borderType=cv2.BORDER_CONSTANT, borderValue=0) > 0
    background = cv2.dilate(hard, disk) == 0
    return foreground, background


def matting(folder, rgb, source_alpha):
    from pymatting import estimate_alpha_cf, estimate_foreground_ml

    pixels = rgb.shape[0] * rgb.shape[1]
    values = np.frombuffer((folder / "input.bits").read_bytes(), dtype=np.uint8)
    hard = np.unpackbits(values, count=pixels, bitorder="little").reshape(rgb.shape[:2])
    foreground, background = trimap_regions(hard)
    if not foreground.any() or not background.any():
        return
    trimap = np.full(hard.shape, .5, dtype=np.float64)
    trimap[foreground] = 1
    trimap[background] = 0
    # Work in overlapping neighborhoods on large originals so memory follows
    # the size of the area being solved. Only the central area of a tile is kept.
    height, width = hard.shape
    edge_rgb = rgb.copy()
    opacity = hard * 255
    tile = 640 if pixels > 1_000_000 else max(height, width)
    for y in range(0, height, tile):
        for x in range(0, width, tile):
            bottom, right = min(y + tile, height), min(x + tile, width)
            if not (trimap[y:bottom, x:right] == .5).any():
                continue
            t, l = max(y - 32, 0), max(x - 32, 0)
            b, r = min(bottom + 32, height), min(right + 32, width)
            guide = trimap[t:b, l:r]
            if not (guide == 1).any() or not (guide == 0).any():
                continue
            patch = rgb[t:b, l:r].astype(np.float64) / 255
            alpha = estimate_alpha_cf(patch, guide)
            front = estimate_foreground_ml(patch, alpha)
            region = (slice(y - t, bottom - t), slice(x - l, right - l))
            opacity[y:bottom, x:right] = np.rint(np.clip(alpha[region], 0, 1) * 255).astype(np.uint8)
            undecided = trimap[y:bottom, x:right] == .5
            edge_rgb[y:bottom, x:right][undecided] = np.rint(np.clip(front[region][undecided], 0, 1) * 255).astype(np.uint8)
    encode_cutout(folder, edge_rgb, opacity, source_alpha)


def portrait(folder, rgb, source_alpha, weights):
    import onnxruntime as ort

    height, width = rgb.shape[:2]
    if max(height, width) < 512 or min(height, width) > 512:
        scale = 512 / min(height, width)
    else:
        scale = 1
    scale = min(scale, 1536 / max(height, width))
    dims = tuple(max(32, int(dim * scale) // 32 * 32) for dim in (width, height))
    normalized = (on_white(rgb, source_alpha).astype(np.float32) - 127.5) / 127.5
    model_image = cv2.resize(normalized, dims, interpolation=cv2.INTER_AREA).transpose(2, 0, 1)[None]
    opts = ort.SessionOptions()
    opts.intra_op_num_threads = 6
    opts.inter_op_num_threads = 1
    session = ort.InferenceSession(str(weights), sess_options=opts, providers=["CPUExecutionProvider"])
    result, = session.run(None, {session.get_inputs()[0].name: np.ascontiguousarray(model_image)})
    alpha = cv2.resize(result.squeeze(), (width, height), interpolation=cv2.INTER_AREA)
    if not np.isfinite(alpha).all():
        raise ValueError("MODNet returned an invalid matte")
    alpha = encode_cutout(folder, rgb, np.rint(np.clip(alpha, 0, 1) * 255).astype(np.uint8), source_alpha)
    pack_mask(folder, alpha >= 128)


def pack_mask(folder, mask):
    (folder / "output-0.bits").write_bytes(np.packbits(mask, bitorder="little").tobytes())


def watershed(folder, rgb, source_alpha):
    guide = json.loads((folder / "markers.json").read_text(encoding="utf-8"))
    left, top, right, bottom = guide["area"]
    marks = np.ones(rgb.shape[:2], dtype=np.int32)
    region = (slice(top, bottom), slice(left, right))
    marks[region] = 0
    for x, y, foreground in guide["points"]:
        cv2.circle(marks, (x, y), 5, 2 if foreground else 1, -1)
    mask = np.zeros(marks.shape, dtype=bool)
    if (marks == 2).any() and (marks == 1).any():
        cv2.watershed(cv2.cvtColor(on_white(rgb, source_alpha), cv2.COLOR_RGB2BGR), marks)
        mask[region] = marks[region] == 2
        if source_alpha is not None:
            mask &= source_alpha > 0
    pack_mask(folder, mask)


def main():
    action, workspace, *assets = sys.argv[1:]
    folder = Path(workspace)
    cv2.setNumThreads(4)
    if action == "parts":
        parts(folder, Path(assets[0]))
        return
    rgb, alpha = read_image(folder / "source.png")
    if action == "matte":
        matting(folder, rgb, alpha)
    elif action == "edge-details":
        from runpy import run_path

        refine = run_path(str(folder / "edge_details.py"))["refine"]
        refine(folder, rgb, alpha)
    elif action == "portrait":
        portrait(folder, rgb, alpha, Path(assets[0]))
    elif action == "watershed":
        watershed(folder, rgb, alpha)
    else:
        raise ValueError("Unknown portrait worker operation")


if __name__ == "__main__":
    main()
