---
license: cc-by-nc-4.0
base_model:
- m-a-p/YuE2-3B
- m-a-p/MERT-v2-FullSong
tags:
- audio
- music
- yue2
- tokenizer
- lora
---
# yue2-mothersuperior-realaudio-tokenizer-v4

Real-audio tooling for [YuE2-3B](https://huggingface.co/m-a-p/YuE2-3B): the **audio → semantic-token encoder** YuE2 doesn't ship, plus a
**NAR-branch LoRA** so the decoder renders real-production latents. Together they let you tokenize your own recordings, LoRA-tune YuE2's AR on
an artist, and generate new songs or covers.

## Files
| file | what |
|---|---|
| `tokenizer_head_joint_v4.pt` / `.safetensors` / `.bf16.safetensors` | MERT-v2-FullSong layer-20 features (per-track instance-normalised, 25 Hz) → 32,768 YuE2 semantic codes. 8-layer transformer, d=512, 512-frame windows. Held-out exact match on YuE2's own songs: 16.1% top-1 (near-miss codes render almost identically; ear tests of NAR round-trips sit around 95%). |
| `nar_lora_joint_v4.pt` / `.safetensors` / `.bf16.safetensors` / `_comfyui.safetensors` | rank-32 LoRA on `nar_self_attn.{q,k,v,o}_proj` + `nar_mlp.{gate,up,down}_proj` (28 layers) + full `vae2llm`/`llm2vae`. Trained jointly with the head on real audio. |
| `scripts/` | the training loop and inference scripts (below). `scripts/ckpt_io.py` loads either format. |

### Safetensors layout
Same weights as the `.pt` files, bit-exact in fp32 (the `.bf16` variants are half the size; head top-1 agreement with fp32 is 98.6%). The previously published scripts accept either
extension via `scripts/ckpt_io.load_ckpt(path)`, which returns the same dict the `.pt` files hold.

- **Head**: the plain `state_dict` of the 8-layer encoder (`inp.*`, `pos`, `enc.layers.{0..7}.*`, `norm.*`, `head.*`), 103 tensors.
- **NAR LoRA**: `layers.{0..27}.nar_self_attn.{q,k,v,o}_proj.lora_A` `[32, in]` and `.lora_B` `[out, 32]`, plus `layers.{i}.nar_mlp.{gate,up,down}_proj.lora_{A,B}`.
  Apply as `W += lora_B @ lora_A` (scale 1.0, no alpha) to the matching `model.layers[i]` Linear of YuE2-3B. `vae2llm.{weight,bias}` and `llm2vae.{weight,bias}`
  are **full replacement weights** for those two Linear layers, not LoRA deltas. Rank and the delta rule are also in the file metadata.

### ComfyUI
`nar_lora_joint_v4_comfyui.safetensors` is the same NAR LoRA in ComfyUI's native YuE2 layout: load it with the standard **LoraLoader** on the
**MODEL** output of the YuE2 checkpoint loader (`Comfy-Org/YuE2`, `yue2_3b_bf16.safetensors`), strength 1.0; the CLIP input is untouched by this file.
ComfyUI stores the NAR branch as `diffusion_model.model.layers.{i}.self_attn.qkv_proj` / `o_proj` and `mlp.gate_up_proj` / `down_proj` (q,k,v and
gate,up fused), so the separate q/k/v and gate/up LoRAs are packed as one block-diagonal LoRA per fused matrix (`lora_up`/`lora_down`, no alpha =
scale 1.0; `up @ down` equals the stacked deltas exactly). The full `vae2llm` / `llm2vae` replacements ship as `.diff` / `.diff_b` against the m-a-p base.

Trained on 4,765 YuE2 self-generated songs, then adapted to real audio. If your material sounds off, rerun `joint.py` on your own audio (step 3 below).

## Requirements
Python 3.12 venv with [`yue2-infer`](https://github.com/multimodal-art-projection/YuE) (commit 92a73cc7), torch 2.10 + cu128, torchaudio 2.10,
transformers, soundfile, scipy, safetensors, demucs; `HF_HOME` with `m-a-p/YuE2-3B`, `m-a-p/YuE2-Vae`, `m-a-p/MERT-v2-FullSong`.
A 24 GB GPU is enough for every stage (14–18 GB measured with gradient checkpointing).

**You also need the minted regularizer pack**: `regularizer/minted_regularizer_pack.pt` from
[Mothersuperior/yue2-minted-corpus](https://huggingface.co/datasets/Mothersuperior/yue2-minted-corpus) (~100 MB, 4,732 YuE2-generated songs as
`{name, src, style, lyrics, codec}`). The AR trainer draws 50% of its songs from it so a small artist set cannot collapse YuE2's token grammar; the
`minted_val` items are the held-out check whose loss should stay flat. The full corpus (audio + tokens + latents) is in the same dataset if you
want to retrain the head.

**Paths are hard-coded to our pod layout** (`/workspace/tok/full`, `/workspace/real/...`, `/workspace/yue2-corpus/tracks`, `/workspace/real/ar/dataset.pt`).
Recreate that layout or edit the constants at the top of each script.

## Train an artist LoRA (folder of songs → LoRA)
Per song you need `<name>.flac`, `<name>.lyrics.txt` (**full** lyrics with `[verse]/[chorus]/[bridge]/...` tags — truncated lyrics ruin structure),
and `<name>.txt` = a style caption starting with your trigger phrase (e.g. `xyzq, in the style of xyzq. <description of the sound>`).

1. `python prep_real.py` — MERT features, VAE latents and the prompt prefix per song.
2. `python cursor_prep.py` — Demucs vocal stem → MMS forced alignment of the lyrics → lyric-cursor targets (automatic).
3. *(optional, recommended for a new artist/era)* `HOLD_TRACK=<one song name> python joint.py joint_mine 3000 1 1 tokenizer_head_joint_v4.pt nar_lora_joint_v4.pt`
   — adapts head + NAR to your audio. Otherwise use the v3 files as-is.
4. `python ar_prep.py <head.pt>` — tokenizes your songs and merges the regularizer pack into `dataset.pt` (`ar_prep.py` expects the pack's records;
   point it at the downloaded file).
5. `SCHED_STEPS=3000 CK_FROM=600 CK_EVERY=200 python ar_lora_cursor.py my_lora 1600 64 0.5 none 1e-4 0.08`
   — rank-64 AR LoRA, 50/50 artist vs minted, lyric-cursor weight 0.08, checkpoints at 600/800/1000/1200/1400/1600. **Do not train longer**: past
   ~1,500 steps the model memorises the songs.
6. `LADDER_STYLE_TRACK=<song> LADDER_LYRICS=<lyrics.txt> bash ladder.sh my_lora nar_lora_joint_v4.pt` — renders one fixed prompt from every checkpoint (optional `FINALS=<file>` with lines `tag style_track lyrics seed`). Pick by ear (ours: step 800).

## Decoder
Consider decoding with [Mothersuperior/YuE2-Vae-merge-0.666](https://huggingface.co/Mothersuperior/YuE2-Vae-merge-0.666): a weight merge of
YuE2-Vae (0.666) and YuE2-Vae-legacy (0.334). The two releases share one encoder and only differ in the decoder; the merge sits between the clean
default decoder and the more musical legacy decoder. Drop-in: `YuE2Pipeline.from_pretrained("m-a-p/YuE2-3B", vae="Mothersuperior/YuE2-Vae-merge-0.666")`,
or point `ar_generate.py` at it.

## Inference
```bash
# new song: style caption with your trigger + lyrics, score-free
python ar_generate.py my_lora/step-800.pt nar_lora_joint_v4.pt out_tag <style_track> lyrics.txt 12
# cover: transcribe any recording with SheetSage2 (--melody-only), then
ABC_FILE=score.abc COT=melody STRIP_TEMPO_KEY=1 python ar_generate.py my_lora/step-800.pt nar_lora_joint_v4.pt cover_tag <style_track> lyrics.txt 21
# LoRA strength: AR_SCALE=0.77 ...   stock model control: pass `none` for either LoRA
```
`ar_generate.py` folds both LoRAs into the base weights and runs YuE2's own pipeline, so the stock sampler, CFG and VAE apply unchanged.

## Other scripts
`extract_full.py` + `train_v2.py` (retrain the head on the minted corpus), `nar_lora.py` (NAR LoRA alone), `teacher_train.py` (head fine-tune with the NAR as
teacher), `ar_lora.py` (AR LoRA without the cursor), `build_reg_pack.py` (rebuild the regularizer pack from a corpus).

Weights derive from YuE2-3B (CC BY-NC 4.0): non-commercial use only. Scripts are provided as-is.

## v5 head (2026-09-16): `tokenizer_head_v5_30k.safetensors` (+ `.bf16`)

Same architecture and input as the v4 head, retrained on the full 14,547-track minted corpus with a better schedule: optimi AdamW (lr 5e-4, betas 0.9/0.99, weight decay 0.01), 30,000 steps, cosine. Held-out token accuracy on minted tracks: **top-1 18.9%, top-5 45.0%, top-1-or-neighbour 34.1%** (v4-era head: 15.9 / 39.2 / 30.9). Drop-in for the v4 head in `ckpt_io.py` and in the ComfyUI FS_Audio Dataset Builder (`models/fs_audio/`). For the matching decoder LoRA see the v5 joint pair below.

## v5 joint pair (2026-09-16): `tokenizer_head_joint_v5` + `nar_lora_joint_v5` (+ `.bf16`, + `_comfyui`)

The v5 head co-trained for 3,000 steps with a rank-32 decoder LoRA on **128 real tracks from three artists (rock, metal, bass music)** plus 4,000 minted anchor tracks, using the decoder's flow loss on the real latents as the teacher (previous joint rounds used a single artist's catalog). Held-out real-audio decoder loss 1.056 -> **0.981**; minted top-1 unchanged (~18.9%, so the head stays universal); token repeat rate on real audio 9.3% -> **3.0%**. Use the pair together: head for tokenizing real audio, LoRA on the decoder when rendering those tokens. `nar_lora_joint_v5_comfyui.safetensors` is the ComfyUI-native layout (load with a LoRA loader, model strength 1.0).

## v8 / v9 joint pairs (2026-09-16): training against the audio, not just the latents

`tokenizer_head_joint_v8` + `nar_lora_joint_v8` and `tokenizer_head_joint_v9` + `nar_lora_joint_v9` (each fp32, `.bf16`, and `_comfyui` for the LoRA). The v5 pair stays available above; these are additions.

**What changed.** Every joint round so far trained the head and decoder LoRA against a loss in VAE-latent space (flow matching). That proxy was not tracking what we heard: v1 -> v3 -> v5 cut the latent loss from 1.159 to 0.981 while audio-domain distances to the original recordings barely moved. Starting with v6 the trainer adds an **audio-domain loss**: at each real-audio step the decoder's clean-latent estimate is decoded through the frozen (differentiable) YuE2 VAE and compared with the same seconds of the *original recording* using log-mel L1, multi-resolution STFT (spectral convergence + log-magnitude, windows 512/1024/2048) and a stereo-width term, applied at noise levels <= 0.4. Gradients reach the decoder LoRA directly and the head through the straight-through token choice. Data and everything else are identical to v5 (128 real tracks from three artists: rock, metal, bass music; 4,000 minted anchor tracks; 3,000 steps; init = `tokenizer_head_v5_30k` + `nar_lora_joint_v4`). The idea of penalising the rendered audio against the real production came from Kytra listening to the v5 reconstructions.

**Weight sweep**, measured on the three held-out tracks (whole songs) against the originals. LTAS = long-term average spectrum distance (the "production fingerprint"; this is the number that tracked the listening results). Stereo = L/R correlation (originals: 0.84 / 0.71 / 0.87).

| pair | audio-loss weight | latent loss | minted top-1 | LTAS dB rock / metal / bass | mel L1 dB rock / metal / bass | stereo rock / metal / bass |
|---|---|---|---|---|---|---|
| v5 | 0 (latent only) | 0.981 | 18.9% | 1.56 / 2.07 / 2.27 | 4.36 / 4.59 / 4.44 | 0.77 / 0.69 / 0.90 |
| v6 | 0.3 | 0.981 | 18.8% | 1.40 / 1.94 / 2.09 | 4.18 / 4.55 / 4.36 | 0.78 / 0.76 / 0.92 |
| v7 | 1.0 | 0.984 | 18.7% | 1.44 / 1.28 / 1.92 | 4.50 / 4.54 / 4.39 | 0.63 / 0.63 / 0.84 |
| **v8** | **2.0** | 0.983 | 19.0% | 1.28 / 1.75 / 1.84 | 4.28 / 4.49 / 4.51 | 0.76 / 0.70 / 0.92 |
| **v9** | **4.0** | 0.984 | 18.7% | 1.23 / 1.49 / 1.92 | 4.29 / 4.48 / 4.44 | 0.74 / 0.65 / 0.88 |

Listening (Kytra): v7 clearly better than v6, v8 clearly better than v7, v9 preferred overall. Minted top-1 stays flat across the sweep, so the head remains universal; the latent loss moves by 0.003, so the audio term is not fighting the latent objective. Mel L1 did **not** follow the listening results, LTAS did. Token-choice errors (occasional out-of-tune notes) are unchanged by this loss; that is a head-accuracy problem.

**Use:** tokenize real audio with the v9 (or v8) head and load the matching `nar_lora_joint_v9_comfyui` / `_v8_comfyui` on the decoder at model strength 1.0. Trainer: [`scripts/joint_v6.py`](scripts/joint_v6.py), the recovered original audio-loss variant of `scripts/joint.py` (env `AUX_W`, `AUX_TMAX`, `AUX_FR`). Read the [setup and historical-reproduction notes](scripts/joint_v6_README.md) first: this original script uses `.pt` checkpoints and the old pod paths; unlike the other published scripts, it has not been adapted to `ckpt_io`. The required neighbor tables are included under `assets/`.
