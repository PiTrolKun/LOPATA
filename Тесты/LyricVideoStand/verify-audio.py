"""Independent PCM/AAC clock check using Python's standard library only."""
import argparse
import array
from datetime import datetime, timezone
import json
import math
from pathlib import Path
import subprocess

parser = argparse.ArgumentParser()
parser.add_argument('--root', type=Path, required=True)
parser.add_argument('--run', type=Path, required=True)
args = parser.parse_args()
report = json.loads((args.run / 'checks.json').read_text(encoding='utf-8'))
cache = args.root / 'Тесты/LyricVideoStand/out/cache' / report['SourceHash'][:16]
original = array.array('h')
with (cache / 'audio.pcm').open('rb') as stream:
    original.frombytes(stream.read())
exe = args.root / 'Runtime/Capture/FFmpeg-8.1/bundle/ffmpeg.exe'
results = []
for name, start in [('opening', 0), ('main', 70), ('ending', report['SongDuration'] - 2)]:
    raw = subprocess.run([str(exe), '-v', 'error', '-i', str(args.run / (name + '.mp4')),
                          '-map', '0:a:0', '-c:a', 'pcm_f32le', '-f', 'f32le', 'pipe:1'],
                         capture_output=True, check=True).stdout
    decoded = array.array('f'); decoded.frombytes(raw)
    if name != 'ending':
        samples = range(4800, 48000 * 6 - 4800, 48)
        reference = [(original[(int(start * 48000) + i) * 2] + original[(int(start * 48000) + i) * 2 + 1]) / 65536
                     for i in samples]
        ref_power = sum(v * v for v in reference)
        best = (-1, None)
        for lag in range(-960, 961, 8):
            actual = [(decoded[(i + lag) * 2] + decoded[(i + lag) * 2 + 1]) / 2 for i in samples]
            power = sum(v * v for v in actual)
            correlation = sum(a * b for a, b in zip(reference, actual)) / math.sqrt(ref_power * power)
            if correlation > best[0]: best = (correlation, lag)
        assert best[0] > .98 and abs(best[1]) <= 48, (name, best)
        results.append({'clip': name, 'correlation': best[0], 'lag_samples': best[1]})
    else:
        tail = decoded[3 * 48000 * 2:]
        rms = math.sqrt(sum(v * v for v in tail) / len(tail))
        assert rms < .0001, rms
        results.append({'clip': name, 'credit_tail_rms': rms})
output = {'Program': 'LOPATA', 'ProgramVersion': (args.root / 'VERSION').read_text().strip(),
          'Scenario': 'lyric-video/stand-independent-audio-check',
          'CreatedUtc': datetime.now(timezone.utc).isoformat(), 'Results': results}
(args.run / 'independent-audio-check.json').write_text(json.dumps(output, ensure_ascii=False, indent=2), encoding='utf-8')
print(json.dumps(results, indent=2))
