import json
from math import hypot

import cv2 as cv
import numpy as np
from numba import njit
from pymatting import estimate_alpha_cf, estimate_foreground_ml


# Native-pixel neighborhoods. Padding gives the solver full context at tile seams.
SILHOUETTE_RADIUS = 9
TILE_SIZE = 512
NEIGHBORS = np.array([
    (dy, dx, 1 / hypot(dy, dx))
    for dy in range(-2, 3) for dx in range(-2, 3) if 0 < hypot(dy, dx) <= 2.25
], dtype=np.float32)


def distance(mask):
    return cv.distanceTransform(mask.astype(np.uint8), cv.DIST_L2, cv.DIST_MASK_PRECISE)


def shift(values, x, y):
    transform = np.array([[1, 0, x], [0, 1, y]], dtype=np.float32)
    return cv.warpAffine(values, transform, values.shape[:2][::-1],
                         flags=cv.INTER_LINEAR | cv.WARP_INVERSE_MAP, borderMode=cv.BORDER_REFLECT101)


@njit
def follow_ridges(score, distance, outward, angle_x, angle_y, bright, reach):
    # Walk outward from the silhouette. Each step must follow a line of the same
    # polarity and similar direction. Ordinary connectivity floods background texture.
    candidate = (score >= 1.8 + .036 * distance) & (distance > 0) & (distance <= reach)
    seeds = (distance <= 2) | ((distance <= 5) & (outward > .55) & (score >= 4.2))
    selected = candidate & seeds
    initial_x = angle_x.copy()
    initial_y = angle_y.copy()
    ys, xs = np.nonzero(candidate & ~selected)
    steps = np.empty(len(ys), dtype=np.float32)
    for i in range(len(ys)):
        steps[i] = distance[ys[i], xs[i]]
    order = np.argsort(steps)
    height, width = distance.shape
    for index in order:
        y, x = ys[index], xs[index]
        if distance[y, x] > 7 and outward[y, x] < .42:
            continue
        for offset in NEIGHBORS:
            dy, dx, inverse_length = int(offset[0]), int(offset[1]), offset[2]
            py, px = y + dy, x + dx
            if not (0 <= py < height and 0 <= px < width and selected[py, px]
                    and distance[py, px] <= distance[y, x] - .2 and bright[py, px] == bright[y, x]):
                continue
            if abs(angle_x[y, x]*dx + angle_y[y, x]*dy)*inverse_length < .68:
                continue
            if abs(angle_x[py, px]*dx + angle_y[py, px]*dy)*inverse_length < .70:
                continue
            if abs(initial_x[py, px]*angle_x[y, x] + initial_y[py, px]*angle_y[y, x]) < .59:
                continue
            if inverse_length < .6 and score[(y + py)//2, (x + px)//2] < 1.1:
                continue
            selected[y, x] = True
            initial_x[y, x] = initial_x[py, px]
            initial_y[y, x] = initial_y[py, px]
            break
    return selected


def strand_matte(rgb, shape, reach, hints):
    far = distance(~shape)
    luminance = cv.cvtColor(rgb, cv.COLOR_RGB2GRAY).astype(np.float32)
    luminance = cv.GaussianBlur(luminance, (0, 0), .55)
    pixels = rgb.astype(np.float32)
    rgb_smooth = cv.GaussianBlur(pixels, (0, 0), .55)
    dx = cv.Sobel(far, cv.CV_32F, 1, 0, scale=1 / 8)
    dy = cv.Sobel(far, cv.CV_32F, 0, 1, scale=1 / 8)
    best = np.zeros_like(luminance)
    strength = np.zeros_like(luminance)
    angle_x, angle_y = np.zeros_like(best), np.zeros_like(best)
    bright = np.zeros(best.shape, dtype=bool)
    front = np.zeros_like(rgb_smooth)
    collar = (far > 0) & (far <= reach)
    for radius in (1.85, 3.1):
        for angle in np.arange(12) * (np.pi / 12):
            nx, ny = np.cos(angle), np.sin(angle)
            tx, ty = ny, -nx
            a = shift(luminance, nx * radius, ny * radius)
            b = shift(luminance, -nx * radius, -ny * radius)
            lighter = luminance - np.maximum(a, b)
            darker = np.minimum(a, b) - luminance
            contrast = np.maximum(np.maximum(lighter, darker), 0)
            symmetry = contrast / (.01 + contrast + np.abs(a - b))
            along = np.maximum(shift(contrast, 1.5 * tx, 1.5 * ty), shift(contrast, -1.5 * tx, -1.5 * ty))
            outward = np.clip(np.abs(tx * dx + ty * dy), 0, 1)
            radial_weight = 1 - np.minimum(far / 11, .92) * (1 - outward**.65)
            score = np.sqrt(contrast * np.minimum(contrast, along) * symmetry) * radial_weight
            improve = collar & (score > best) & (score >= 1.4)
            if not improve.any():
                continue
            best[improve] = score[improve]
            angle_x[improve], angle_y[improve] = tx, ty
            bright[improve] = (lighter > darker)[improve]
            background = (shift(rgb_smooth, nx * radius, ny * radius)[improve]
                          + shift(rgb_smooth, -nx * radius, -ny * radius)[improve]) * .5
            difference = pixels[improve] - background
            signal = np.minimum(np.abs(difference).max(axis=1), contrast[improve] * 1.8)
            # Recover a foreground color from the two banks of the line. Ambiguous
            # low-contrast marks contribute little opacity when the cutout is moved.
            opacity = signal / (signal + 52)
            strength[improve] = opacity
            front[improve] = background + difference / np.maximum(opacity[:, None], .1)
    nearby_guide = cv.dilate((hints > 0).astype(np.uint8), np.ones((3, 3), dtype=np.uint8)) > 0
    # The Hessian gives a continuous tangent when equally strong sampled angles
    # alternate across neighboring pixels, especially where several hairs start together.
    smoothed = cv.GaussianBlur(luminance, (0, 0), .75)
    xx = cv.Sobel(smoothed, cv.CV_32F, 2, 0, scale=.25)
    yy = cv.Sobel(smoothed, cv.CV_32F, 0, 2, scale=.25)
    xy = cv.Sobel(smoothed, cv.CV_32F, 1, 1, scale=.25)
    tangent = .5 * np.arctan2(2 * xy, xx - yy) + (xx + yy < 0) * (np.pi / 2)
    directional = np.hypot(xx - yy, 2 * xy) > .75 * (.01 + np.abs(xx + yy))
    angle_x[directional], angle_y[directional] = -np.sin(tangent[directional]), np.cos(tangent[directional])
    outward = np.abs(angle_x * dx + angle_y * dy)
    taken = follow_ridges(best, far, outward, angle_x, angle_y, bright, reach)
    # A guide can restore nearby detail, but a painted pixel must not seed a
    # long path through an unrelated textured background.
    guided = nearby_guide & (best >= 2.4)
    confidence = np.clip((best - (2 + .08 * far)) / (5 + .05 * far), 0, 1)
    opacity = strength * (taken | guided) * confidence
    return opacity, np.clip(front, 0, 255)


def silhouette(rgb, hard, hints):
    inside = distance(hard)
    outside = distance(~hard)
    # Preserve an anchor on thin or small selections. A tile with no object or no
    # background simply carries forward the current cutout.
    radius = min(SILHOUETTE_RADIUS, max(1, float(inside.max()) - .5))
    foreground = (inside > radius) | ((hints > 0) & (outside <= 2))
    background = (outside > SILHOUETTE_RADIUS) | (hints < 0)
    foreground &= ~background
    band = ~(background | foreground)
    if not foreground.any() or not background.any() or not band.any():
        return hard.copy(), hard.astype(np.float64), rgb.copy()
    guide = np.full(hard.shape, .5, dtype=np.float64)
    guide[foreground] = 1
    guide[background] = 0
    image = rgb.astype(np.float64) / 255
    alpha = np.clip(estimate_alpha_cf(image, guide), 0, 1)
    front = rgb.copy()
    front[band] = np.rint(np.clip(estimate_foreground_ml(image, alpha)[band], 0, 1) * 255).astype(np.uint8)
    shape = alpha >= .5
    _, labels = cv.connectedComponents(shape.astype(np.uint8))
    connected = np.unique(labels[shape & (hard | (hints > 0))])
    shape &= np.isin(labels, connected)
    return shape, alpha, front


def draw_hints(shape, strokes):
    guides = np.zeros(shape, dtype=np.int8)
    for keep, points in strokes:
        color = 1 if keep else -1
        # Rasterize continuous gestures at source resolution. The most recent
        # brush stroke wins, including where a retain and exclude stroke cross.
        thickness = 1 if keep else 3
        if len(points) == 1:
            cv.circle(guides, tuple(points[0]), 0 if keep else 1, color, -1)
        else:
            cv.polylines(guides, [np.asarray(points, dtype=np.int32)], False, color, thickness)
    return guides


def refine(folder, rgb, source_alpha):
    config = json.loads((folder / "edge.json").read_text(encoding="utf-8"))
    left, top, right, bottom = config["area"]
    reach = config["reach"]
    halo = reach + 16
    height, width = rgb.shape[:2]
    values = np.frombuffer((folder / "input.bits").read_bytes(), dtype=np.uint8)
    hard = np.unpackbits(values, count=width * height, bitorder="little").reshape(height, width).astype(bool)
    # The existing PNG, or the current smooth contour rendered as a PNG, is the
    # baseline outside the edit. These alpha values already include source opacity.
    lx, ly = config["baseline"]
    previous = cv.imdecode(np.frombuffer((folder / "before.png").read_bytes(), dtype=np.uint8), cv.IMREAD_UNCHANGED)
    if previous is None or previous.ndim != 3:
        raise ValueError("Expected a color cutout")
    if previous.shape[2] == 3:
        previous = cv.cvtColor(previous, cv.COLOR_BGR2BGRA)
    cutout = np.zeros((height, width, 4), dtype=np.uint8)
    cutout[ly:ly + previous.shape[0], lx:lx + previous.shape[1]] = previous
    del previous
    guides = draw_hints(hard.shape, config["strokes"])
    guides[:top, :] = guides[bottom:, :] = 0
    guides[:, :left] = guides[:, right:] = 0
    refined = hard.copy()
    # For each tile, keep only the central region. Work stops at picture edges
    # and local edits never change any saved color, transparency or mask outside the scope.
    for y in range(top, bottom, TILE_SIZE):
        for x in range(left, right, TILE_SIZE):
            end_y, end_x = min(y + TILE_SIZE, bottom), min(x + TILE_SIZE, right)
            origin_y, origin_x = max(0, y - halo), max(0, x - halo)
            neighborhood = (slice(origin_y, min(height, end_y + halo)), slice(origin_x, min(width, end_x + halo)))
            patch = hard[neighborhood]
            hints = guides[neighborhood]
            if not hints.any() and (patch.all() or not patch.any()):
                continue
            colors = rgb[neighborhood]
            pixels = colors
            if source_alpha is not None:
                opacity = source_alpha[neighborhood][:, :, None].astype(np.uint16)
                pixels = ((colors.astype(np.uint16) * opacity + 128 * (255 - opacity) + 127) // 255).astype(np.uint8)
            shape, alpha, front = silhouette(pixels, patch, hints)
            strands, color = strand_matte(pixels, shape, reach, hints) if shape.any() else (np.zeros_like(alpha), colors)
            stronger = strands > alpha
            alpha[stronger], front[stronger] = strands[stronger], color[stronger]
            keep = (hints > 0) & (alpha < .32)
            alpha[keep], front[keep] = .32, colors[keep]
            alpha[hints < 0] = 0
            shape[hints < 0] = False
            center = (slice(y - origin_y, end_y - origin_y), slice(x - origin_x, end_x - origin_x))
            output = (slice(y, end_y), slice(x, end_x))
            new_alpha = np.rint(np.clip(alpha[center], 0, 1) * 255).astype(np.uint8)
            if source_alpha is not None:
                original_pixels = source_alpha[neighborhood] != 255
                front[original_pixels] = colors[original_pixels]
                shape &= source_alpha[neighborhood] > 0
                new_alpha = ((new_alpha.astype(np.uint16) * source_alpha[output] + 127) // 255).astype(np.uint8)
            use = ((distance(patch) <= SILHOUETTE_RADIUS) & (distance(~patch) <= halo) | (hints != 0))[center]
            use &= (alpha[center] > 0) | patch[center] | (cutout[output][:, :, 3] > 0)
            cutout[output][use, :3] = front[center][use, ::-1]
            cutout[output][use, 3] = new_alpha[use]
            refined[output] = shape[center]
    ok, encoded = cv.imencode(".png", cutout)
    if not ok:
        raise ValueError("Could not encode detailed cutout")
    (folder / "cutout.png").write_bytes(encoded.tobytes())
    (folder / "output-0.bits").write_bytes(np.packbits(refined, bitorder="little").tobytes())
