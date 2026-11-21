"""Local PP-OCR and glyph coverage on the original image grid."""

from dataclasses import dataclass
import json
import math
from pathlib import Path
import sys

import cv2 as cv
import numpy as np
import onnxruntime as ort


@dataclass
class Box:
    quad: np.ndarray
    score: float
    cores: tuple[np.ndarray, ...]

    @property
    def width(self):
        return float(np.linalg.norm(self.quad[1] - self.quad[0]))

    @property
    def height(self):
        return float(np.linalg.norm(self.quad[3] - self.quad[0]))

    @property
    def center(self):
        return self.quad.mean(axis=0)

    @property
    def direction(self):
        return (self.quad[1] - self.quad[0]) / max(self.width, 1)


@dataclass
class Glyph:
    value: str
    confidence: float
    x: float


@dataclass
class Line:
    box: Box
    words: list


def session(path):
    options = ort.SessionOptions()
    options.intra_op_num_threads = 4
    options.inter_op_num_threads = 1
    return ort.InferenceSession(str(path), sess_options=options, providers=["CPUExecutionProvider"])


def oriented_rect(points, margin=0):
    center, (width, height), angle = cv.minAreaRect(points)
    corners = cv.boxPoints((center, (width + 2 * margin, height + 2 * margin), angle))
    corners = corners[np.argsort(corners[:, 0], kind="stable")]
    left = corners[:2][np.argsort(corners[:2, 1])]
    right = corners[2:][np.argsort(corners[2:, 1])]
    return np.float32([left[0], right[0], right[1], left[1]])


def detect(detector, image):
    height, width = image.shape[:2]
    ratio = min(1920 / max(height, width), max(1, 736 / min(height, width)))
    out_width = max(32, round(width * ratio / 32) * 32)
    out_height = max(32, round(height * ratio / 32) * 32)
    sample = cv.resize(image, (out_width, out_height)).astype(np.float32) / 127.5 - 1
    probability = detector.run(None, {"x": sample.transpose(2, 0, 1)[None]})[0][0, 0]
    contours, _ = cv.findContours((probability > .3).astype(np.uint8), cv.RETR_EXTERNAL, cv.CHAIN_APPROX_SIMPLE)
    boxes = []
    for contour in contours:
        area = cv.contourArea(contour)
        perimeter = cv.arcLength(contour, True)
        if area < 10 or perimeter <= 0:
            continue
        x, y, w, h = cv.boundingRect(contour)
        stencil = cv.drawContours(np.zeros((h, w), np.uint8), [contour - (x, y)], -1, 255, cv.FILLED)
        score = cv.mean(probability[y:y+h, x:x+w], stencil)[0]
        if score < .5:
            continue
        scale = np.float32([width / probability.shape[1], height / probability.shape[0]])
        quad = oriented_rect(contour, 1.6 * area / perimeter)
        box = Box(quad * scale, score, (contour.reshape(-1, 2) * scale,))
        if min(box.width, box.height) >= 3:
            boxes.append(box)
    return join_line_fragments(boxes)


def join_line_fragments(boxes):
    # DB can split a printed line at its spaces. Align close fragments along the same baseline.
    lines = []
    for box in sorted(boxes, key=lambda item: item.center[0]):
        for index, previous in enumerate(lines):
            axis = previous.direction
            offset = box.center - previous.center
            along = float(axis @ offset)
            across = abs(float(np.array([-axis[1], axis[0]]) @ offset))
            height = min(box.height, previous.height)
            gap = along - (previous.width + box.width) / 2
            if (previous.width < previous.height or box.width < box.height or not 0 < along
                    or across > height * .25 or max(box.height, previous.height) > height * 1.55
                    or axis @ box.direction < .98 or not -height * .5 <= gap <= height * .8):
                continue
            merged = oriented_rect(np.concatenate([previous.quad, box.quad]), 1)
            lines[index] = Box(merged, min(previous.score, box.score), previous.cores + box.cores)
            break
        else:
            lines.append(box)
    return sorted(lines, key=lambda item: (item.quad[:, 1].min(), item.quad[:, 0].min()))


def crop_line(image, quad):
    width, height = [max(2, round(float(np.linalg.norm(quad[i] - quad[0])))) for i in (1, 3)]
    if height > width * 1.55:
        width, height = height, width
        quad = np.roll(quad, -1, axis=0)
    target = np.float32([[0, 0], [width-1, 0], [width-1, height-1], [0, height-1]])
    transform = cv.getPerspectiveTransform(quad, target)
    return cv.warpPerspective(image, transform, (width, height), borderMode=cv.BORDER_REPLICATE), transform


class Recognizer:
    def __init__(self, path):
        self.model = session(path)
        self.alphabet = ["", *self.model.get_modelmeta().custom_metadata_map["character"].splitlines(), " "]

    def read(self, image):
        height, width = image.shape[:2]
        scaled = min(2304, math.ceil(48 * width / height))
        padded = max(320, math.ceil(scaled / 8) * 8)
        prepared = np.zeros((1, 3, 48, padded), dtype=np.float32)
        sample = cv.resize(image, (scaled, 48), interpolation=cv.INTER_CUBIC)
        prepared[0, :, :, :scaled] = sample.transpose(2, 0, 1).astype(np.float32) / 127.5 - 1
        probabilities = self.model.run(None, {"x": prepared})[0][0]
        if len(self.alphabet) != probabilities.shape[1]:
            raise ValueError("PP-OCR character dictionary does not match the recognizer")
        path = probabilities.argmax(axis=1)
        starts = np.nonzero(np.r_[True, path[1:] != path[:-1]])[0]
        glyphs = []
        for start, end in zip(starts, [*starts[1:], len(path)]):
            code = path[start]
            if not code:
                continue
            peak = start + probabilities[start:end, code].argmax()
            center = (peak + .5) * padded / len(path) * width / scaled
            value = self.alphabet[code]
            glyphs.append(Glyph(value, float(probabilities[peak, code]), min(width - .5, center)))
        visible = [item for item in glyphs if not item.value.isspace()]
        if not visible or not any(c.isalnum() for item in visible for c in item.value):
            return 0, []
        score = float(np.mean([item.confidence for item in visible]))
        if len(visible) == 1 and score < .92:
            return 0, []
        return score, glyphs


def word_ranges(glyphs, projection):
    words = []
    current = []
    visible = [glyph.x for glyph in glyphs if not glyph.value.isspace()]
    common_step = float(np.median(np.diff(visible))) if len(visible) >= 5 else float("inf")
    for index, glyph in enumerate(glyphs):
        if glyph.value.isspace():
            if current:
                words.append(current)
                current = []
        else:
            if current and glyph.x - glyphs[current[-1]].x > 1.75 * common_step:
                middle = (glyph.x + glyphs[current[-1]].x) / 2
                left, right = int(middle - .18 * common_step), math.ceil(middle + .18 * common_step)
                if left >= 0 and right <= len(projection) and not projection[left:right].any():
                    words.append(current)
                    current = []
            current.append(index)
    if current:
        words.append(current)
    return words


def ink_pixels(line):
    gray = cv.cvtColor(line, cv.COLOR_BGR2GRAY)
    border = np.concatenate((gray[0], gray[-1], gray[:, 0], gray[:, -1]))
    background = float(np.median(border))
    low, high = np.percentile(gray, [8, 92])
    if max(background - low, high - background) < 9:
        return None
    light = high - background >= background - low
    signal = gray if light else 255 - gray
    threshold, _ = cv.threshold(signal, 0, 255, cv.THRESH_BINARY | cv.THRESH_OTSU)
    # Re-sample a threshold at original pixel centers. Closing and hole filling change glyphs like O, R and A.
    return signal, float(threshold), light


def glyph_boundaries(glyphs, projection):
    visible = [item for item in glyphs if not item.value.isspace()]
    centers = [item.x for item in visible]
    cuts = []
    for first, second in zip(centers, centers[1:]):
        middle = (first + second) / 2
        reach = (second - first) * .36
        columns = np.arange(max(1, math.ceil(middle - reach)),
                            min(len(projection), math.floor(middle + reach) + 1))
        if len(columns):
            penalty = abs(columns - middle) / max(reach, 1) * .32
            middle = float(columns[np.argmin(projection[columns] + penalty)])
        cuts.append(middle)
    return cuts


def assign_components(assignment, count, labels, stats, glyph_count, remove_specks):
    # A slanted glyph can span several CTC columns. Keep its connected strokes together unless
    # the same component contains the main strokes of multiple letters, as in joined handwriting.
    foreground = assignment >= 0
    components, columns = labels[foreground], assignment[foreground]
    pairs, votes = np.unique(components.astype(np.int64) * glyph_count + columns, return_counts=True)
    main_component = np.zeros(glyph_count, dtype=int)
    main_votes = np.zeros(glyph_count, dtype=int)
    owner = np.full(count, -1, dtype=int)
    owner_votes = np.zeros(count, dtype=int)
    for pair, vote in zip(pairs, votes):
        component, column = divmod(pair, glyph_count)
        if vote > owner_votes[component]:
            owner_votes[component], owner[component] = vote, column
        if vote > main_votes[column]:
            main_votes[column], main_component[column] = vote, component
    members = [[] for _ in range(count)]
    for column, component in enumerate(main_component):
        if component:
            members[component].append(column)
    if remove_specks:
        owner[stats[:, cv.CC_STAT_AREA] < 2] = -1
    for component, primary in enumerate(members):
        if len(primary) == 1:
            owner[component] = primary[0]
    resolved = owner[components]
    for component in range(1, count):
        primary = members[component]
        if len(primary) > 1:
            joined = components == component
            resolved[joined] = np.clip(columns[joined], primary[0], primary[-1])
    assignment[foreground] = resolved


def keep_line_ink(assignment, labels, stats, box, left, top):
    # The padded quad is for recognition. Keep intact strokes that reach the
    # detector's own line, with a narrow tolerance for detached glyph parts.
    support = np.zeros(labels.shape, np.uint8)
    for polygon in box.cores:
        cv.fillPoly(support, [np.rint(polygon - (left, top)).astype(np.int32)], 1)
    radius = max(1, min(12, round(min(box.width, box.height) * .05)))
    diameter = 2 * radius + 1
    support = cv.dilate(support, cv.getStructuringElement(cv.MORPH_ELLIPSE, (diameter, diameter)))
    hits = np.bincount(labels[support != 0], minlength=len(stats))
    keep = hits >= np.maximum(1, stats[:, cv.CC_STAT_AREA] * .04)
    keep[0] = False
    assignment[~keep[labels]] = -1


def split_ink(image, alpha, box, line, transform, glyphs):
    ink = ink_pixels(line)
    if ink is None:
        return None
    signal, threshold, light = ink
    height, width = image.shape[:2]
    x, y, w, h = cv.boundingRect(box.quad)
    x0, x1 = max(x, 0), min(x + w, width)
    y0, y1 = max(y, 0), min(y + h, height)
    if x1 <= x0 or y1 <= y0:
        return None
    projection = (signal > threshold).mean(axis=0)
    cuts = glyph_boundaries(glyphs, projection)
    assignment = np.empty((y1-y0, x1-x0), dtype=np.int32)
    for first in range(y0, y1, 128):
        end = min(first + 128, y1)
        grid_y, grid_x = np.mgrid[first:end, x0:x1]
        pixels = np.float32(np.dstack((grid_x, grid_y))).reshape(-1, 1, 2)
        horizontal, vertical = cv.perspectiveTransform(pixels, transform).reshape(-1, 2).T
        gray = cv.cvtColor(image[first:end, x0:x1], cv.COLOR_BGR2GRAY).ravel()
        original = gray if light else 255 - gray
        valid = ((horizontal >= -.5) & (horizontal <= line.shape[1] - .5)
                 & (vertical >= -.5) & (vertical <= line.shape[0] - .5)
                 & (original > threshold) & (alpha[first:end, x0:x1].ravel() > 0))
        assigned = np.searchsorted(cuts, horizontal)
        assigned[~valid] = -1
        assignment[first-y0:end-y0] = assigned.reshape(end-first, x1-x0)
    count, labels, stats, _ = cv.connectedComponentsWithStats((assignment >= 0).astype(np.uint8))
    keep_line_ink(assignment, labels, stats, box, x0, y0)
    assign_components(assignment, count, labels, stats, len(cuts) + 1, line.shape[0] >= 19)
    return x0, y0, assignment, projection


def read_box(image, alpha, box, recognizers, owned):
    crop, transform = crop_line(image, box.quad)
    variants = sorted((recognizer.read(crop) for recognizer in recognizers), key=lambda match: match[0], reverse=True)
    for confidence, glyphs in variants:
        if confidence < .84:
            break
        line = read_strokes(image, alpha, box, crop, transform, glyphs, owned)
        if line is not None:
            return line
    return None


def read_strokes(image, alpha, box, crop, transform, glyphs, owned):
    result = split_ink(image, alpha, box, crop, transform, glyphs)
    if result is None:
        return None
    x, y, assignment, projection = result
    used = owned[y:y+assignment.shape[0], x:x+assignment.shape[1]]
    assignment[used] = -1
    words = []
    glyph_index = 0
    for positions in word_ranges(glyphs, projection):
        word = []
        for position in positions:
            letter = glyphs[position]
            yy, xx = np.nonzero(assignment == glyph_index)
            glyph_index += 1
            if len(xx) == 0:
                return None
            word.append((letter, x + xx, y + yy))
        words.append(word)
    used |= assignment >= 0
    return Line(box, words)


def group_lines(lines):
    groups = []
    for line in lines:
        best = None
        minimum_gap = float("inf")
        for group in groups:
            prior = group[-1].box
            current = line.box
            axis = prior.direction
            normal = np.array([-axis[1], axis[0]])
            line_height = min(prior.height, current.height)
            distance = float(normal @ (current.center - prior.center))
            if (axis @ current.direction < .93 or max(prior.height, current.height) > line_height * 2.3
                    or distance < line_height * .57 or distance > (prior.height + current.height) / 2 + line_height * .55):
                continue
            first = prior.quad @ axis
            second = current.quad @ axis
            overlap = min(first.max(), second.max()) - max(first.min(), second.min())
            if overlap >= .55 * min(prior.width, current.width) and distance < minimum_gap:
                best, minimum_gap = group, distance
        if best is None:
            groups.append([line])
        else:
            best.append(line)
    return groups


def write_glyph(folder, index, glyph, xs, ys):
    left, top = int(xs.min()), int(ys.min())
    width, height = int(xs.max()) - left + 1, int(ys.max()) - top + 1
    mask = np.zeros((height, width), dtype=np.uint8)
    mask[ys-top, xs-left] = 1
    np.packbits(mask, axis=None, bitorder="little").tofile(folder / f"output-{index}.bits")
    return {"value": glyph.value, "confidence": glyph.confidence, "index": index,
            "bounds": [left, top, width, height]}


def run(folder, model_dir, limit):
    cv.setNumThreads(2)
    source = cv.imdecode(np.fromfile(folder / "source.png", np.uint8), cv.IMREAD_UNCHANGED)
    image = source[:, :, :3].copy()
    alpha = source[:, :, 3] if source.shape[2] == 4 else np.full(source.shape[:2], 255, np.uint8)
    detector = session(model_dir / "PP-OCRv6_det_small.onnx")
    recognizers = [Recognizer(model_dir / name) for name in
                   ("PP-OCRv6_rec_small.onnx", "cyrillic_PP-OCRv5_rec_mobile.onnx")]
    owned = np.zeros(source.shape[:2], bool)
    lines = []
    for box in detect(detector, image):
        line = read_box(image, alpha, box, recognizers, owned)
        if line is not None:
            lines.append(line)
    count, skipped = 0, 0
    areas = []
    for group in group_lines(lines):
        glyph_count = sum(len(word) for line in group for word in line.words)
        if count + glyph_count > limit:
            skipped += 1
            continue
        words = []
        for line_index, line in enumerate(group):
            for word in line.words:
                masks = []
                for glyph, xs, ys in word:
                    masks.append(write_glyph(folder, count, glyph, xs, ys))
                    count += 1
                words.append({"line": line_index, "glyphs": masks})
        areas.append({"words": words})
    (folder / "result.json").write_text(json.dumps({"areas": areas, "skipped": skipped}, ensure_ascii=False), encoding="utf-8")


if __name__ == "__main__":
    run(Path(sys.argv[1]), Path(sys.argv[2]), int(sys.argv[3]))
