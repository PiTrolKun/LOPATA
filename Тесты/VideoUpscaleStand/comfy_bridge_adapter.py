"""File I/O around the author's unchanged isolated VapourSynth NR runner."""
import argparse
import json
import pathlib
import subprocess
import sys

import numpy as np


parser = argparse.ArgumentParser()
parser.add_argument('request', type=pathlib.Path)
args = parser.parse_args()
request = json.loads(args.request.read_text(encoding='utf-8'))
folder = args.request.parent
width, height = request['size']
count = request['frames']
frames = np.lib.format.open_memmap(folder / 'input.npy', mode='w+',
    dtype=np.float32, shape=(count, height, width, 3))
decoder = subprocess.Popen([request['ffmpeg'], '-v', 'error', '-i', request['input'],
    '-map', '0:v:0', '-f', 'rawvideo', '-pix_fmt', 'rgb24', '-'], stdout=subprocess.PIPE,
    creationflags=subprocess.CREATE_NO_WINDOW)
try:
    for index in range(count):
        raw = decoder.stdout.read(width * height * 3)
        if len(raw) != width * height * 3:
            raise RuntimeError('Source decoder returned a partial frame')
        frames[index] = np.frombuffer(raw, dtype=np.uint8).reshape(height, width, 3) / 255.
    if decoder.stdout.read(1) or decoder.wait():
        raise RuntimeError('Source decoder frame count or exit code differs')
finally:
    decoder.stdout.close()
    if decoder.poll() is None:
        decoder.kill()
        decoder.wait()
frames.flush()
del frames
installation = pathlib.Path(request['installation'])
subprocess.run([sys.executable, str(installation / 'bridge_runner.py'),
    str(folder / 'input.npy'), str(folder / 'enhanced.npy'),
    '--plugin', str(installation / 'runtime/vsdlssnr.dll'),
    '--snippet', str(installation / 'runtime/nvngx_dlssnr.dll'),
    '--settings', json.dumps(request['settings'])], check=True,
    creationflags=subprocess.CREATE_NO_WINDOW)
enhanced = np.load(folder / 'enhanced.npy', mmap_mode='r')
if enhanced.shape != (count, height, width, 3):
    raise RuntimeError('Author bridge returned a different shape')
encoder = subprocess.Popen([request['ffmpeg'], '-v', 'error', '-n',
    '-f', 'rawvideo', '-pix_fmt', 'rgb48le', '-s', f'{width}x{height}',
    '-r', request['fps'], '-i', '-', '-i', request['input'],
    '-map', '0:v:0', '-map', '1:a?', '-c:v', 'ffv1', '-level', '3',
    '-c:a', 'copy', request['output']], stdin=subprocess.PIPE,
    creationflags=subprocess.CREATE_NO_WINDOW)
try:
    for frame in enhanced:
        encoder.stdin.write(np.rint(np.clip(frame, 0, 1) * 65535).astype('<u2').tobytes())
    encoder.stdin.close()
    if encoder.wait():
        raise RuntimeError('FFV1 encoder failed')
finally:
    if encoder.poll() is None:
        encoder.kill()
        encoder.wait()
print('Author bridge processed all frames; no optional depth/motion guides supplied', flush=True)
