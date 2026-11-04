"""LOPATA I/O adapter around upstream T8 ModelHandle; neural pipeline stays upstream."""
import json, os, pathlib, sys

request = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding='utf-8-sig'))
os.environ['IMAGEIO_FFMPEG_EXE'] = request['ffmpeg']
sys.dont_write_bytecode = True
sys.path.insert(0, request['source'])
import torch
from fractions import Fraction
import imageio.v2 as imageio
from flashvsr_plus.comfy_runtime import ModelHandle, Options
from flashvsr_plus.inference import InferenceRequest
from flashvsr_plus.inference import pipeline, output

class LosslessWriter(output.IncrementalVideoWriter):
    # Upstream streaming/crop/frame conversion preserved; only container/codec differs.
    def __init__(self, path, fps, *, preset='veryfast', crop_hw=None):
        destination = pathlib.Path(path); destination.parent.mkdir(parents=True, exist_ok=True)
        self.destination = destination
        self.pixel_format = 'bgr0'
        self.fps = float(fps)
        self.crop_hw = crop_hw
        self.writer = imageio.get_writer(str(destination), format='FFMPEG', mode='I', fps=fps,
            codec='ffv1', pixelformat='bgr0', macro_block_size=None,
            input_params=['-r', str(Fraction(fps).limit_denominator(1000000))])
        self.frame_count = 0
        self.first_frame = None
    def verify(self):
        data = super().verify(); data['codec'] = 'ffv1'; return data

pipeline.IncrementalVideoWriter = LosslessWriter
output.IncrementalVideoWriter = LosslessWriter
if not torch.cuda.is_available(): raise RuntimeError('VideoUpscale.CudaRequired')
device = request['device']
if device == 'auto':
    compatible = [(torch.cuda.mem_get_info(i)[0], i) for i in range(torch.cuda.device_count())]
    if not compatible: raise RuntimeError('VideoUpscale.CudaRequired')
    device = 'cuda:' + str(max(compatible)[1])
torch.cuda.set_device(device)
options = Options(**request['options']).validate()
handle = ModelHandle(mode=request['mode'], precision=request['precision'], attention='triton_block_sparse',
    decoder='tiny', directory=request['weights'], auto_download=False, keep_loaded=False, device=device)
result = handle.infer(InferenceRequest(input_path=request['input'], output_path=request['output'],
    height=request['canvas'][1], width=request['canvas'][0], content_height=request['content'][1], content_width=request['content'][0]),
    seed=request['seed'], options=options, progress=lambda line: print(line, flush=True))
print(json.dumps(dict(frames=result.frames, fps=result.fps, device=device)), flush=True)
