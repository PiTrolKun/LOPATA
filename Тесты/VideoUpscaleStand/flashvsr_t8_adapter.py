"""Thin adapter for T8's ready host-neutral ModelHandle, with upstream options."""
import argparse
import dataclasses
import json
import os
import pathlib
import sys
import time

parser = argparse.ArgumentParser()
parser.add_argument('request', type=pathlib.Path)
args = parser.parse_args()
request = json.loads(args.request.read_text(encoding='utf-8'))
root = pathlib.Path(request['installation'])
os.environ['IMAGEIO_FFMPEG_EXE'] = request['ffmpeg']
sys.path.insert(0, str(root))
import torch
from flashvsr_plus.inference import InferenceRequest
from flashvsr_plus.comfy_runtime import ModelHandle, Options

options = Options(**request['options']).validate()
handle = ModelHandle(mode=request['mode'], precision=request['precision'],
    attention='triton_block_sparse', decoder='tiny', directory=root/'ckpts',
    auto_download=False, keep_loaded=False, device=request.get('device', 'cuda:0'))
torch.cuda.reset_peak_memory_stats()
start = time.monotonic()
result = handle.infer(InferenceRequest(input_path=request['input'],
    output_path=request['output'], height=request['canvas'][1], width=request['canvas'][0],
    content_height=request['content'][1], content_width=request['content'][0]),
    seed=1, options=options, progress=lambda x: print(x, flush=True))
torch.cuda.synchronize()
report = dict(frames=result.frames, fps=result.fps, metadata=result.metadata,
    total_seconds=time.monotonic()-start,
    torch_peak_allocated_bytes=torch.cuda.max_memory_allocated(),
    torch_peak_reserved_bytes=torch.cuda.max_memory_reserved(),
    options=dataclasses.asdict(options), request=request)
pathlib.Path(request['report']).write_text(json.dumps(report, ensure_ascii=False,
    indent=2, default=str), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, default=str), flush=True)
