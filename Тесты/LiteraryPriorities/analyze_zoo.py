"""Incremental factual markers and source-delivery audit, not a quality grader."""
import json
from pathlib import Path
import subprocess
import sys

base = Path(sys.argv[1]).resolve()
rows = []
for folder in sorted(base.iterdir()):
    run = folder / "run"
    if not (run / "summary.json").exists():
        continue
    result = subprocess.run([sys.executable, str(Path(__file__).with_name("analyze_novel.py")), str(run)], capture_output=True, text=True, encoding="utf-8")
    if result.returncode:
        print(folder.name, result.stderr)
        continue
    audit = json.loads((run / "audit.json").read_text(encoding="utf-8"))
    for variant in ["baseline", "short_numeric", "gated", "direct"]:
        items = [x for x in audit["table"] if x["variant"] == variant]
        if not items:
            continue
        rows.append(dict(model=folder.name, variant=variant, **{k: sum(x[k] for x in items) for k in
            ["total", "core_mentions", "source_delivered", "useful_source", "errors"]}))
(base / "audit.json").write_text(json.dumps(rows, ensure_ascii=False, indent=2), encoding="utf-8")
for row in rows:
    print(f'{row["model"]:16} {row["variant"]:14} n={row["total"]:2} source={row["source_delivered"]:2} useful={row["useful_source"]:2} core={row["core_mentions"]:2} errors={row["errors"]:2}')
