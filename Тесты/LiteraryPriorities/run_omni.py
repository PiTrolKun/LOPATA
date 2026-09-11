"""Run the Omni comparison after all GGUF servers have exited."""
import datetime as dt
import json
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request

root = Path(__file__).resolve().parents[2]
out = Path(sys.argv[1]).resolve()
out.mkdir(parents=True, exist_ok=False)
model = next((root / "Данные_для_внедрения/Модели/Vision/Qwen2.5-Omni-3B").glob("*/model.safetensors.index.json")).parent
python = root / "Runtime/Python/qwen3-omni/.venv/Scripts/python.exe"
with socket.socket() as sock:
    sock.bind(("127.0.0.1", 0))
    port = sock.getsockname()[1]
endpoint = f"http://127.0.0.1:{port}/"
command = [str(python), "-X", "utf8", str(Path(__file__).with_name("omni_endpoint.py")),
           "--port", str(port), "--model", str(model), "--log", str(out / "generation.jsonl")]
info = dict(model="qwen25-omni", command=command, startUtc=dt.datetime.now(dt.timezone.utc).isoformat(),
            limitation="Planning is prompt-only JSON. Single-action grammar is unsupported and returns 422.")
server = probe = None
start = time.monotonic()
try:
    with (out / "server.log").open("w", encoding="utf-8") as log:
        server = subprocess.Popen(command, cwd=root, stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
        info["pid"] = server.pid
        (out / "launch.json").write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")
        while True:
            if server.poll() is not None:
                raise RuntimeError(f"Server exited: {server.returncode}")
            if time.monotonic()-start > 150:
                raise TimeoutError("Startup exceeded 150s")
            try:
                with urllib.request.urlopen(endpoint+"health", timeout=2) as response:
                    if response.status == 200:
                        break
            except OSError:
                time.sleep(.4)
        info["readySeconds"] = time.monotonic()-start
        with (out / "probe.log").open("w", encoding="utf-8") as log_probe:
            probe = subprocess.Popen(["dotnet", str(root / "Тесты/LiteraryPriorities/bin/zoo2/Probe.dll"),
                str(out / "run"), str(root / "Тесты/LiteraryPriorities/runs/novel-07/Project"),
                "--novel", "--fixture", "--endpoint", endpoint], cwd=root, stdout=log_probe, stderr=log_probe,
                creationflags=subprocess.CREATE_NO_WINDOW)
            probe.wait(timeout=600)
        info["status"] = "complete" if probe.returncode == 0 else "probe_failed"
except Exception as exc:
    info.update(status="technical_failure", error=repr(exc))
finally:
    for process in [probe, server]:
        if process is not None and process.poll() is None:
            subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=15)
            process.wait(timeout=15)
    info["elapsedSeconds"] = time.monotonic()-start
    (out / "outcome.json").write_text(json.dumps(info, ensure_ascii=False, indent=2), encoding="utf-8")
    print(json.dumps(info, ensure_ascii=False))
