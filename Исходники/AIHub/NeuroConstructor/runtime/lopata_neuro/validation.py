"""Reproduce packaged checkpoints on independently recorded image and audio inputs."""
import json
from pathlib import Path

from . import layout
from .model import Model


def reference_rows(suite):
    return json.loads((layout.REFERENCE / "checks" / f"{suite}.json").read_text("utf-8"))


def _answers(model, root, suite):
    if model.kind not in ("medium","heavy"):
        model.reset_series()
    controls = root / "checks"
    saved = json.loads((controls / f"{suite}.json").read_text("utf-8"))
    canonical = reference_rows(suite)
    if len(saved) != len(canonical):
        raise ValueError("Неполный набор контрольных ответов")
    for row, original in zip(saved, canonical):
        if (any(row.get(k) != v for k, v in original.items() if k != "expected")
                or type(row.get("expected")) != int or row["expected"] not in range(4)):
            raise ValueError("Запись контрольного задания не соответствует формату")
        inputs = [original["image"], *original["audio"]]
        for name in inputs:
            if layout.digest(controls / name) != layout.digest(layout.REFERENCE / "checks" / name):
                raise ValueError(f"Изменён вход контрольного задания {name}")
        result = model.predict(controls / original["image"], original["text"],
                               [controls / name for name in original["audio"]])
        yield row, result


def capture(root):
    root = Path(root)
    model = Model(root)
    suites = ["pair", "trio"] if model.kind == "trio" else ["pair"]
    return {s: [dict(row, expected=answer["slot"]) for row, answer in _answers(model, root, s)] for s in suites}


def check(root, device="auto", suite=None):
    root = Path(root)
    if json.loads((root/"manifest.json").read_text("utf-8"))["kind"] in ("medium","heavy"):
        from . import from_directory
        model = from_directory(root,device)
        try:
            rows = [dict(text=r["text"],slot=r["slot"],predicted=a["slot"],expected=r["expected"],
                         matches_saved=r["expected"]==a["slot"],route=a["route"],calls=a["calls"])
                    for s in ([suite] if suite else ["pair","trio"]) for r,a in _answers(model,root,s)]
            return dict(total=len(rows),matched=sum(r["matches_saved"] for r in rows),
                        correct=sum(r["slot"]==r["predicted"] for r in rows),rows=rows,placement=model.placement)
        finally:
            model.close()
    model = Model(root, device)
    suite = suite or model.kind
    if suite not in ("trio", "pair") or suite == "trio" and model.kind != "trio":
        raise ValueError("Этот набор заданий недоступен для выбранной сборки")
    rows = []
    for row, answer in _answers(model, root, suite):
        rows.append(dict(text=row["text"], slot=row["slot"], predicted=answer["slot"], expected=row["expected"],
                         matches_saved=answer["slot"] == row["expected"], route=answer["route"],
                         elapsed_ms=answer["elapsed_ms"], link_bytes=answer["link_bytes"],
                         transfers=answer["transfers"], feedback_requested=answer["feedback_requested"],
                         calls=answer["calls"]))
    return dict(total=len(rows), matched=sum(row["matches_saved"] for row in rows),
                correct=sum(row["predicted"] == row["slot"] for row in rows), suite=suite,
                device=str(model.device), placement=model.placement, rows=rows)
