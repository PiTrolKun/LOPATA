"""Raster stages for the layer workspace. Inputs are pixels and validated model coordinates."""

import json
import math
import sys
from pathlib import Path

import cv2
import numpy as np


SEED = 20261121
MAX_SIDE = 1536


def polygon(points, width, height):
    coordinates = np.asarray(points, dtype=np.float64)
    return np.rint(coordinates * [(width - 1) / 1000, (height - 1) / 1000]).astype(np.int32)


def close_gap(rgb, vertices):
    """Use a local search to join open ends, avoiding unrelated stronger edges farther away."""
    start, end = vertices[-1], vertices[0]
    distance = np.linalg.norm(start - end)
    if distance <= 2:
        return vertices
    height, width = rgb.shape[:2]
    margin = min(64, max(4, round(distance * .15)))
    x0, y0 = np.maximum(0, np.minimum(start, end) - margin)
    x1, y1 = np.minimum([width, height], np.maximum(start, end) + margin + 1)
    left_top = np.array([x0, y0])
    scissors = cv2.segmentation_IntelligentScissorsMB()
    scissors.setEdgeFeatureCannyParameters(45, 120)
    scissors.applyImage(np.ascontiguousarray(rgb[y0:y1, x0:x1]))
    scissors.buildMap(tuple(int(value) for value in start - left_top))
    bridge = scissors.getContour(tuple(int(value) for value in end - left_top)).reshape(-1, 2) + left_top
    if len(bridge) < 2 or len(bridge) > distance * 2 + 2:
        return vertices
    return np.concatenate((vertices, bridge[1:-1]))


def outline(rgb, shapes):
    height, width = rgb.shape[:2]
    initial = np.zeros((height, width), np.uint8)
    for shape in shapes:
        outer = polygon(shape["outer"], width, height)
        if shape.get("open", False):
            outer = close_gap(rgb, outer)
        component = cv2.fillPoly(np.zeros_like(initial), [outer], 1)
        for hole in shape["holes"]:
            cv2.fillPoly(component, [polygon(hole, width, height)], 0)
        initial |= component
    return initial != 0


def fit(rgb, initial):
    if np.count_nonzero(initial) < 5 or np.count_nonzero(~initial) < 5:
        return initial
    distance = cv2.DIST_MASK_PRECISE
    inner = cv2.distanceTransform(np.pad(initial.astype(np.uint8), 1), cv2.DIST_L2, distance)[1:-1, 1:-1]
    outer = cv2.distanceTransform((~initial).astype(np.uint8), cv2.DIST_L2, distance)
    band = max(1, round(min(initial.shape) * .08))
    state = np.where(initial, cv2.GC_PR_FGD, cv2.GC_PR_BGD).astype(np.uint8)
    state[initial & (inner >= min(band, .8 * inner.max()))] = cv2.GC_FGD
    state[(~initial) & (outer > band)] = cv2.GC_BGD
    return graph_cut(rgb, state)


def graph_cut(rgb, state):
    cv2.setRNGSeed(SEED)
    cv2.grabCut(np.ascontiguousarray(rgb), state, None,
                np.zeros((1, 65), np.float64), np.zeros((1, 65), np.float64), 5, cv2.GC_INIT_WITH_MASK)
    return (state & 1) != 0


def signed(mask):
    if not mask.any() or mask.all():
        distance = math.hypot(*mask.shape) * (1 if mask.all() else -1)
        return np.full(mask.shape, distance, np.float64)
    def distance(coverage):
        result = cv2.distanceTransform(coverage.astype(np.uint8), cv2.DIST_L2, cv2.DIST_MASK_PRECISE)
        return np.sqrt(np.rint(np.square(result.astype(np.float64))))
    return distance(mask) - distance(~mask)


def fuse(rgb, first, second):
    if np.array_equal(first, second) or not first.any() or not second.any():
        return first | second
    height, width = first.shape
    scale = min(1, MAX_SIDE / max(height, width))
    shape = (max(1, round(width * scale)), max(1, round(height * scale)))
    if scale < 1:
        small = [cv2.resize(mask.astype(np.uint8), shape, interpolation=cv2.INTER_NEAREST) != 0
                 for mask in (first, second)]
        result = fuse_small(cv2.resize(rgb, shape, interpolation=cv2.INTER_AREA), *small)
        result = cv2.resize(result.astype(np.uint8), (width, height), interpolation=cv2.INTER_NEAREST) != 0
        return (first & second) | (result & (first ^ second))
    return fuse_small(rgb, first, second)


def fuse_small(rgb, first, second):
    if not first.any() or not second.any():
        return first | second
    common_fg = first & second
    common_bg = ~(first | second)
    average = (signed(first) + signed(second)) > 0
    if not common_fg.any() or not common_bg.any() or min(average.sum(), (~average).sum()) < 5:
        return (average & ~(common_fg | common_bg)) | common_fg
    state = np.where(average, cv2.GC_PR_FGD, cv2.GC_PR_BGD).astype(np.uint8)
    state[common_fg] = cv2.GC_FGD
    state[common_bg] = cv2.GC_BGD
    return graph_cut(rgb, state)


def segment(rgb, entries):
    height, width = rgb.shape[:2]
    result = np.zeros((height, width), dtype=bool)
    for entry in entries:
        x, y, w, h = entry["crop"]
        sample = rgb[y:y+h, x:x+w]
        scale = min(1, MAX_SIDE / max(w, h))
        if scale < 1:
            sample = cv2.resize(sample, (max(1, round(w * scale)), max(1, round(h * scale))),
                                interpolation=cv2.INTER_AREA)
        mask = fit(sample, outline(sample, entry["components"]))
        if scale < 1:
            mask = cv2.resize(mask.astype(np.uint8), (w, h), interpolation=cv2.INTER_NEAREST) != 0
        result[y:y+h, x:x+w] |= mask
    return result


def load_pixels(file):
    pixels = cv2.imdecode(np.frombuffer(file.read_bytes(), np.uint8), cv2.IMREAD_UNCHANGED)
    if pixels is None or pixels.ndim != 3:
        raise ValueError("Expected a canonical source PNG")
    if pixels.shape[2] == 3:
        return pixels
    alpha = pixels[:, :, 3:4].astype(np.uint16)
    return ((pixels[:, :, :3].astype(np.uint16) * alpha + 255 * (255 - alpha) + 127) // 255).astype(np.uint8)


def main(folder):
    cv2.setNumThreads(1)
    cv2.ocl.setUseOpenCL(False)
    rgb = load_pixels(folder / "source.png")
    request = json.loads((folder / "request.json").read_text(encoding="utf-8"))
    for index, entry in enumerate(request):
        mask = segment(rgb, entry["pieces"])
        if entry["crossCheck"]:
            original = np.frombuffer((folder / f"input-{index}.bits").read_bytes(), np.uint8)
            first = np.unpackbits(original, count=mask.size, bitorder="little").reshape(mask.shape) != 0
            mask = fuse(rgb, first, mask)
        packed = np.packbits(mask, bitorder="little")
        (folder / f"output-{index}.bits").write_bytes(packed.tobytes())


if __name__ == "__main__":
    main(Path(sys.argv[1]))
