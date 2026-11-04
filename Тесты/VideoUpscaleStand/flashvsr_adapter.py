"""Use the ready FlashVSR+ T8 inference API, including its padding/cropping."""
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
from flashvsr_plus.inference import InferenceRequest, build_inference_pipeline
from flashvsr_plus.inference.presets import model_paths_for_mode, stream_config_for_mode

start = time.monotonic()
config = stream_config_for_mode(
    request['mode'], device=request.get('device', 'cuda:0'), seed=1,
    decode_every=request.get('decode_every', 8),
    attn_kernel='triton_block_sparse', cache_offload_mode='always',
    tiny_decoder_mode='serial', color_correction=True)
print('Loading author pipeline', flush=True)
pipeline = build_inference_pipeline(config, model_paths_for_mode(root, request['mode']))
torch.cuda.synchronize()
loaded = time.monotonic()
torch.cuda.reset_peak_memory_stats()
print('Model loaded; running inference', flush=True)
result = pipeline.infer(InferenceRequest(
    input_path=request['input'], output_path=request['output'],
    height=request['canvas'][1], width=request['canvas'][0],
    content_height=request['content'][1], content_width=request['content'][0]),
    progress=lambda message: print(message, flush=True))
torch.cuda.synchronize()
report = dict(frames=result.frames, fps=result.fps, metadata=result.metadata,
    loading_seconds=loaded-start, inference_seconds=time.monotonic()-loaded,
    torch_peak_allocated_bytes=torch.cuda.max_memory_allocated(),
    torch_peak_reserved_bytes=torch.cuda.max_memory_reserved(),
    config=dataclasses.asdict(config), request=request)
pathlib.Path(request['report']).write_text(
    json.dumps(report, ensure_ascii=False, indent=2, default=str), encoding='utf-8')
print(json.dumps(report, ensure_ascii=False, default=str), flush=True)
