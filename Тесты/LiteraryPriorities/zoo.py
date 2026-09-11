"""Sequential, isolated comparison of installed GGUF models. No product edits."""
import datetime as dt
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request

sys.stdout.reconfigure(encoding="utf-8")
ROOT = Path(__file__).resolve().parents[2]
MODELS = ROOT / "Данные_для_внедрения/Модели"
BACKEND = ROOT / "Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64/llama-server.exe"
PROBE = ROOT / "Тесты/LiteraryPriorities/bin/Debug/net10.0-windows/Probe.dll"
for option in sys.argv[2:]:
    if option.startswith("--probe="):
        PROBE = Path(option.split("=", 1)[1]).resolve()
FIXTURE = ROOT / "Тесты/LiteraryPriorities/runs/novel-07/Project"
OUT = Path(sys.argv[1]).resolve()
OUT.mkdir(parents=True, exist_ok=False)
MODELS_TO_TEST = [
    ("qwen38-4b", MODELS, "Qwen3.8-4B-Distill.Q5_K_M.gguf"),
    ("qwen38-9b", MODELS, "Qwen3.8-9B-Q4_K_M.gguf"),
    ("qwen3-8b", MODELS, "Qwen3-8B-Q4_K_M.gguf"),
    ("smolvlm2-2b", ROOT / "Runtime/Components/Models", "SmolVLM2-2.2B-Instruct-Q4_K_M.gguf"),
    ("qwen38-27b", MODELS, "Qwen3.8-27B-Ridge-3.7bpw.gguf"),
    ("qwen36-27b", MODELS, "qwen3.6-27b-q4_k_m.gguf"),
    ("kimi-vl", MODELS, "Kimi-VL-A3B-Thinking-2506-Q4_K_M.gguf"),
    ("runeweaver", ROOT / "Тесты/Runeweaver/model", "MN-12B-Runeweaver-RP-RU.Q4_K_M.gguf"),
]
for option in sys.argv[2:]:
    if option.startswith("--only="):
        MODELS_TO_TEST = [m for m in MODELS_TO_TEST if m[0] in option.split("=", 1)[1].split(",")]
if not MODELS_TO_TEST:
    raise ValueError("No selected models")

def write(path, obj):
    path.write_text(json.dumps(obj, ensure_ascii=False, indent=2), encoding="utf-8")

def stop(process):
    if process is not None and process.poll() is None:
        subprocess.run(["taskkill", "/PID", str(process.pid), "/T", "/F"], capture_output=True, timeout=15)
        process.wait(timeout=15)

outcomes = []
for name, folder, filename in MODELS_TO_TEST:
    paths = list(folder.rglob(filename))
    if len(paths) != 1:
        raise RuntimeError(f"Expected one installed model: {name}: {paths}")
    model = paths[0]
    target = OUT / name
    target.mkdir()
    with socket.socket() as sock:
        sock.bind(("127.0.0.1", 0))
        port = sock.getsockname()[1]
    endpoint = f"http://127.0.0.1:{port}/"
    command = [str(BACKEND), "-m", str(model), "--host", "127.0.0.1", "--port", str(port),
        "-c", "16384", "-np", "1", "--fit", "on", "--fit-target", "2048", "--cache-ram", "0",
        "--no-context-shift", "--offline", "--jinja", "--slots", "--reasoning", "off",
        "--reasoning-budget", "0", "--device", "CUDA0", "-t", "8", "-tb", "8"]
    env = {k: v for k, v in os.environ.items() if not k.startswith("LLAMA_ARG_")}
    info = dict(model=name, path=str(model), bytes=model.stat().st_size, endpoint=endpoint, command=command,
                startUtc=dt.datetime.now(dt.timezone.utc).isoformat(), status="starting")
    write(target / "launch.json", info)
    print("START", name, flush=True)
    server = probe = None
    start = time.monotonic()
    try:
        with (target / "server.log").open("w", encoding="utf-8") as log:
            server = subprocess.Popen(command, cwd=BACKEND.parent, env=env, stdout=log, stderr=log,
                                      creationflags=subprocess.CREATE_NO_WINDOW)
            info["pid"] = server.pid
            write(target / "launch.json", info)
            while True:
                if server.poll() is not None:
                    raise RuntimeError(f"Server exited {server.returncode}")
                if time.monotonic() - start > 150:
                    raise TimeoutError("Startup exceeded 150 seconds")
                try:
                    with urllib.request.urlopen(endpoint + "health", timeout=2) as response:
                        if response.status == 200:
                            break
                except (OSError, TimeoutError):
                    time.sleep(.4)
            info["readySeconds"] = time.monotonic() - start
            for route in ["props", "slots"]:
                with urllib.request.urlopen(endpoint + route, timeout=5) as response:
                    (target / f"{route}.json").write_bytes(response.read())
            with (target / "probe.log").open("w", encoding="utf-8") as probe_log:
                probe = subprocess.Popen(["dotnet", str(PROBE), str(target / "run"), str(FIXTURE),
                    "--novel", "--fixture", "--endpoint", endpoint], cwd=ROOT, stdout=probe_log, stderr=probe_log,
                    creationflags=subprocess.CREATE_NO_WINDOW)
                probe.wait(timeout=600)
            info["probeExit"] = probe.returncode
            info["status"] = "complete" if probe.returncode == 0 else "probe_failed"
    except Exception as exc:
        info["status"] = "technical_failure"
        info["error"] = repr(exc)
    finally:
        stop(probe)
        stop(server)
        info["elapsedSeconds"] = time.monotonic() - start
        info["endedUtc"] = dt.datetime.now(dt.timezone.utc).isoformat()
        write(target / "outcome.json", info)
        outcomes.append(info)
        write(OUT / "outcomes.json", outcomes)
        print("END", name, info["status"], round(info["elapsedSeconds"], 1), flush=True)
print("DONE", len(outcomes), flush=True)
