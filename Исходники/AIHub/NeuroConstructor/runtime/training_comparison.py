"""Run small same-input comparisons of routing and communication policies."""
from pathlib import Path
import time

from PIL import Image

from training_data import image_input
from training_state import read_json


def predict(model, row):
    started = time.perf_counter()
    frame = row["image"] if isinstance(row["image"],Image.Image) else image_input(row["image"])
    out = model.predict(frame, row["text"], row.get("audio", []))
    return dict(predicted=out["slot"],route=out["route"],calls=out["calls"],transfers=out["transfers"],
                feedbackRequested=out.get("feedback_requested",False),
                transmitted=out["link_bytes"],fusedSeconds=time.perf_counter()-started)


def totals(rows):
    directions = sorted({key for r in rows for key in r["transfers"]})
    return dict(calls={key:sum(r["calls"][key] for r in rows) for key in ("vision","language","audio")},
                transfers={direction:{unit:sum(r["transfers"].get(direction,{}).get(unit,0) for r in rows)
                           for unit in ("bytes","copies")} for direction in directions},
                routeErrors=sum(r["route"] != r["expectedRoute"] for r in rows))


def compare(model, cases, job):
    reference = Path(__file__).parent / "training_examples"
    direct = [dict(row, image=str(reference/row["image"]), audio=[], expectedRoute="pair")
              for row in read_json(reference/"pair.json") if row["split"]=="test"][:2]
    original = model.settings.copy()
    experiments = []

    def subsequent_frames(sequence):
        probes = []
        for row in sequence:
            second = image_input(row["image"])
            # A new frame of the same panel and command. The four targets stay put.
            second.putpixel((0,0),tuple(255-c for c in second.getpixel((0,0))))
            probes.extend((row,dict(row,image=second)))
        return probes

    def measure(name, inputs, **options):
        model.settings.update(original)
        model.settings.update(options)
        model.reset_series()
        rows = []
        for case in inputs:
            job.check()
            rows.append(dict(slot=case["slot"],expectedRoute=case["expectedRoute"],**predict(model,case)))
        experiments.append(dict(name=name,inputs=len(rows),correct=sum(r["slot"]==r["predicted"] for r in rows),
                                seconds=sum(r["fusedSeconds"] for r in rows),rows=rows,**totals(rows)))

    try:
        channel_probes = subsequent_frames(direct)
        for channel,send in [("continuous","always"),("byte","always"),("byte","changed")]:
            measure(channel+"-"+send, channel_probes, channel=channel, send=send, routing="input")
        if model.kind=="trio":
            spoken = [dict(row,expectedRoute="trio") for row in cases[:2]]
            repeats = subsequent_frames(spoken)
            for send in ("always","changed"):
                measure("speech-continuous-"+send,repeats,channel="continuous",send=send,routing="input")
            # A direct command can arrive while two buffers of unrelated speech are present.
            mixed = [dict(row,audio=spoken[0]["audio"]) for row in direct] + spoken
            for routing in ("input","always","learned"):
                measure("routing-"+routing,mixed,routing=routing)
    finally:
        model.settings.update(original)
        model.reset_series()
    return experiments
