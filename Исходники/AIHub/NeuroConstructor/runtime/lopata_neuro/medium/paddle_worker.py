"""Isolated Paddle Inference process. A request carries four image cells, never filenames."""
import base64
import io
import json
import os
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import numpy as np
from PIL import Image
from paddle import inference


def predictor(folder):
    graph = folder / "inference.json"
    if not graph.exists():
        graph = folder / "inference.pdmodel"
    config = inference.Config(str(graph), str(folder / "inference.pdiparams"))
    config.disable_gpu()
    config.disable_glog_info()
    config.disable_mkldnn()
    config.set_cpu_math_library_num_threads(min(3, os.cpu_count() or 1))
    return inference.create_predictor(config)


def forward(net, **feeds):
    for name in net.get_input_names():
        net.get_input_handle(name).copy_from_cpu(np.ascontiguousarray(feeds[name]))
    net.run()
    return [net.get_output_handle(name).copy_to_cpu() for name in net.get_output_names()]


def rgb(pic, size):
    return np.asarray(pic.resize(size, Image.Resampling.BICUBIC), dtype=np.float32) / 255


def cells(pic):
    w, h = pic.size
    return [pic.crop((x*w//2, y*h//2, (x+1)*w//2, (y+1)*h//2)) for y in range(2) for x in range(2)]


class ScreenReader:
    def __init__(self, root):
        self.det, self.en, self.slavic, self.layout = [predictor(root / name) for name in
            ("PP-OCRv5_mobile_det", "en_PP-OCRv5_mobile_rec", "eslav_PP-OCRv5_mobile_rec", "PaddleOCR-VL/PP-DocLayoutV2")]

    def detect(self, pic):
        # The screen in this profile is a square 2x2 grid. Paddle detector/recognizer inputs use BGR.
        bgr = rgb(pic, (256, 256))[:, :, ::-1]
        norm = (bgr - np.float32([.485, .456, .406])) / np.float32([.229, .224, .225])
        mask = forward(self.det, x=norm.transpose(2, 0, 1)[None])[0].squeeze()
        det = [np.array([[c.mean(), c.max(), (c > .3).mean()] for c in np.array_split(part,4,axis=1)]).flatten()
               for row in np.array_split(mask,2,axis=0) for part in np.array_split(row,2,axis=1)]
        return np.stack(det)

    def recognize(self, pic, net):
        crop = np.zeros((4,3,48,320), dtype=np.float32)
        for i, cell in enumerate(cells(pic)):
            crop[i,:,:,:48] = ((rgb(cell,(48,48))[:,:,::-1] - .5)*2).transpose(2,0,1)
        chars = forward(net, x=crop)[0].max(axis=1)
        return np.stack([group.max(axis=1) for group in np.array_split(chars,32,axis=1)],axis=1)

    def document(self, pic):
        layout_input = rgb(pic,(800,800)).transpose(2,0,1)[None]
        # Detection postprocessing uses original-coordinate scale factors.
        shapes = np.float32([[800,800]])
        scale = np.float32([[800/pic.height,800/pic.width]])
        outputs = forward(self.layout, image=layout_input, im_shape=shapes, scale_factor=scale)
        boxes = next(a for a in outputs if a.ndim == 2 and a.shape[1] in (6,8))[:,:6]
        layout = np.zeros((4,26),np.float32)
        for cls, confidence, x1, y1, x2, y2 in boxes:
            if confidence <= 0 or not 0 <= cls < layout.shape[1]:
                continue
            for idx in range(4):
                x, y = idx%2*pic.width/2, idx//2*pic.height/2
                area = max(0,min(x+pic.width/2,x2)-max(x,x1))*max(0,min(y+pic.height/2,y2)-max(y,y1))
                layout[idx,int(cls)] = max(layout[idx,int(cls)],confidence*area/(pic.width*pic.height/4))
        return layout

    def read(self,pic):
        gate = threading.Barrier(4)
        def timed(op):
            gate.wait()
            start=time.perf_counter()
            block=op()
            return dict(block=block.astype(np.float32).tolist(),start=start,finish=time.perf_counter())
        with ThreadPoolExecutor(max_workers=4) as pool:
            tasks = [pool.submit(timed,fn) for fn in (lambda:self.detect(pic),lambda:self.recognize(pic,self.en),
                     lambda:self.recognize(pic,self.slavic),lambda:self.document(pic))]
            return [f.result() for f in tasks]


def reply(value):
    print("NEURO " + json.dumps(value, allow_nan=False),flush=True)


if __name__ == "__main__":
    reader = ScreenReader(Path(sys.argv[1]))
    reply({"ready":True})
    for line in sys.stdin:
        with Image.open(io.BytesIO(base64.b64decode(line.strip(),validate=True))) as pic:
            reply(reader.read(pic.convert("RGB")))
