"""Explicit tiny CPU-only codec check. No Torch or model is imported."""
import argparse
import json
from pathlib import Path
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument("--libraries", required=True)
parser.add_argument("--tools", required=True)
parser.add_argument("--output", required=True)
args = parser.parse_args()
sys.path.insert(0, args.libraries)
sys.path.insert(0, args.tools)
import numpy as np
import av
from vace_media import encode

output = Path(args.output)
output.parent.mkdir(parents=True, exist_ok=True)
if output.exists():
    raise ValueError("Use a fresh output filename")
started = time.monotonic()
def facts():
    return dict(Schema=1, Program="LOPATA", ProgramVersion="media-smoke", Scenario="video-generation/wan-vace-1.3b",
                GenerationSeconds=123.456 + time.monotonic() - started, ModelHistory="not-applicable", Models=[])
def frames():
    for index in range(5):
        pixels = np.zeros((192, 320, 3), dtype=np.uint8)
        pixels[:, :, index % 3] = 60 + index * 30
        yield pixels
count = encode(output, frames(), 320, 192, 19, facts, "cpu-media-smoke")
with av.open(str(output)) as source:
    tags = {k.lower(): value for k, value in source.metadata.items()}
    public = json.loads(tags["lopata_video"])
    assert count == source.streams.video[0].frames == 5
    assert float(source.streams.video[0].average_rate) == 16.0
    assert public["GenerationSeconds"] >= 123.456
    assert abs(float(tags["lopata_generation_seconds"]) - public["GenerationSeconds"]) < 0.001
    print(json.dumps(dict(video=str(output), frames=count, fps=str(source.streams.video[0].average_rate),
        generation_seconds=public["GenerationSeconds"], imported_torch="torch" in sys.modules)))
