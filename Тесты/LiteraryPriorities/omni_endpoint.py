"""Bench-only text endpoint using the installed Omni Thinker loader.

Normal planning remains prompt-only JSON; constrained one-action grammar is
explicitly rejected. This limitation must not be scored as model reasoning.
"""
import argparse
import json
import os
from pathlib import Path
import sys
import time
import traceback
from http.server import BaseHTTPRequestHandler, HTTPServer

root = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(root / "Исходники/AIHub/Tools"))
os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["TRANSFORMERS_OFFLINE"] = "1"
from qwen25_omni_worker import OmniWorker

parser = argparse.ArgumentParser()
parser.add_argument("--port", type=int, required=True)
parser.add_argument("--model", required=True)
parser.add_argument("--log", required=True)
args = parser.parse_args()
worker = OmniWorker()
loaded = worker.warmup(args.model, 100 * 1024**3, 14 * 1024**3)
torch = worker.torch
torch.set_num_threads(8)
tokenizer = worker.processor.tokenizer

def log(value):
    with open(args.log, "a", encoding="utf-8") as out:
        out.write(json.dumps(value, ensure_ascii=False) + "\n")

log(dict(event="ready", loaded=loaded, backend="transformers", constrained_json=False))

class Handler(BaseHTTPRequestHandler):
    def result(self, code, data):
        body = json.dumps(data, ensure_ascii=False).encode()
        self.send_response(code)
        self.send_header("Content-Type", "application/json")
        self.send_header("Content-Length", str(len(body)))
        self.end_headers()
        self.wfile.write(body)

    def do_GET(self):
        self.result(200, [dict(id=0, is_processing=False)] if self.path == "/slots" else
                    dict(status="ok", backend="Omni Thinker Transformers", context=16384, constrained_json=False))

    def do_POST(self):
        try:
            body = json.loads(self.rfile.read(int(self.headers["Content-Length"])))
            if self.path == "/apply-template":
                return self.result(200, dict(prompt=worker.processor.apply_chat_template(
                    body["messages"], tokenize=False, add_generation_prompt=True)))
            if self.path == "/tokenize":
                return self.result(200, dict(tokens=tokenizer.encode(body["content"], add_special_tokens=body.get("add_special", False))))
            schema = body.get("response_format", {}).get("json_schema", {}).get("schema", {})
            allowed = schema.get("properties", {}).get("action", {}).get("enum", [])
            if len(allowed) == 1:
                log(dict(event="unsupported_grammar", allowed=allowed))
                return self.result(422, dict(error="Bench Omni adapter does not implement constrained single-action grammar. Do not score as semantic failure."))
            started = time.monotonic()
            text = worker.processor.apply_chat_template(body["messages"], tokenize=False, add_generation_prompt=True)
            inputs = worker.processor(text=text, return_tensors="pt", padding=True)
            inputs = inputs.to(worker.model.device).to(worker.model.dtype)
            length = inputs["input_ids"].shape[1]
            limit = body["max_tokens"]
            if length + limit > 16384:
                return self.result(400, dict(error="Context budget exceeded"))
            torch.manual_seed(body["seed"])
            temperature = body.get("temperature", .5)
            kwargs = dict(max_new_tokens=limit, do_sample=temperature > 0, repetition_penalty=body.get("repeat_penalty", 1.05),
                          eos_token_id=worker._text_eos_token_ids(), pad_token_id=tokenizer.pad_token_id,
                          use_audio_in_video=False, max_time=50)
            if temperature > 0:
                kwargs.update(temperature=temperature, top_k=40, top_p=.95, min_p=.05)
            with torch.inference_mode():
                generated = worker.model.generate(**inputs, **kwargs)
            new_tokens = generated[0, length:]
            answer = tokenizer.decode(new_tokens, skip_special_tokens=True)
            ended = int(new_tokens[-1]) in worker._text_eos_token_ids() if len(new_tokens) else False
            log(dict(event="generation", seconds=time.monotonic()-started, inputTokens=length, outputTokens=len(new_tokens),
                     eos=ended, requestedSchema=bool(schema), seed=body["seed"], messages=body["messages"], result=answer))
            self.send_response(200)
            self.send_header("Content-Type", "text/event-stream")
            self.send_header("Connection", "close")
            self.end_headers()
            response = dict(choices=[dict(index=0, delta=dict(content=answer), finish_reason=None)])
            finish = dict(choices=[dict(index=0, delta={}, finish_reason="stop" if ended else "length")])
            self.wfile.write(("data: " + json.dumps(response, ensure_ascii=False) + "\n\n" + "data: " + json.dumps(finish) + "\n\ndata: [DONE]\n\n").encode())
            self.close_connection = True
            del inputs, generated, new_tokens
            worker._clear_unused_cache()
        except Exception as exc:
            log(dict(event="error", error=traceback.format_exc()))
            self.result(500, dict(error=str(exc)))

try:
    HTTPServer(("127.0.0.1", args.port), Handler).serve_forever()
finally:
    worker.shutdown()
