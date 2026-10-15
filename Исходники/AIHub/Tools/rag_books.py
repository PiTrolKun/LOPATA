"""Offline book preparation. Supported Transformers encoder; durable SQLite checkpoints.

Original passages are reference data, never executable instructions or model rewrites.
"""
import argparse
import array
from contextlib import closing
import hashlib
import json
import math
import os
from pathlib import Path
import re
import sqlite3
import sys
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from runtime_hardware import (select_torch_device, system_memory, cpu_model_budget,
    torch_inference_dtype, torch_is_out_of_memory, require_supported_torch)
from rag_book_load import EmbeddingLoad

PROTOCOL = "book-exact-mean-l2-384-v1"
REVISION = "b71168088212f0a13688514e4fc288a86106c4e4"
DIMENSIONS = 2048


def emit(stage, done=0, total=0, **data):
    print(json.dumps(dict(stage=stage, done=done, total=total, **data), ensure_ascii=False), flush=True)


def digest(path):
    h = hashlib.sha256()
    with open(path, "rb") as stream:
        while block := stream.read(1024 * 1024):
            h.update(block)
    return h.hexdigest()


def atomic(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + "." + uuid.uuid4().hex + ".tmp")
    try:
        with temporary.open("w", encoding="utf-8", newline="\n") as stream:
            json.dump(value, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def read(path):
    with open(path, encoding="utf-8-sig") as stream:
        return json.load(stream)


def reasons(text):
    result = []
    if not text.strip():
        result.append("empty_content")
    if "\ufffd" in text or any(ord(c) < 32 and c not in "\t\n\r" for c in text):
        result.append("invalid_characters")
    if "°" in text or re.search(r"(?:[—_∪˘]\s*){4,}", text):
        result.append("unresolved_metric_layout")
    if text.strip() and sum(c.isalpha() for c in text) < 5:
        result.append("insufficient_language_content")
    return result


def spans(text, tokenizer):
    """Paragraphs keep examples together; oversized units split at sentences/token offsets without truncation."""
    paragraphs = list(re.finditer(r"\S[\s\S]*?(?=\n\s*\n|\Z)", text))
    if not paragraphs:
        return
    start = paragraphs[0].start()
    end = start
    for paragraph in paragraphs:
        if end > start and len(tokenizer(text[start:paragraph.end()])["input_ids"]) > 384:
            yield from bounded(text, start, end, tokenizer)
            start = paragraph.start()
        end = paragraph.end()
    yield from bounded(text, start, end, tokenizer)


def bounded(text, start, end, tokenizer):
    while start < end:
        offsets = tokenizer(text[start:end], add_special_tokens=False, return_offsets_mapping=True)["offset_mapping"]
        if len(offsets) <= 370:
            yield start, end
            return
        cut = offsets[369][1]
        sentences = list(re.finditer(r"[.!?;]\s+", text[start:start + cut]))
        if sentences and sentences[-1].end() > cut // 2:
            cut = sentences[-1].end()
        if cut <= 0:
            raise ValueError("Cannot split token span")
        yield start, start + cut
        start += cut


def connect(path):
    db = sqlite3.connect(path)
    db.execute("PRAGMA foreign_keys=ON")
    db.execute("PRAGMA synchronous=FULL")
    db.executescript("""
      CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY,name TEXT NOT NULL,sha256 TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS pages(id TEXT PRIMARY KEY,sha256 TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS chunks(id TEXT PRIMARY KEY,source_id TEXT REFERENCES sources(id),page INTEGER,
        start INTEGER,end INTEGER,text TEXT NOT NULL,vector BLOB);
      CREATE TABLE IF NOT EXISTS exclusions(id TEXT PRIMARY KEY,source_id TEXT,page INTEGER,
        characters INTEGER,reasons TEXT NOT NULL);
    """)
    return db


def valid_vector(values):
    return len(values) == DIMENSIONS and all(math.isfinite(x) for x in values) and abs(sum(x*x for x in values) - 1) < .005


def prepare(db, request, tokenizer):
    if request["revision"] != REVISION:
        raise ValueError("Unsupported embedding revision")
    identity = hashlib.sha256(json.dumps([PROTOCOL, REVISION, [(s["Id"], s["Sha256"], s["Warnings"]) for s in request["sources"]]], sort_keys=True).encode()).hexdigest()
    previous = db.execute("SELECT value FROM meta WHERE key='identity'").fetchone()
    if previous and previous[0] != identity:
        raise ValueError("Checkpoint identity changed")
    with db:
        db.execute("INSERT OR IGNORE INTO meta VALUES('identity',?)", (identity,))
    for source in request["sources"]:
        if digest(source["Snapshot"]) != source["Sha256"]:
            raise ValueError("Source snapshot checksum mismatch")
        with db:
            db.execute("INSERT OR IGNORE INTO sources VALUES(?,?,?)", (source["Id"], source["Name"], source["Sha256"]))
            if source["Warnings"]:
                db.execute("INSERT OR REPLACE INTO exclusions VALUES(?,?,?,?,?)", (source["Id"], source["Id"], 0, 0, json.dumps(source["Warnings"])))
        for number, path in enumerate(source["Pages"], 1):
            page = read(path)
            page_id = source["Id"] + ":" + str(page["Page"])
            checksum = digest(path)
            saved = db.execute("SELECT sha256 FROM pages WHERE id=?", (page_id,)).fetchone()
            if saved:
                if saved[0] != checksum:
                    raise ValueError("Completed page checksum changed")
                continue
            text = page["Text"]
            with db:
                if page["Flags"]:
                    db.execute("INSERT INTO exclusions VALUES(?,?,?,?,?)", (page_id, source["Id"], page["Page"], len(text), json.dumps(page["Flags"])))
                else:
                    for start, end in spans(text, tokenizer):
                        value = text[start:end]
                        ident = page_id + ":" + str(start)
                        flags = reasons(value)
                        if flags:
                            db.execute("INSERT INTO exclusions VALUES(?,?,?,?,?)", (ident, source["Id"], page["Page"], len(value), json.dumps(flags)))
                        else:
                            if len(tokenizer(value)["input_ids"]) > 384:
                                raise ValueError("Unbounded source passage")
                            db.execute("INSERT INTO chunks VALUES(?,?,?,?,?,?,NULL)", (ident, source["Id"], page["Page"], start, end, value))
                db.execute("INSERT INTO pages VALUES(?,?)", (page_id, checksum))
            emit("Split", number, len(source["Pages"]))
    return identity


def encoder(model_dir, requested):
    import torch
    from transformers import AutoModel
    require_supported_torch(torch)
    torch.set_num_threads(max(1, min(4, (os.cpu_count() or 2) // 2)))
    device, inventory, why = select_torch_device(torch, requested)
    if device != "cpu" and inventory[0] < 9 * 1024**3:
        device = "cpu"
        why = "Insufficient free GPU memory for 3B encoder; CPU fallback"
    if device == "cpu":
        total, available = system_memory()
        if available < cpu_model_budget(model_dir, total):
            raise RuntimeError("Insufficient physical RAM for 3B embeddings")
    emit("Loading", device=device, reason=why)
    try:
        model = AutoModel.from_pretrained(model_dir, trust_remote_code=True, local_files_only=True,
            dtype=torch_inference_dtype(torch, device), attn_implementation="sdpa").to(device).eval()
        if model.config.hidden_size != DIMENSIONS or model.config.is_causal:
            raise ValueError("Expected bidirectional 2048-dimensional Giga encoder")
        return torch, model, device
    except Exception as error:
        if device != "cpu" and torch_is_out_of_memory(torch, error):
            raise RuntimeError("RAG_BOOK_GPU_OOM") from error
        raise


def encode(torch, model, device, tokenizer, texts):
    try:
        with torch.inference_mode():
            batch = tokenizer(texts, padding=True, truncation=False, return_tensors="pt").to(device)
            hidden = model(**batch).last_hidden_state
            mask = batch["attention_mask"].unsqueeze(-1).to(hidden.dtype)
            vectors = torch.nn.functional.normalize((hidden * mask).sum(1).float() / mask.sum(1).float().clamp_min(1), p=2, dim=1)
            values = vectors.cpu().tolist()
            if not all(valid_vector(v) for v in values):
                raise ValueError("Invalid embedding vector")
            return values
    except Exception as error:
        if device != "cpu" and torch_is_out_of_memory(torch, error):
            raise RuntimeError("RAG_BOOK_GPU_OOM") from error
        raise


def publish(db, database, library, request):
    count = db.execute("SELECT count(*) FROM chunks").fetchone()[0]
    if not count:
        raise ValueError("No admissible text. Previous ready library is preserved.")
    if db.execute("PRAGMA integrity_check").fetchone()[0] != "ok" or db.execute("PRAGMA foreign_key_check").fetchall():
        raise ValueError("Library integrity check failed")
    for text, start, end, blob in db.execute("SELECT text,start,end,vector FROM chunks"):
        values = array.array("f")
        if blob is None:
            raise ValueError("Incomplete embedding checkpoint")
        values.frombytes(blob)
        if not valid_vector(values) or end - start != len(text):
            raise ValueError("Invalid published passage")
    excluded = db.execute("SELECT count(*),coalesce(sum(characters),0) FROM exclusions").fetchone()
    exclusions = [dict(id=row[0], source=row[1], page=row[2], characters=row[3], reasons=json.loads(row[4]))
        for row in db.execute("SELECT id,source_id,page,characters,reasons FROM exclusions")]
    counts = {}
    for unit in exclusions:
        for reason in unit["reasons"]:
            key = reason.split(":", 1)[0]
            counts[key] = counts.get(key, 0) + 1
    atomic(Path(library) / "work" / request["Id"] / "quality.json", dict(exclusions=exclusions, reason_counts=counts))
    versions = Path(library) / "versions"
    versions.mkdir(parents=True, exist_ok=True)
    version = request["Id"]
    final = versions / version
    if final.exists():
        manifest = read(final / "manifest.json")
        if manifest["database_sha256"] != digest(final / "library.sqlite") or manifest["protocol"] != PROTOCOL or manifest["revision"] != REVISION:
            raise ValueError("Published version integrity mismatch")
        with closing(sqlite3.connect((final / "library.sqlite").as_uri() + "?mode=ro", uri=True)) as ready_db:
            if ready_db.execute("SELECT value FROM meta WHERE key='identity'").fetchone() != db.execute("SELECT value FROM meta WHERE key='identity'").fetchone():
                raise ValueError("Published version source identity mismatch")
        atomic(Path(library) / "current.json", dict(version=version, manifest_sha256=digest(final / "manifest.json")))
        emit("Ready", count, count, excluded=excluded[0])
        return
    candidate = versions / (version + ".building")
    candidate.mkdir(exist_ok=True)
    # Publish only accepted sources, passages and vectors. Exclusion details remain in work diagnostics.
    ready = candidate / "library.sqlite"
    with closing(sqlite3.connect(ready)) as output:
        db.backup(output)
        output.execute("DROP TABLE exclusions")
        output.execute("DROP TABLE pages")
        output.commit()
        output.execute("VACUUM")
    with ready.open("rb+") as stream:
        os.fsync(stream.fileno())
    manifest = dict(schema=1, version=version, name=request["Name"], model="model.giga-embeddings-3b", revision=REVISION,
        protocol=PROTOCOL, dimensions=DIMENSIONS, offset_unit="unicode_codepoint", chunks=count, excluded_units=excluded[0], excluded_characters=excluded[1],
        exclusion_reasons=counts,
        database_sha256=digest(ready), private=True, shipping_corpus=False,
        limitations=["Heuristic quality checks are not proof of flawless OCR or source truth.",
                      "Tesseract OCR pages with a word confidence below 85 are excluded as a whole.",
                      "Paragraph structure is preserved; dictionary-specific entry structure is not inferred."])
    atomic(candidate / "manifest.json", manifest)
    notice = "Private user library. Source rights are not automatically cleared for redistribution.\n" + "\n".join(
        s["Name"] + " | SHA256 " + s["Sha256"] for s in request["sources"])
    with (candidate / "SOURCE_NOTICE.txt").open("w", encoding="utf-8") as stream:
        stream.write(notice)
        stream.flush()
        os.fsync(stream.fileno())
    candidate.rename(final)
    atomic(Path(library) / "current.json", dict(version=version, manifest_sha256=digest(final / "manifest.json")))
    emit("Ready", count, count, excluded=excluded[0])


def current(library):
    pointer = read(Path(library) / "current.json")
    if not re.fullmatch("[0-9a-f]{32}", pointer["version"]):
        raise ValueError("Invalid library version")
    folder = Path(library) / "versions" / pointer["version"]
    if digest(folder / "manifest.json") != pointer["manifest_sha256"]:
        raise ValueError("Manifest checksum mismatch")
    manifest = read(folder / "manifest.json")
    if manifest["revision"] != REVISION or manifest["protocol"] != PROTOCOL or manifest["dimensions"] != DIMENSIONS:
        raise ValueError("Incompatible embedding index")
    if digest(folder / "library.sqlite") != manifest["database_sha256"]:
        raise ValueError("Database checksum mismatch")
    return folder, manifest


def reuse_vectors(db, library):
    if not (Path(library) / "current.json").exists():
        return 0
    # Integrity must be checked before old vectors are reused, just as for retrieval.
    folder, _ = current(library)
    reused = 0
    with closing(sqlite3.connect((folder / "library.sqlite").as_uri() + "?mode=ro", uri=True)) as old:
        with db:
            for ident, text, blob in old.execute("SELECT id,text,vector FROM chunks"):
                values = array.array("f"); values.frombytes(blob)
                if not valid_vector(values):
                    raise ValueError("Invalid reusable vector")
                reused += db.execute("UPDATE chunks SET vector=? WHERE id=? AND text=? AND vector IS NULL", (blob, ident, text)).rowcount
    return reused


def run(args):
    from transformers import AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True, local_files_only=True)
    started = time.monotonic()
    if args.query:
        folder, manifest = current(args.library)
        query = read(args.input)["query"]
        value = "Instruct: Given a query, retrieve relevant passages\nQuery: " + query
        if len(tokenizer(value)["input_ids"]) > 512:
            raise ValueError("Query exceeds context")
        torch, model, device = encoder(args.model, args.device)
        vector = encode(torch, model, device, tokenizer, [value])[0]
        hits = []
        with closing(sqlite3.connect((folder / "library.sqlite").as_uri() + "?mode=ro", uri=True)) as db:
            for ident, name, page, start, end, text, blob in db.execute("SELECT c.id,s.name,c.page,c.start,c.end,c.text,c.vector FROM chunks c JOIN sources s ON s.id=c.source_id"):
                values = array.array("f"); values.frombytes(blob)
                if not valid_vector(values):
                    raise ValueError("Invalid search vector")
                hit = dict(id=ident, source=name, page=page, start=start, end=end, text=text, score=sum(a*b for a,b in zip(values, vector)),
                    exact=query.strip().casefold() in text.casefold())
                hits.append(hit)
                hits.sort(key=lambda h: (h["exact"], h["score"]), reverse=True)
                del hits[5:]
        atomic(Path(args.work) / "hits.json", dict(version=manifest["version"], hits=hits, seconds=time.monotonic()-started))
        emit("Ready", len(hits), len(hits), device=device)
        return
    database = Path(args.work) / "checkpoint.sqlite"
    request = read(args.input)
    with closing(connect(database)) as db:
        prepare(db, request, tokenizer)
        quality = [dict(id=r[0], source=r[1], page=r[2], characters=r[3], reasons=json.loads(r[4]))
            for r in db.execute("SELECT id,source_id,page,characters,reasons FROM exclusions")]
        atomic(Path(args.work) / "quality.json", dict(exclusions=quality))
        for unit in quality:
            for reason in unit["reasons"]:
                emit("Quality." + reason.split(":", 1)[0], detail="Page " + str(unit["page"]))
        reused = reuse_vectors(db, args.library)
        if reused:
            emit("Reused", reused, db.execute("SELECT count(*) FROM chunks").fetchone()[0])
        count = db.execute("SELECT count(*) FROM chunks").fetchone()[0]
        if count == 0:
            raise ValueError("No admissible text. Inspect page exclusions in checkpoint.sqlite.")
        done = db.execute("SELECT count(*) FROM chunks WHERE vector IS NOT NULL").fetchone()[0]
        if done < count:
            torch, model, device = encoder(args.model, args.device)
            load = EmbeddingLoad(torch, device, args.cpu_target, emit)
            while True:
                load.before()
                size = min(load.batch, load.capacity())
                rows = db.execute("SELECT id,text FROM chunks WHERE vector IS NULL ORDER BY length(text),id LIMIT ?", (size,)).fetchall()
                if not rows:
                    break
                try:
                    vectors = encode(torch, model, device, tokenizer, [row[1] for row in rows])
                except Exception as error:
                    if (torch_is_out_of_memory(torch, error) or 'RAG_BOOK_GPU_OOM' in str(error)) and load.shrink():
                        emit('Resource', detail=f'Reduced batch to {load.batch} after allocation failure')
                        continue
                    raise
                with db:
                    for row, vector in zip(rows, vectors):
                        db.execute("UPDATE chunks SET vector=? WHERE id=?", (array.array("f", vector).tobytes(), row[0]))
                done += len(rows)
                emit("Index", done, count, device=device)
                load.success()
        emit("Validate", count, count)
        publish(db, database, args.library, request)


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    for name in ("model", "input", "work", "library"):
        parser.add_argument("--" + name, required=True)
    parser.add_argument("--device", default="auto")
    parser.add_argument("--cpu-target", type=int, default=50, choices=range(10, 91))
    parser.add_argument("--query", action="store_true")
    run(parser.parse_args())
