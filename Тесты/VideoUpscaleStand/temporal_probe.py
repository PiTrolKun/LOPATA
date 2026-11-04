"""A source-motion residual diagnostic, not a perceptual quality verdict."""
import argparse
import json
import pathlib
import subprocess

import cv2
import numpy as np


def decode(ffmpeg, path):
    raw = subprocess.check_output([str(ffmpeg), '-v', 'error', '-i', str(path),
        '-vf', 'format=gbrp10le,scale=320:180:flags=lanczos,format=gray',
        '-an', '-f', 'rawvideo', '-pix_fmt', 'gray', '-'],
        creationflags=subprocess.CREATE_NO_WINDOW)
    return np.frombuffer(raw, np.uint8).reshape(-1, 180, 320)


def compare(source, result):
    if source.shape != result.shape:
        raise ValueError('Frame sequences differ in length or geometry')
    estimator = cv2.DISOpticalFlow_create(cv2.DISOPTICAL_FLOW_PRESET_MEDIUM)
    xx, yy = np.meshgrid(np.arange(320, dtype=np.float32), np.arange(180, dtype=np.float32))
    scores, cuts = [], []
    for index in range(1, len(source)):
        prev, current = source[index - 1], source[index]
        if np.abs(current.astype(float) - prev).mean() > 30:
            cuts.append(index)
            continue
        backward = estimator.calc(current, prev, None)
        forward = estimator.calc(prev, current, None)
        mx, my = xx + backward[:, :, 0], yy + backward[:, :, 1]
        fw = cv2.remap(forward, mx, my, cv2.INTER_LINEAR)
        source_delta = current.astype(float) - cv2.remap(prev.astype(np.float32), mx, my, cv2.INTER_LINEAR)
        mask = ((mx > 2) & (mx < 317) & (my > 2) & (my < 177)
                & (np.linalg.norm(backward + fw, axis=-1) < 1.)
                & (np.abs(source_delta) < 3.))
        if mask.sum() < 500:
            continue
        output_delta = result[index].astype(float) - cv2.remap(
            result[index - 1].astype(np.float32), mx, my, cv2.INTER_LINEAR)
        extra = output_delta - source_delta
        scores.append({'frame': index, 'pixels': int(mask.sum()),
                       'extra_temporal_rms_codes_8bit': float(np.sqrt(np.mean(extra[mask] ** 2)))})
    values = [s['extra_temporal_rms_codes_8bit'] for s in scores]
    return {'frame_pairs': scores, 'cut_candidates_excluded': cuts,
            'median_extra_rms': float(np.median(values)) if values else None,
            'p95_extra_rms': float(np.percentile(values, 95)) if values else None}


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('stand', type=pathlib.Path)
    args = parser.parse_args()
    root = args.stand
    ffmpeg = root / 'tools/dlss5-visual-enhancer/ffmpeg.exe'
    fixtures = [('cats_glass_cuts', 2), ('cats_face_hands_motion', 2),
                ('cats_fur_closeup', 2), ('sg1_dark_face', 1), ('sg1_motion_uniform', 1)]
    report = {'scope': '320x180 gray diagnostic with source-derived bidirectional DIS motion; '
              'excludes large cut candidates, occlusion/inconsistent motion and changing source pixels. '
              'A higher value can include legitimate new lighting/material response. '
              'This is not an artifact classifier or acceptance score.', 'fixtures': {}}
    for fixture, style in fixtures:
        source = decode(ffmpeg, root / 'inputs' / (fixture + '.mkv'))
        comparisons = {}
        for label, job in [('one_pass', f've_s{style}_{fixture}'),
                           ('four_passes', f've_freedom_s{style}_p4_{fixture}')]:
            comparisons[label] = compare(source, decode(ffmpeg, root / 'jobs' / job / 'result.mkv'))
        report['fixtures'][fixture] = comparisons
        print(fixture, {k: round(v['median_extra_rms'], 3) for k, v in comparisons.items()}, flush=True)
    (root / 'previews/temporal_probe.json').write_text(json.dumps(report, indent=2), encoding='utf-8')


if __name__ == '__main__':
    main()
