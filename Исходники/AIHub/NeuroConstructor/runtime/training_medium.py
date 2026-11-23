"""One medium-profile comparison. Train/dev choose the link, test only measures it."""
import copy
import hashlib
import json
import math
import os
import tempfile
import time
from pathlib import Path

import torch
from torch.nn import functional as F
from PIL import Image

from lopata_neuro import layout
from lopata_neuro.archive import load_weights,unpack
from lopata_neuro.placement import devices
from lopata_neuro.medium.fusion import Link, build_bank
from lopata_neuro.medium.model import Medium
from lopata_neuro.medium.profiles import catalog
from training_data import dataset, NativeControl
from training_state import read_json,write,write_json


def sample(model,row):
    with Image.open(row["image"]) as pic:
        item,timing = model.extract(pic,row["text"],row["audio"],independent=True)
    blocks = [x.float().cpu() for x in item["blocks"]]
    return dict(blocks=blocks,native=torch.cat([F.normalize(b) for b in blocks],dim=1),text=item["text"].cpu(),
                words=torch.stack(item["words"]).cpu(),transcripts=item["transcripts"],slot=row["slot"]),timing


def fit(job,sets,rows,anchors,signature,checkpoint):
    torch.manual_seed(20261123)
    samples,dev = sets["train"],sets["dev"]
    bank = build_bank(samples,anchors)
    link = Link(bank)
    optimizer = torch.optim.Adam(link.parameters(),lr=.04)
    ids = [r["imageId"] for r in rows if r["split"]=="train"]
    blocked = torch.tensor([[a==b for b in ids] for a in ids])
    if blocked.all(1).any():
        raise ValueError("Нужны примеры хотя бы с двумя разными обучающими изображениями.")
    targets = torch.tensor([r["slot"] for r in samples])
    dev_targets = torch.tensor([r["slot"] for r in dev])
    epochs = int(job.run["cycles"])
    steps = math.ceil(len(samples)/8)
    job.total = epochs*steps
    best = None
    score = -1
    history = []
    if checkpoint:
        link.load_state_dict(checkpoint["link"])
        optimizer.load_state_dict(checkpoint["opt"])
        best,score,history = checkpoint["best"],checkpoint["score"],checkpoint["history"]
    job.stage_begin("Настройка связей")
    for epoch in range(job.step//steps,epochs):
        gen = torch.Generator().manual_seed(7102+epoch)
        batches = torch.randperm(len(samples),generator=gen).split(8)
        for batch in batches[job.step%steps:]:
            job.check()
            begin = time.perf_counter()
            optimizer.zero_grad()
            loss = F.cross_entropy(link.scores([samples[i] for i in batch],blocked[batch]),targets[batch])
            loss.backward()
            optimizer.step()
            job.training_seconds += time.perf_counter()-begin
            job.step += 1
            if job.step%steps==0:
                with torch.no_grad():
                    correct = int((link.scores(dev).argmax(1)==dev_targets).sum())
                history.append(dict(epoch=epoch+1,validationCorrect=correct,total=len(dev)))
                if correct>score:
                    best,score = copy.deepcopy(link.state_dict()),correct
            job.training_wall += time.perf_counter()-begin
            job.estimated_training = job.training_seconds/job.step*job.total
            job.remaining = job.training_seconds/job.step*(job.total-job.step)
            job.save(signature,dict(link=link.state_dict(),opt=optimizer.state_dict(),best=best,score=score,history=history))
            job.status()
    return dict(bank=bank,state=best),history


def run_job(job,model,rows,signature,checkpoint,inherited=None):
    folder,run = job.folder,job.run
    cache = folder/"medium-cache"/signature
    cache.mkdir(parents=True,exist_ok=True)
    sets = {"train":[],"dev":[]}
    material = [r for r in rows if r["split"]!="test"]
    start = time.monotonic()
    count = 0
    for i,r in enumerate(material):
        job.check()
        file = cache/f"sample-{i}.pt"
        if not file.is_file():
            item,_ = sample(model,r)
            write(file,lambda f:torch.save(item,f))
            count += 1
        sets[r["split"]].append(torch.load(file,weights_only=True,map_location="cpu"))
        job.progress("Подготовка примеров",i+1,len(material),start,count)
    # Native outputs have no trained connection. Use the same matching rule as for the light trio.
    control = NativeControl(sets["train"])
    cases = [r for r in rows if r["split"]=="test"]
    def measure(name,phase,fn):
        file = cache/(name+".json")
        results = read_json(file) if file.is_file() else []
        first,start = len(results),time.monotonic()
        for i in range(first,len(cases)):
            job.check()
            results.append(fn(cases[i]))
            write_json(file,results)
            job.progress(phase,i+1,len(cases),start,i+1-first)
        return results

    def independent(row):
        begin = time.perf_counter()
        item,starts = sample(model,row)
        answer = control.answer(item)
        return dict(slot=answer,seconds=time.perf_counter()-begin,starts=starts,transcripts=item["transcripts"])
    baseline = measure("independent","Проверка исходных моделей",independent)
    if run["evaluateOnly"]:
        source = Path(run["reusableWeights"]) if run.get("reusableWeights") else inherited
        if source is None:
            raise ValueError("Сначала настройте связи среднего набора.")
        product = load_weights(source/(model.kind+".pt"))
        history = []
    else:
        anchors = model.language.encode(["первое","first","второе","second"]).detach().cpu()
        product,history = fit(job,sets,rows,anchors,signature,checkpoint)
    model.set_link(product)
    weights = folder/"learned"
    write(weights/(model.kind+".pt"),lambda f:torch.save(product,f))
    def fused(row):
        begin = time.perf_counter()
        result = model.predict(row["image"],row["text"],row["audio"])
        result["seconds"] = time.perf_counter()-begin
        return result
    joined = measure("fused","Проверка результата",fused)
    rows_out = [dict(text=r["text"],image=r["image"],audio=r["audio"],slot=r["slot"],own=r["own"],baseline=b["slot"],
                predicted=f["slot"],route=f["route"],calls=f["calls"],baselineSeconds=b["seconds"],fusedSeconds=f["seconds"],
                transmitted=f["link_bytes"],transcripts=b["transcripts"]) for r,b,f in zip(cases,baseline,joined,strict=True)]
    # These inputs and labels come from the existing external SDK controls, never from model fitting.
    for suite in ("pair","trio"):
        controls = read_json(layout.REFERENCE/"checks"/(suite+".json"))
        for row in controls:
            job.check()
            base = layout.REFERENCE/"checks"
            row["expected"] = model.predict(base/row["image"],row["text"],[base/n for n in row["audio"]])["slot"]
        write_json(weights/(suite+".json"),controls)
    before = sum(r["baseline"]==r["slot"] for r in rows_out)
    after = sum(r["predicted"]==r["slot"] for r in rows_out)
    from training import peak_rss
    report = dict(runId=run["runId"],eligible=after>=before,total=len(cases),baselineCorrect=before,correct=after,
                  baselineSeconds=sum(b["seconds"] for b in baseline),fusedSeconds=sum(f["seconds"] for f in joined),
                  baselineMethod="nearest labelled native outputs; parallel inference",firstIndependentForwards=baseline[0]["starts"],
                  placement=model.placement,models=model.catalog["models"],visualExperts=model.visual_ids,
                  baselineCalls={k:len(cases)*(2 if k=="Qwen3-ASR" else 1) for k in ["MiniLM","Qwen3-ASR",*model.visual_ids]},
                  fusedCalls={k:len(cases) for k in ["MiniLM","Qwen3-ASR",*model.visual_ids]},rows=rows_out,history=history,
                  visualMix=model.link.expert_weights.detach().softmax(0).cpu().tolist(),
                  elapsedSeconds=job.elapsed,trainingWallSeconds=job.training_wall,trainingComputeSeconds=job.training_seconds,estimatedTrainingSeconds=job.estimated_training,
                  transferredBytes=sum(f["link_bytes"] for f in joined),peakRamBytes=peak_rss(),
                  peakVramBytes=sum(torch.cuda.max_memory_allocated(d) for d in set(model.devices.values()) if d.type=="cuda"),
                  optimizationSteps=job.step,evaluateOnly=run["evaluateOnly"],settings=model.settings,
                  examples=[dict(own=r["own"],split=r["split"],imageId=r["imageId"],audioIds=r["audioIds"]) for r in rows])
    write_json(folder/"report.json",report)
    job.remaining,job.stage=0,"Готово"
    job.status("completed")


def execute(job):
    run = job.run
    kind = run["composition"]
    pinned = catalog(kind)
    link_name = kind+".pt"
    rows,ids = dataset(run,job.folder,"trio")
    settings = layout.settings(dict(routing="input",**{k:run[k] for k in ("device",*layout.ROLE_DEVICES) if k in run}))
    runtime = Path(__file__).parent
    code = [layout.digest(f) for f in [Path(__file__),runtime/"training_data.py",
             *sorted((runtime/"lopata_neuro"/"medium").glob("*.py"))]]
    if run.get("reusableWeights"):
        code.append(layout.digest(Path(run["reusableWeights"])/link_name))
    if run.get("parentPackage"):
        from lopata_neuro.archive import inspect
        parent_info = inspect(run["parentPackage"])
        code.append(parent_info["files"]["weights/"+link_name]["sha256"])
    signature = hashlib.sha256(json.dumps([2,run,ids,pinned,code,str(torch.__version__)],sort_keys=True).encode()).hexdigest()
    checkpoint = job.restore(signature)
    job.stage_begin("Загрузка моделей")
    limit = int(run.get("memoryMiB") or 0)*1024**2
    targets = set(devices(settings,"trio").values())
    for target in targets:
        if limit and target.type=="cuda":
            torch.cuda.set_per_process_memory_fraction(min(1.,limit/torch.cuda.get_device_properties(target).total_memory),target)
    if limit and all(t.type=="cpu" for t in targets):
        from training import peak_rss
        def guard():
            if peak_rss()>limit:
                raise ValueError("Достигнут заданный предел оперативной памяти.")
        job.memory_guard = guard
    with tempfile.TemporaryDirectory(dir=job.folder) as temporary:
        source = layout.source_root(run.get("source"))
        inherited = None
        if run.get("parentPackage"):
            parent = Path(temporary)/"parent"
            prior = unpack(run["parentPackage"],parent)
            if prior["kind"]!=kind:
                raise ValueError("Нужна сборка того же состава.")
            source,inherited = parent/"models",parent/"weights"
        else:
            for entry in pinned["files"]:
                file = source/entry["repo"]/entry["file"]
                if (not file.is_file() or file.stat().st_size!=entry["size"] or
                    os.environ.get("LOPATA_NEURO_SOURCE_AUDITED")!="1" and layout.digest(file)!=entry["sha256"]):
                    raise ValueError(f'Требуется {entry["repo"]}/{entry["file"]} редакции {pinned["models"][entry["repo"]]["revision"]}')
        model = Medium(source,settings,weights=inherited,kind=kind)
        try:
            run_job(job,model,rows,signature,checkpoint,inherited)
        finally:
            model.close()
