import base64
import io
import json
import os
import subprocess
import sys
import threading
import time
from concurrent.futures import ThreadPoolExecutor
from pathlib import Path

import torch
from PIL import Image

from .. import layout
from ..archive import load_weights
from ..model import Language, POSITIONS, pixels, prepare_image, waveform
from ..networks import Cut, Fusion
from ..placement import Traffic, devices
from .experts import Hearing, Vision
from .fusion import Link, ordinal
from .profiles import FAMILIES,catalog,visual_ids


class Paddle:
    def __init__(self, root):
        command = [os.environ.get("LOPATA_PADDLE_PYTHON") or sys.executable,"-B","-s","-X","utf8","-u",
                   str(Path(__file__).with_name("paddle_worker.py")),str(root/"PaddlePaddle")]
        self.process = subprocess.Popen(command,stdin=subprocess.PIPE,stdout=subprocess.PIPE,
                           text=True,encoding="utf-8",creationflags=getattr(subprocess,"CREATE_NO_WINDOW",0))
        try:
            if self.reply() != {"ready":True}:
                raise RuntimeError("Paddle Inference did not initialize")
        except BaseException:
            self.close()
            raise

    def reply(self):
        for line in self.process.stdout:
            if line.startswith("NEURO "):
                return json.loads(line[6:])
        raise RuntimeError(f"Paddle Inference exited ({self.process.wait()})")

    def read(self,pic):
        buf = io.BytesIO()
        pic.save(buf,"PNG")
        self.process.stdin.write(base64.b64encode(buf.getvalue()).decode("ascii")+"\n")
        self.process.stdin.flush()
        return self.reply()

    def close(self):
        self.process.stdin.close()
        try:
            self.process.wait(10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.process.stdout.close()


class Medium:
    def __init__(self,root,settings,device="auto",weights=None,kind="medium"):
        self.kind = kind
        self.catalog = catalog(kind)
        self.visual_ids = visual_ids(kind)
        self.settings = settings
        self.devices = devices(settings,"trio",device)
        self.device = self.devices["vision"]
        self.placement = {k:str(v) for k,v in self.devices.items()}
        self.placement["ocr"] = "cpu"
        torch.set_num_threads(min(4,os.cpu_count() or 1))
        torch._C._set_graph_executor_optimize(False)
        torch.backends.cudnn.allow_tf32 = torch.backends.cuda.matmul.allow_tf32 = False
        detector = next(f for f in self.catalog["files"] if f["file"]=="icon_detect_v3/model.pt")
        layout.checked_component(root/detector["repo"]/detector["file"],detector)
        self.language = Language(root/"sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2",self.devices["language"])
        self.hearing = Hearing(root/FAMILIES[kind][0],self.devices["speech"])
        self.vision = Vision(root,self.device,FAMILIES[kind][1])
        self.cut = Cut(self.vision.detector)
        pair = load_weights((weights if weights else layout.REFERENCE/"weights")/"pair.pt")
        self.pair = Fusion(pair["channels"],pair["mode"],self.device,self.devices["language"]).eval()
        self.pair.load_state_dict(pair["weights"])
        self.link = None
        if weights:
            self.set_link(load_weights(weights/(kind+".pt")))
        self.paddle = Paddle(root)
        self.pool = ThreadPoolExecutor(max_workers=7)
        self.lock = threading.RLock()

    def set_link(self,record):
        self.link = Link(record["bank"])
        self.link.load_state_dict(record["state"],strict=True)
        self.anchors = self.link.anchors.to(self.devices["language"])
        self.link = self.link.eval().to(self.device)

    def close(self):
        if self.paddle is None:
            return
        self.pool.shutdown(wait=True)
        self.paddle.close()
        self.paddle = self.vision = self.cut = self.pair = self.link = self.hearing = self.language = None

    def extract(self, image, text, audio, independent=False, selected=None):
        pic = prepare_image(image)
        chips = [pic.crop((x*128,y*128,(x+1)*128,(y+1)*128)) for y in range(2) for x in range(2)]
        ids = self.visual_ids
        calls = {ids[0]:lambda:self.vision.detect(pic),ids[1]:lambda:self.vision.describe(chips),
                 ids[2]:lambda:self.vision.multimodal(chips,0),ids[3]:lambda:self.vision.multimodal(chips,1),
                 "ocr":lambda:self.paddle.read(pic)}
        if selected is None:
            calls["language"] = lambda:self.language.encode([text])
        if audio:
            calls["speech"] = lambda:[self.hearing.read(waveform(path),independent) for path in audio]
        gate = threading.Barrier(len(calls))
        start = time.perf_counter()
        def invoke(fn):
            gate.wait()
            begin = time.perf_counter()-start
            with torch.inference_mode():
                val = fn()
                return val,{"startSeconds":begin,"finishedSeconds":time.perf_counter()-start}
        jobs = {k:self.pool.submit(invoke,v) for k,v in calls.items()}
        done = {k:f.result() for k,f in jobs.items()}
        output = {k:v[0] for k,v in done.items()}
        timings = {k:v[1] for k,v in done.items() if k!="ocr"}
        timings.update({k:dict(startSeconds=data["start"]-start,finishedSeconds=data["finish"]-start)
                        for k,data in zip(ids[4:],output["ocr"],strict=True)})
        result = dict(blocks=[output[k] for k in ids[:4]]+[torch.tensor(d["block"]) for d in output["ocr"]],text=output.get("language"),
                      transcripts=[v[1] for v in output.get("speech",[])],
                      words=[v[0][0] for v in output.get("speech",[])])
        return result,timings

    @torch.inference_mode()
    def predict(self,image,text,audio=()):
        if not isinstance(text,str) or not text.strip() or len(audio) not in (0,2):
            raise ValueError("Нужны команда, изображение и, при голосовом запросе, две записи WAV.")
        if not isinstance(image,Image.Image):
            with Image.open(image) as pic:
                return self.predict(pic,text,audio)
        traffic = Traffic()
        start = time.perf_counter()
        with self.lock:
            command = self.language.encode([text])
            if audio:
                index = int(traffic.move(ordinal(command,self.anchors),torch.device("cpu"),link=False).item())
                data,_ = self.extract(image,text,[audio[index]],selected=index)
                blocks = [traffic.move(block,self.device)[None] for block in data["blocks"]]
                voice = traffic.move(data["words"][0][None],self.device)
                scores = self.link(blocks,voice)
            else:
                base = self.cut.features(pixels(prepare_image(image)).to(self.device))
                cue = traffic.move(self.pair.message(command),self.device)
                scores = self.pair.decide(base,cue,self.cut)
            slot = int(traffic.move(scores.argmax(),torch.device("cpu"),link=False).item())
        return dict(slot=slot,position=POSITIONS[slot],route=self.kind if audio else "pair",placement=self.placement,device=str(self.device),
                    elapsed_ms=(time.perf_counter()-start)*1000,link_bytes=traffic.link_bytes,
                    transfers=traffic.counts,feedback_requested=False,
                    calls=dict(language=1,vision=len(self.visual_ids) if audio else 1,audio=int(bool(audio))))
