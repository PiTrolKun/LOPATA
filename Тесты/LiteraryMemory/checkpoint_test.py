"""No model load: verify interrupted embedding append recovery."""
import importlib.util
import json
from pathlib import Path
import tempfile

source = Path(__file__).resolve().parents[2] / "Исходники/AIHub/Tools/giga_embeddings.py"
spec = importlib.util.spec_from_file_location("worker", source)
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
chunks = [{"text": "Первый"}, {"text": "Второй"}]
row = json.dumps({"id": 1, "payload": chunks[0], "vector": [1.] + [0.] * 1023}) + "\n"
with tempfile.TemporaryDirectory() as folder:
    path = Path(folder) / "vectors.jsonl"
    assert worker.resume_checkpoint(str(path), "identity", chunks) == 0
    path.write_bytes(row.encode() + b'{"id":2')
    assert worker.resume_checkpoint(str(path), "identity", chunks) == 1
    assert path.read_bytes() == row.encode()
    try:
        worker.resume_checkpoint(str(path), "changed", chunks)
        raise AssertionError("Different identity accepted")
    except ValueError:
        pass
    path.write_bytes(b'broken\n' + row.encode())
    try:
        worker.resume_checkpoint(str(path), "identity", chunks)
        raise AssertionError("Middle corruption accepted")
    except ValueError:
        pass
    invalid = json.dumps({"id": 1, "payload": chunks[0], "vector": [0.] * 1024}) + "\n"
    path.write_text(invalid, encoding="utf-8")
    assert worker.resume_checkpoint(str(path), "identity", chunks) == 0
    assert path.stat().st_size == 0
print("PASS checkpoint recovery, identity, corruption, normalization")
