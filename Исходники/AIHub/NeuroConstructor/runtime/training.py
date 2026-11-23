"""Local Neuroconstructor worker. Run: python training.py --project PROJECT_DIRECTORY."""
import argparse
import contextlib
import json
import os
from pathlib import Path
import shutil
import sys
import time
import traceback

os.environ.setdefault("HF_HUB_OFFLINE", "1")
os.environ.setdefault("TRANSFORMERS_OFFLINE", "1")
os.environ.setdefault("HF_HUB_DISABLE_PROGRESS_BARS", "1")
os.environ.setdefault("TRANSFORMERS_VERBOSITY", "error")
sys.path.insert(0, str(Path(__file__).resolve().parent))
from training_state import Job, StopRequested, WorkerBusy, digest, exclusive, read_json, write, write_json


def peak_rss():
    if os.name == "nt":
        import ctypes
        from ctypes import wintypes
        class Counters(ctypes.Structure):
            _fields_ = [("cb",wintypes.DWORD), ("faults",wintypes.DWORD)] + [
                (name,ctypes.c_size_t) for name in ("peak","current","pp","cp","pn","cn","file","peakfile")]
        item = Counters()
        item.cb = ctypes.sizeof(item)
        if not ctypes.WinDLL("psapi").GetProcessMemoryInfo(ctypes.c_void_p(-1),ctypes.byref(item),item.cb):
            raise OSError("Не удалось измерить память процесса")
        return int(item.peak)
    import resource
    return resource.getrusage(resource.RUSAGE_SELF).ru_maxrss * (1 if sys.platform == "darwin" else 1024)


def settings(run):
    channel = run.get("channel") or "continuous"
    policy = run.get("audioPolicy") or "learned"
    return dict(channel={"byte":"byte","cached":"byte","continuous":"continuous","feedback":"feedback",
                         "cached-continuous":"continuous"}[channel],
                send="changed" if channel in ("cached","cached-continuous") else "always",
                routing={"needed":"input","input":"input","always":"always","learned":"learned"}[policy])


def reuse(model, folder, pair_only=False):
    from lopata_neuro.archive import load_weights
    from lopata_neuro.networks import Fusion
    if not folder.is_dir() or not (folder / "topology.json").is_file():
        raise ValueError("Не найдены проверенные веса предыдущего прогона.")
    if pair_only and not (folder / "pair.pt").is_file():
        raise ValueError("В предыдущем прогоне не найдены обученные веса пары.")
    for role in model.weights:
        if pair_only and role not in ("pair","byte","router","feedback"):
            continue
        candidate = folder / (role+".pt")
        if candidate.is_file():
            model.weights[role] = candidate
    pair = load_weights(model.weights["pair"])
    model.pair = Fusion(pair["channels"],pair["mode"],model.device,model.devices["language"]).eval()
    model.pair.load_state_dict(pair["weights"])
    model.byte.load_state_dict(load_weights(model.weights["byte"])["weights"])
    model.router.load_state_dict(load_weights(model.weights["router"])["state"])
    if not pair_only:
        model.topology = read_json(folder/"topology.json")["topology"]


def execute(job):
    import torch
    from lopata_neuro import layout
    from lopata_neuro.archive import unpack
    from lopata_neuro.model import Model
    from lopata_neuro.placement import accelerator, devices
    from training_comparison import compare, predict, totals
    from training_data import Samples, dataset, signature
    from training_links import Links, fit

    torch.manual_seed(2718)
    torch.set_num_threads(min(4, os.cpu_count() or 1))
    torch.backends.cudnn.benchmark = False
    run, folder = job.run, job.folder
    kind = run.get("composition") or "pair"
    if run.get("channel")=="cached-continuous" and kind!="trio":
        raise ValueError("Повторное использование голосовой подсказки доступно в сборке со слухом.")
    if run.get("channel")=="feedback" and (kind!="pair" or not run.get("evaluateOnly")):
        raise ValueError("Для запроса подробной подсказки выберите пару и «Сравнить без обучения».")
    parent = None
    job.stage_begin("Подготовка моделей")
    if run.get("parentPackage"):
        package = (folder / run["parentPackage"]).resolve()
        parent = folder / ("parent-" + digest(package))
        if not (parent / "manifest.json").is_file():
            unpack(package, parent)
    device = run.get("device") or "auto"
    if device == "auto":
        device = os.environ.get("LOPATA_NEURO_DEVICE") or device
    inherited = read_json(parent/"manifest.json").get("settings", {}) if parent else {}
    if device == "auto":
        device = inherited.get("device", "auto")
    if device == "auto":
        device = accelerator("auto")
    role_devices = {key: run[key] for key in layout.ROLE_DEVICES if run.get(key)}
    effective = layout.settings({**inherited, "device": device, **role_devices})
    execution_devices = set(devices(effective, kind).values())
    if int(run.get("memoryMiB") or 0) < 0:
        raise ValueError("Объём памяти должен быть положительным. Значение 0 снимает заданный предел.")
    root = (folder / run["source"]).resolve() if run.get("source") else None
    for execution_device in execution_devices:
        if execution_device.type == "cuda" and run.get("memoryMiB", 0):
            properties = torch.cuda.get_device_properties(execution_device)
            fraction = min(1., int(run["memoryMiB"]) * 1024**2 / properties.total_memory)
            torch.cuda.set_per_process_memory_fraction(fraction, execution_device)
    model = Model.from_source(root,kind,device=device,parent_root=parent,role_devices=role_devices)
    evaluate_only = bool(run.get("evaluateOnly",False))
    previous_pair = run.get("previousPairWeights")
    if previous_pair:
        reuse(model,(folder/previous_pair).resolve(),pair_only=True)
    if evaluate_only and run.get("reusableWeights"):
        reuse(model,(folder/run["reusableWeights"]).resolve())
    if kind == "trio":
        model.prepare_audio()
    model.settings.update(settings(run))
    memory_limit = int(run.get("memoryMiB") or 0) * 1024**2
    if memory_limit and all(target.type == "cpu" for target in execution_devices):
        def guard():
            used = peak_rss()
            if used > memory_limit:
                raise ValueError(f"Процесс занял {used//1024**2} МиБ оперативной памяти. Увеличьте заданный предел.")
        job.memory_guard = guard
    job.stage_begin("Проверка учебных и контрольных примеров")
    rows, ids = dataset(run, folder, kind)
    key = signature(run, ids, model, device)
    checkpoint = job.restore(key)
    if checkpoint is None:
        job.save(key, {})
    cache = folder / "features" / key
    samples = Samples(model, rows, job)
    try:
        job.stage_begin("Подготовка примеров")
        sets = samples.prepare(cache)
        job.stage_begin("Одновременный запуск исходных моделей и проверка ответов")
        baseline = samples.baseline(sets["train"], cache)
    finally:
        samples.pool.shutdown()
    if evaluate_only:
        mode = model.pair.mode if kind == "pair" else model.topology
        requested = run.get("connection")
        if kind=="trio" and requested=="inside":
            requested = "early"
        if requested and requested not in (mode,"auto"):
            raise ValueError("Для выбранной новой точки связи требуется обучение.")
        links = Links(model,kind,mode,run.get("channel","continuous"),train=False)
        history = []
    else:
        links, history = fit(model, sets, run, job, checkpoint, key)
    job.stage_begin("Проверка обученной связи")
    controls = {name:links.score(sets["test"],name)[0] for name in ("disabled","shuffled")}
    if kind == "trio":
        model.trio, model.topology = links.net.eval(), links.mode
    else:
        model.pair = links.net.eval()
        if links.codebook is not None:
            model.byte = links.codebook.eval()
    model.reset_series()
    results = []
    cases = [r for r in rows if r["split"]=="test"]
    if model.settings["channel"] == "feedback":
        votes = []
        for row,other in zip(cases,cases[1:]+cases[:1]):
            job.check()
            votes.append(predict(model,dict(row,text=other["text"]))["predicted"] == row["slot"])
        controls["shuffled"] = sum(votes)
        model.reset_series()
    for row,(native,seconds) in zip(cases, baseline, strict=True):
        job.check()
        results.append(dict(text=row["text"], image=row["image"], audio=row["audio"],slot=row["slot"],
                            own=row["own"],baseline=native,expectedRoute=kind, baselineSeconds=seconds,**predict(model,row)))
    score = sum(row["slot"] == row["predicted"] for row in results)
    base_score = sum(row["slot"] == row["baseline"] for row in results)
    job.stage_begin("Сравнение способов передачи подсказки и выбора моделей")
    experiments = compare(model,cases,job)
    job.stage_begin("Сохранение результата")
    weights = folder / "learned"
    for name,path in model.weights.items():
        if evaluate_only or previous_pair and name in ("pair","byte","router","feedback"):
            with path.open("rb") as src:
                write(weights / (name+".pt"), lambda stream:shutil.copyfileobj(src,stream))
    if not evaluate_only:
        for name, product in links.product().items():
            write(weights / name, lambda stream:torch.save(product,stream))
    write_json(weights / "topology.json", {"topology":model.topology})
    fused_seconds = sum(r["fusedSeconds"] for r in results)
    baseline_seconds = sum(r["baselineSeconds"] for r in results)
    report = dict(runId=run.get("runId"), correct=score, total=len(results), baselineCorrect=base_score,
                  eligible=score >= base_score, baselineMethod="nearest labelled native outputs; parallel inference",
                  firstIndependentForwards=samples.first_forwards,
                  baselineCalls=dict(language=len(cases),vision=len(cases),audio=len(cases)*2 if kind=="trio" else 0),
                  correctPerSecond=score / fused_seconds, baselineCorrectPerSecond=base_score / baseline_seconds,
                  baselineSeconds=baseline_seconds, fusedSeconds=fused_seconds, controls=controls,
                  peakRamBytes=peak_rss(), device=str(model.device), placement=model.placement, history=history, connection=links.mode,
                  peakVramBytes=sum(getattr(torch, target.type).max_memory_allocated(target) for target in execution_devices
                                    if target.type in ("cuda", "xpu")),
                  transferredBytes=sum(r["transmitted"] for r in results), outputBytes=len(results)*4 if model.device.type!="cpu" else 0,
                  rows=results, elapsedSeconds=job.elapsed, settings=model.settings,evaluateOnly=evaluate_only,
                  optimizationSteps=job.step,estimatedTrainingSeconds=job.estimated_training,
                  trainingComputeSeconds=job.training_seconds, trainingWallSeconds=job.training_wall,
                  policyComparisons=experiments,**totals(results),
                  examples=[dict(own=r["own"],split=r["split"],imageId=r["imageId"],audioIds=r["audioIds"]) for r in rows])
    write_json(folder / "report.json",report)
    job.remaining = 0
    job.stage = "Готово"
    job.status("completed")


def optional_record(path):
    try:
        record = read_json(path)
    except (FileNotFoundError, UnicodeDecodeError, json.JSONDecodeError):
        return {}
    return record if isinstance(record, dict) else {}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--project",required=True,type=Path)
    project = parser.parse_args().project.resolve()
    with contextlib.ExitStack() as cleanup:
        try:
            cleanup.enter_context(exclusive(project))
        except WorkerBusy:
            return 73
        job = Job(project, read_json(project/"run.json"))
        try:
            prior = optional_record(project/"status.json")
            if prior.get("runId")==job.run.get("runId"):
                if prior.get("status")=="canceled":
                    return 0
                if prior.get("status")=="completed":
                    report = optional_record(project/"report.json")
                    weights = project/"learned"/(job.run.get("composition","pair")+".pt")
                    if type(report.get("eligible")) is bool and weights.is_file():
                        return 0
                if prior.get("status")=="paused" and (project/"pause.request").exists() and not (project/"cancel.request").exists():
                    return 0
            # Only a verified checkpoint can supply an optimizer step and matching weights.
            job.check()
            job.status()
            if job.run.get("composition") in ("medium","heavy"):
                from training_medium import execute as execute_medium
                execute_medium(job)
            else:
                execute(job)
        except StopRequested as stopped:
            job.remaining = None
            job.stage = "На паузе" if stopped.status == "paused" else "Отменено"
            job.status(stopped.status)
        except Exception as exc:
            job.stage = "Не удалось завершить операцию"
            job.remaining = None
            job.status("failed",str(exc))
            traceback.print_exc()
            return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
