"""One-shot, offline book embedding worker. No chat or background indexing."""
import argparse
import json
import os
import sys
import time


def emit(**data):
    print(json.dumps(data, ensure_ascii=False), flush=True)


def windows(offsets, capacity, overlap=48):
    """Token windows with exact source-character boundaries; no silent truncation."""
    if capacity <= overlap:
        raise ValueError("Invalid token window")
    start = 0
    while start < len(offsets):
        end = min(start + capacity, len(offsets))
        yield offsets[start][0], offsets[end - 1][1]
        if end == len(offsets):
            break
        start = end - overlap


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--model")
    parser.add_argument("--input")
    parser.add_argument("--output")
    args = parser.parse_args()
    import torch
    import transformers
    if torch.__version__.split("+")[0] != "2.10.0" or transformers.__version__ != "5.3.0":
        raise RuntimeError("Unexpected runtime versions")
    from transformers import AutoModel, AutoTokenizer
    if args.check:
        emit(ready=True, cuda=torch.cuda.is_available())
        return
    started = time.monotonic()
    torch.set_num_threads(max(1, min(4, (os.cpu_count() or 2) // 2)))
    device = "cuda" if torch.cuda.is_available() else "cpu"
    dtype = (torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16) if device == "cuda" else torch.float32
    emit(stage="Loading", device=device)
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True, local_files_only=True)
    model = AutoModel.from_pretrained(args.model, trust_remote_code=True, local_files_only=True,
                                      dtype=dtype, attn_implementation="sdpa").to(device).eval()
    if model.config.hidden_size != 1024 or model.config.is_causal:
        raise ValueError("Expected 1024-dimensional bidirectional Giga encoder")
    with open(args.input, encoding="utf-8-sig") as stream:
        sections = json.load(stream)
    chunks = []
    for section in sections:
        text = section["text"]
        tokens = tokenizer(text, add_special_tokens=False, return_offsets_mapping=True, truncation=False)
        capacity = 512 - tokenizer.num_special_tokens_to_add(pair=False)
        for begin, end in windows(tokens["offset_mapping"], capacity):
            chunk = text[begin:end]
            # Re-tokenization at a cut can change a boundary token. Preserve text and
            # split further rather than discard its tail with truncation=True.
            pending = [(begin, chunk)]
            while pending:
                offset, part = pending.pop(0)
                if len(tokenizer(part, add_special_tokens=True)["input_ids"]) > 512:
                    mid = len(part) // 2
                    if mid == 0:
                        raise ValueError("Cannot split oversized token span")
                    pending[0:0] = [(offset, part[:mid]), (offset + mid, part[mid:])]
                elif part.strip():
                    chunks.append(dict(source=section["source"], section=section["section"],
                                       offset=offset, text=part, kind="reference"))
    if not chunks:
        raise ValueError("No indexable text")
    with open(args.output, "x", encoding="utf-8") as output, torch.inference_mode():
        for i in range(0, len(chunks), 4):
            batch = chunks[i:i + 4]
            inputs = tokenizer([c["text"] for c in batch], padding=True, truncation=False, return_tensors="pt").to(device)
            hidden = model(**inputs).last_hidden_state
            mask = inputs["attention_mask"].unsqueeze(-1).to(hidden.dtype)
            vectors = torch.nn.functional.normalize((hidden * mask).sum(1).float() / mask.sum(1).float().clamp_min(1), p=2, dim=1)
            if not torch.isfinite(vectors).all():
                raise ValueError("Non-finite embedding")
            for j, (chunk, vector) in enumerate(zip(batch, vectors.cpu().tolist())):
                output.write(json.dumps(dict(id=i + j + 1, payload=chunk, vector=vector), ensure_ascii=False) + "\n")
            emit(stage="Embedding", done=min(i + 4, len(chunks)), total=len(chunks), device=device)
    emit(stage="Complete", total=len(chunks), device=device, seconds=round(time.monotonic() - started, 3),
         peak_vram_bytes=torch.cuda.max_memory_allocated() if device == "cuda" else 0)


if __name__ == "__main__":
    main()
