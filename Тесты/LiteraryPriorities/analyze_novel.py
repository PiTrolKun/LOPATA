"""Mechanical checks for the fixed synthetic-source probe; read answers as well."""
import hashlib
import json
import pathlib
import sys
from collections import defaultdict

root = pathlib.Path(sys.argv[1])
rows = json.loads((root / "summary.json").read_text(encoding="utf-8-sig"))
truth = json.loads((root / "ground-truth.json").read_text(encoding="utf-8-sig"))
texts = [(p, p.read_text(encoding="utf-8-sig")) for p in sorted((root / "Project/Materials").glob("*.txt"))]
assert hashlib.sha256("\n\n".join(t for _, t in texts).encode()).hexdigest().upper() == truth["sourceSha256"]

def fragments(node):
    if isinstance(node, dict):
        if node.get("kind") == "reference" and "text" in node:
            yield node["text"]
        for value in node.values():
            yield from fragments(value)
    elif isinstance(node, list):
        for value in node:
            yield from fragments(value)

table = defaultdict(lambda: dict(total=0, core_mentions=0, source_delivered=0, useful_source=0, errors=0))
detail = []
for row in rows:
    saved = json.loads((root / f'{row["test"]}-{row["variant"]}-{row["repeat"]}.json').read_text(encoding="utf-8-sig"))
    assert row == saved["record"], "Summary does not match per-request evidence"
    answer = row["result"].lower().replace("ё", "е")
    required = ["кевар", "смотрител", "солян"] if row["test"] != "novel_objects" else ["медн", "бел", "черн"]
    core = all(term in answer for term in required)
    if row["test"] == "novel_change":
        core = core and "учител" in answer and "вейран" in answer
    final = next((t["messages"] for t in saved["trace"] if t["kind"] == "final_context"), None)
    raw = final[-1]["Content"] if final else ""
    if not final:
        source = ""
    elif row["variant"] == "direct":
        source = "\n\n".join(t for _, t in texts)
    else:
        start, end = raw.index("{"), raw.rindex("\n\nЗадание автора:")
        source = "\n".join(fragments(json.loads(raw[start:end])))
    useful = all(term in source.lower().replace("ё", "е") for term in required)
    item = table[(row["test"], row["variant"])]
    item["total"] += 1
    item["core_mentions"] += core
    item["source_delivered"] += row["referenceDelivered"]
    item["useful_source"] += useful
    item["errors"] += bool(row["failure"])
    detail.append(dict(test=row["test"], variant=row["variant"], repeat=row["repeat"], core_mentions=core,
                       useful_source=useful, source_chars=len(source), answer=row["result"]))
result = {"rows": len(rows), "source_characters": sum(len(t) for _, t in texts),
          "preflight": json.loads((root / "preflight.json").read_text(encoding="utf-8-sig")),
          "table": [{"test": k[0], "variant": k[1], **v} for k, v in sorted(table.items())], "detail": detail}
(root / "audit.json").write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding="utf-8")
print(json.dumps({k: v for k, v in result.items() if k not in ("detail", "preflight")}, ensure_ascii=False, indent=2))
