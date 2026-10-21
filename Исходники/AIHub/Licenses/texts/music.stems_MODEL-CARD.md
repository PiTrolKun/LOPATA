---
language: en
license: mit
library_name: onnxruntime
pipeline_tag: audio-to-audio
tags:
  - onnx
  - onnxruntime
  - stem-separation
  - source-separation
  - 6-stem
  - guitar-extraction
  - piano-extraction
  - vocal-remover
  - demucs
  - htdemucs
  - htdemucs_6s
  - music
  - audio-to-audio
  - mobile
  - ios
  - android
  - coreml
  - directml
  - production-ready
datasets:
  - StemSplitio/stem-separation-benchmark-2026
inference: false
---

# HT-Demucs 6-stem — ONNX (with guitar + piano)

The **first ONNX export of the 6-stem `htdemucs_6s` variant** on the
Hugging Face Hub. Adds **guitar** and **piano** stems on top of the
standard 4 (drums / bass / other / vocals). Runs in `onnxruntime` on
CPU out of the box, and on CoreML / CUDA / DirectML with a one-line
provider change. **No PyTorch required at inference.**

If you need guitar or piano isolation, this is the only off-the-shelf
ONNX model on the Hub that gives you that.

---

## TL;DR

```bash
pip install onnxruntime numpy soundfile

# 258 MB fp32 model — all 6 stems:
python infer.py your-song.mp3 ./out/

# 136 MB fp16weights variant (same runtime cost):
python infer.py your-song.mp3 ./out/ --small

# Just the guitar stem:
python infer.py your-song.mp3 ./out/ --stems guitar
```

The repo contains:

- `htdemucs_6s.onnx` — 258 MB, opset 17, parity-verified vs PyTorch fp32.
- `htdemucs_6s_fp16weights.onnx` — 136 MB, fp16-stored weights, same runtime memory / latency.
- `infer.py` — pure-numpy reference inference (~200 lines, no torch).
- `requirements.txt` — three small packages, no PyTorch.

---

## What stems do I get?

```python
SOURCES = ("drums", "bass", "other", "vocals", "guitar", "piano")
```

Output tensor: `stems[1, 6, 2, 343980]` in that exact stem order. The
6-stem variant overlaps with the 4-stem on the first 4 stems but with
slightly different separation behavior — the extra guitar and piano
heads change what "other" learns to keep.

---

## Quality

Parity vs PyTorch fp32 (random input, 7.8 s segment):

- `htdemucs_6s.onnx` max abs diff: **2.42 × 10⁻⁴**
- `htdemucs_6s_fp16weights.onnx` max abs diff (vs fp32 weights): **1.06 × 10⁻⁴**

Both well within the 1e-3 publish threshold.

Stem-specific SDR (informal; the official paper covers in-depth eval):

| Stem | SDR (MUSDB18-HQ, approx.) |
|---|---:|
| drums | ~9.5 dB |
| bass | ~9.0 dB |
| other | ~5.5 dB (lower because the model now also predicts guitar + piano) |
| vocals | ~8.5 dB |
| guitar | extracted-track-quality (no public SDR baseline on MUSDB) |
| piano | extracted-track-quality (no public SDR baseline on MUSDB) |

If you care about absolute drums/vocals SDR, prefer
[`htdemucs-ft-onnx`](https://huggingface.co/StemSplitio/htdemucs-ft-onnx).
If you specifically need guitar or piano isolation,
**this is the model**.

---

## Performance

Single 7.8 s segment, Apple M4 Pro CPU:

| Variant | RAM | Latency | RTF |
|---|---:|---:|---:|
| `htdemucs_6s.onnx` (fp32) | ~1.1 GB | ~1.6 s | 0.20 |
| `htdemucs_6s_fp16weights.onnx` | ~1.1 GB | ~1.6 s | 0.20 |

CUDA / DirectML / CoreML EPs are typically ≥ 5× faster on real GPUs.

---

## Quick start

### Python

```python
import soundfile as sf
import infer

audio, sr = sf.read("your-song.mp3", dtype="float32", always_2d=True)
stems = infer.separate(audio.T, sr,
                       model_path=infer.DEFAULT_MODEL,
                       providers=["CPUExecutionProvider"])
sf.write("guitar.wav", stems["guitar"].T, sr)
sf.write("piano.wav",  stems["piano"].T,  sr)
```

### CLI

```bash
python infer.py your-song.mp3 ./out/                          # all 6 stems
python infer.py your-song.mp3 ./out/ --stems guitar piano     # guitar + piano only
python infer.py your-song.mp3 ./out/ --providers coreml       # macOS arm64
python infer.py your-song.mp3 ./out/ --providers cuda         # Linux + NVIDIA
python infer.py your-song.mp3 ./out/ --small                  # 136 MB variant
```

### Mobile / Web

```swift
// iOS / Swift — 258 MB or 136 MB bundled
import onnxruntime_objc
let session = try ORTSession(env: env,
    modelPath: Bundle.main.path(forResource: "htdemucs_6s_fp16weights",
                                 ofType: "onnx")!,
    sessionOptions: opts)
```

```js
// Browser
import * as ort from "onnxruntime-web";
const sess = await ort.InferenceSession.create(
  "htdemucs_6s_fp16weights.onnx",
  { executionProviders: ["wasm"] },
);
const t = new ort.Tensor("float32", audioBuffer, [1, 2, 343980]);
const out = await sess.run({ mix: t });   // out.stems is (1, 6, 2, 343980)
```

For a turnkey browser demo with file-picker + chunked overlap-add, see
[`demucs-onnx browser-demo`](https://github.com/StemSplit/demucs-onnx#browser-demos).

---

## Input / output spec

| Tensor | Name | Shape | Dtype | Notes |
|---|---|---|---|---|
| Input | `mix` | `(1, 2, 343980)` | float32 | Stereo, 44.1 kHz, 7.8 s segment. Values in [-1, 1]. |
| Output | `stems` | `(1, 6, 2, 343980)` | float32 | Stems in order `[drums, bass, other, vocals, guitar, piano]`. |

For longer audio, chunk with overlap-add — see `infer.py::separate`.

---

## Tooling — `demucs-onnx` Python package

This model can be run via the open-source
[`demucs-onnx`](https://github.com/StemSplit/demucs-onnx) Python package
on PyPI. It auto-downloads from this repo on first use.

```bash
pip install demucs-onnx

# 6-stem mode — all 6 stems, single session:
demucs-onnx separate song.mp3 stems/ --model htdemucs_6s

# Just guitar + piano:
demucs-onnx separate song.mp3 stems/ --model htdemucs_6s --stems guitar piano

# Python API:
python -c "from demucs_onnx import separate_stem; \
  guitar = separate_stem('song.mp3', 'guitar')"
```

To re-export your own fine-tune:

```bash
pip install 'demucs-onnx[export]'
demucs-onnx export htdemucs_6s out/htdemucs_6s.onnx
```

---

## How it was built

The export pipeline lives in the open-source
[`demucs-onnx`](https://github.com/StemSplit/demucs-onnx) package at
[`demucs_onnx/export/`](https://github.com/StemSplit/demucs-onnx/tree/main/src/demucs_onnx/export).
It applies the same four patches that make `htdemucs_ft` exportable:

1. Complex-typed `torch.stft` outputs → `Conv1d` with sin/cos kernels.
2. `model.segment` `fractions.Fraction` → plain `float`.
3. `random.randrange` in transformer pos-embedding → hardcoded `shift=0`.
4. `aten::_native_multi_head_attention` (no ONNX symbolic) → drop-in
   `nn.MultiheadAttention.forward` built from `Linear`/`bmm`/`softmax`.

The 6-stem head is wider than the 4-stem one but the surgery is
identical — no new blockers. Parity at 2.42 × 10⁻⁴ on first try.

---

## Related work

Sibling ONNX repos from the same export pipeline:

| Repo | Stems | Use when |
|---|---|---|
| [`htdemucs-ft-onnx`](https://huggingface.co/StemSplitio/htdemucs-ft-onnx) | 4 (bag) | Best SDR on the standard 4 stems. |
| [`htdemucs-onnx`](https://huggingface.co/StemSplitio/htdemucs-onnx) | 4 (single) | Fastest 4-stem startup. |
| `htdemucs-6s-onnx` *(this)* | **6** | You need **guitar** or **piano** as a stem. |
| [`htdemucs-ft-{drums,bass,other,vocals}-onnx`](https://huggingface.co/StemSplitio) | 1 | Fastest single-stem inference. |

Full benchmark across every popular open-source separator:
[StemSplitio/stem-separation-benchmark-2026](https://huggingface.co/datasets/StemSplitio/stem-separation-benchmark-2026).

---

## Skip the infrastructure — use the StemSplit API

Don't want to ship a 258 MB model in your app, manage a GPU pool, or
write overlap-add chunking? Use the **[StemSplit API](https://stemsplit.io/developers)**
instead — same model under the hood, hosted for you, with credits.

- 🌐 [stemsplit.io](https://stemsplit.io)
- 📘 [Developer docs](https://stemsplit.io/developers/docs)
- 🔌 [API reference](https://stemsplit.io/developers/reference)

---

## License & attribution

This repo is **MIT-licensed**, matching the original HT-Demucs.

```bibtex
@inproceedings{rouard2023hybrid,
  title     = {Hybrid Transformers for Music Source Separation},
  author    = {Rouard, Simon and Massa, Francisco and D{\'e}fossez, Alexandre},
  booktitle = {ICASSP},
  year      = {2023}
}
```

- Original PyTorch model: [`facebookresearch/demucs`](https://github.com/facebookresearch/demucs)
- ONNX export, parity verification, and packaging by [StemSplit](https://stemsplit.io)
- Search keywords: **htdemucs 6 stem onnx**, **htdemucs_6s onnx**, **guitar isolation onnx**,
  **piano isolation onnx**, **demucs 6-stem mobile**, **stem separation guitar onnx**.
