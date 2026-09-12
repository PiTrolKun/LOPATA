"""Read-only delivery audit. Does not grade the models' prose or run inference."""
import hashlib
import json
from pathlib import Path
import sqlite3

HERE = Path(__file__).resolve().parent


def read(path):
    return json.loads(path.read_text(encoding="utf-8"))


def sha(text):
    return hashlib.sha256(text.encode("utf-8")).hexdigest()


def main():
    fixture = read(HERE / "fixture.json")
    retrieved = read(HERE / "rag-live/rag_results.json")
    expected_rag = [dict(query=retrieved[q]["query"], score=retrieved[q]["selected"][i]["score"],
                         **retrieved[q]["selected"][i]["fragment"])
                    for q, i in ((0, 0), (0, 2), (1, 1), (2, 0))]
    report = {}
    for model in sorted((HERE / "runs").iterdir()):
        if not model.is_dir():
            continue
        rows = []
        database_checks = []
        launch_path = model / (model.name + "-launch.json")
        command = read(launch_path)["command"] if launch_path.exists() else []
        server_context = int(command[command.index("-c") + 1]) if "-c" in command else None
        for series in sorted(model.glob("series-*")):
            for request_path in sorted(series.glob("*-request.json")):
                key = request_path.name.removesuffix("-request.json")
                packet_path = series / (key + "-packet.json")
                if not packet_path.exists():
                    continue
                request, packet = read(request_path), read(packet_path)
                response_path = series / (key + "-response.json")
                response = read(response_path)["response"] if response_path.exists() else None
                prompt_tokens = response.get("usage", {}).get("prompt_tokens") if response else None
                role = "advisor" if key in ("advice", "review") else "writer"
                checks = {}
                user = request["messages"][-1]["content"]
                expected_task = fixture["tasks"]["write3" if key == "control-no-memory" else key]
                checks["exact_packet_and_task_delivered"] = user == json.dumps(packet, ensure_ascii=False) + "\n\nПоследнее задание автора:\n" + expected_task
                checks["draft_hash_exact"] = packet["editorAnchor"]["sha256"] == sha(packet["workingDraft"])
                checks["draft_length_exact"] = packet["editorAnchor"]["characters"] == len(packet["workingDraft"]) <= 7500
                checks["draft_is_preliminary"] = packet["editorAnchor"]["state"] == "preliminary"
                checks["fixed_source_exact"] = packet["projectHistory"] == [dict(number="001", state="completed", sha256=sha(fixture["chapter"]), text=fixture["chapter"])]
                checks["anchor_exact"] = packet["anchor"] == fixture["anchor"]
                checks["history_excludes_old_context_packets"] = all('"editorAnchor"' not in message["content"] for message in request["messages"][1:-1])
                expected_history = {"advice": 0, "write1": 0, "write2": 2, "write3": 4, "control-no-memory": 4, "review": 2}[key]
                checks["history_count_exact"] = len(request["messages"]) - 2 == expected_history
                checks["output_reserve_within_role_budget"] = None if prompt_tokens is None else prompt_tokens + request["max_tokens"] <= (8192 if role == "writer" else 16384)
                if key == "control-no-memory":
                    checks["memory_and_reference_omitted"] = packet["jelly"] == packet["jellyRevisions"] == packet["referenceRag"] == []
                else:
                    checks["real_rag_receipts_exact"] = packet["referenceRag"] == expected_rag
                    active = {fact["id"]: fact for fact in packet["jelly"]}
                    changed = key in ("write3", "review")
                    checks["key_value_and_version_current"] = active["key"]["value"] == ("Томский" if changed else "Лизавета") and active["key"]["version"] == (2 if changed else 1)
                    checks["fixture_approval_marked"] = all(fact["status"] == "approved_by_stand_fixture" for fact in active.values())
                    checks["revision_separate"] = len(packet["jellyRevisions"]) == int(changed)
                    if changed:
                        revision = packet["jellyRevisions"][0]
                        checks["old_and_new_values_preserved"] = revision["previous"]["value"] == "Лизавета" and revision["current"]["value"] == "Томский"
                if key in ("write3", "control-no-memory"):
                    checks["manual_editor_replacement_exact"] = packet["workingDraft"] == fixture["corrected_editor"]
                if key == "review":
                    checks["review_sees_actual_write3"] = packet["workingDraft"] == fixture["corrected_editor"] + "\n\n" + (series / "write3-answer.txt").read_text(encoding="utf-8")
                rows.append(dict(series=series.name, key=key, role=role, response_complete=response is not None,
                                 prompt_tokens=prompt_tokens, output_reserved=request["max_tokens"], checks=checks))
            path = series / "память.lopata"
            if path.exists():
                with sqlite3.connect(path.as_uri() + "?mode=ro", uri=True) as db:
                    database_checks.append(dict(series=series.name, integrity=db.execute("PRAGMA integrity_check").fetchone()[0],
                                                facts=db.execute("SELECT count(*) FROM fact").fetchone()[0],
                                                revisions=db.execute("SELECT count(*) FROM revision").fetchone()[0]))
        failures = [dict(series=row["series"], key=row["key"], check=key) for row in rows for key, value in row["checks"].items() if value is False]
        report[model.name] = dict(server_context=server_context, requests=len(rows), completed=sum(row["response_complete"] for row in rows),
                                 failures=failures, database=database_checks, rows=rows)
    print(json.dumps(dict(models=report, limitations=[
        "Server capacity is 16384; writer limit 8192 is checked from actual prompt usage plus reserved output, not enforced pre-request.",
        "Combined control removes RAG and jelly together. It cannot isolate the contribution of each layer.",
        "Project history is a fixed synthetic source delivered directly. No completed user chapter existed to test project-vector retrieval.",
        "All facts and author approvals are predetermined stand fixtures. This run does not test automated extraction or a human approval UI.",
        "Packets are read and assembled by Python; models receive JSON text, not direct SQLite/file access.",
        "Later model comparisons follow each model's own generated history, so those contexts are intentionally not byte-identical."
    ]), ensure_ascii=False, indent=2))


if __name__ == "__main__":
    main()
