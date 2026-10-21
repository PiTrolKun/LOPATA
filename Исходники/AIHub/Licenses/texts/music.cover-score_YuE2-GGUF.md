Source: https://huggingface.co/Serveurperso/YuE2-GGUF/blob/e630f2b8f6aedf8254c3186438633c1b5eeaf80a/README.md

---
language:
- en
- zh
- ja
- ko
- fr
- es
tags:
- music-generation
- text-to-music
- ggml
- gguf
- cpp
- yue2
- flow-matching
- abc-notation
license: cc-by-nc-4.0
base_model:
- m-a-p/YuE2-3B
- m-a-p/YuE2-Vae
- m-a-p/SheetSage2
- m-a-p/MERT-v2-FullSong
- Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4
---

# YuE2 GGUF

Pre-quantized GGUF models for [yue2.cpp](https://github.com/ServeurpersoCom/yue2.cpp), a portable C++17 implementation of YuE2 song generation using GGML.

Style tags and lyrics in, stereo 48kHz audio out, with the ABC score the model composed on the way. Runs on CPU, CUDA, Vulkan.

## Quick start

```bash
git clone --recurse-submodules https://github.com/ServeurpersoCom/yue2.cpp
cd yue2.cpp

pip install hf
./models.sh           # downloads the Q8_0 set (~5.4 GB)

mkdir build && cd build
cmake .. -DGGML_CUDA=ON
cmake --build . --config Release -j$(nproc)
cd ..

./build/yue-server --host 0.0.0.0 --port 8087 \
    --model models/YuE2-3B-Q8_0.gguf \
    --vae models/YuE2-Vae-F32.gguf
```

Open http://localhost:8087 in your browser. The embedded WebUI handles everything: write style tags and lyrics, generate, read the score the model wrote, play and download tracks.

<details>
<summary>CLI tools (without the server)</summary>

One request JSON drives both tools, the same schema the server and the WebUI speak: `style`, `lyrics`, `cot`, seeds, and the score or the semantic codes of a track when you feed one back.

```bash
# full pipeline: score, semantic codes, flow matching, VAE
./build/yue-synth \
    --model models/YuE2-3B-Q8_0.gguf \
    --vae models/YuE2-Vae-F32.gguf \
    --request request.json \
    --out song.mp3

# symbolic stage alone: the ABC score the model intends to play
./build/yue-plan \
    --model models/YuE2-3B-Q8_0.gguf \
    --request request.json \
    --out score.abc
```

</details>

## Available models

### Backbone (3.6B Mixture-of-Transformers)

| File | Quant | Size |
|------|-------|------|
| YuE2-3B-BF16.gguf | BF16 | 7.17 GB |
| YuE2-3B-Q8_0.gguf | Q8_0 | 3.81 GB |
| YuE2-3B-Q6_K.gguf | Q6_K | 2.94 GB |
| YuE2-3B-Q5_K_M.gguf | Q5_K_M | 2.62 GB |

One file holds two complete transformers sharing their attention: the autoregressive half writes the score then the semantic codes, the non-autoregressive half renders them by flow matching. Q8_0 is near lossless and is what the scripts load. No Q4: an audio code LM breaks below Q5.

### VAE

| File | Quant | Size |
|------|-------|------|
| YuE2-Vae-F32.gguf | F32 | 530 MB |

Oobleck SnakeBeta decoder, 48 kHz stereo, 1920x upsample. Never quantized: its weights are the audio.

### Transcriber (SheetSage2 on MERT-v2-FullSong, optional)

| File | Quant | Size |
|------|-------|------|
| MERT-v2-FullSong-F32.gguf | F32 | 2.53 GB |
| MERT-v2-FullSong-Q8_0.gguf | Q8_0 | 902 MB |

| File | Quant | Size |
|------|-------|------|
| SheetSage2-F32.gguf | F32 | 229 MB |
| SheetSage2-Q8_0.gguf | Q8_0 | 106 MB |

Audio to score, the cover path of YuE2: a recording becomes the ABC melody the model then realizes in your style with your lyrics. Each repository ships as its own GGUF: MERT-v2-FullSong is the audio encoder, SheetSage2 the head on it, holding the LoRA factors of the MERT attention projections, the layer mix and the decoder. Keep both files of the same quant side by side: the transcriber finds its MERT next to it and merges the LoRA at load, the arithmetic the reference applies. Only the linear projections are quantized; convolutions, the mel filterbank, the positions and the LoRA factors stay F32. Q8_0 transcribes identically to F32 on the test song and is what the scripts load. `yue-server --transcriber` enables it, `yue-transcribe` runs it from the command line.

### Audio tokenizer (Mothersuperior v9 on MERT-v2-FullSong, optional)

| File | Quant | Size |
|------|-------|------|
| yue2-mothersuperior-realaudio-tokenizer-v4-F32.gguf | F32 | 171 MB |
| yue2-mothersuperior-realaudio-tokenizer-v4-Q8_0.gguf | Q8_0 | 47 MB |

Audio to semantic codes, the encoder YuE2 does not ship, by [Mothersuperior](https://huggingface.co/Mothersuperior): an 8 layer transformer over MERT-v2-FullSong layer 20 that writes the 25 codes per second YuE2 itself composes in, a 375 bit/s description of a song. The repository carries every training round, this GGUF holds the v9 head, trained on songs YuE2 generated then on real recordings jointly with its realaudio NAR adapter against an audio domain loss. Like the transcriber, it loads the MERT GGUF of the same quant beside it. Feed its codes back as the semantic stream of a request and the song renders again through YuE2, best with the [realaudio NAR adapter](https://huggingface.co/Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4) loaded. `yue-server --tokenizer` enables it, `yue-tokenize` runs it from the command line.

The converter keeps the native dtype of the source byte for byte, so no dtype exists in a GGUF that does not exist in the checkpoint.

## Pipeline

```
style tags + lyrics
        v
LM, Autoregressive (AR)            writes the ABC score, then the semantic codes at 25 Hz,
        v  evict / load            and leaves everything in the KV cache
LM, Non-Autoregressive (NAR)       reads that cache and paints the acoustic latents by flow
        v  evict / load            matching, 64 channels per frame, all frames at once
VAE, Oobleck decoder               1920x upsample -> 48 kHz stereo
```

One backbone GGUF holds the two halves of a single Qwen3 transformer: the same 28 layers with two sets of attention projections and MLPs, one to write tokens, one to paint latents, sharing the embeddings and the final norm. The AR half works like a language model: token by token, it first writes the ABC score, a symbolic plan in plain text you can read and edit, then the semantic codes, one per 40 ms frame, and every token it processes lands in the KV cache. The NAR half is the same network used the other way round: it starts from Gaussian noise for every frame of the song, attends on the cache the AR half just left, and refines all the frames together with a midpoint flow matching solver, 32 steps of two evaluations, from noise to latents. The VAE turns the latents into sound, 1920 samples per frame.

Only one module is in VRAM at a time. The AR half is evicted once the codes are written, the NAR half loads, is evicted in turn, and the VAE loads; the KV cache stays through all of it, so the halves trade places around it and nothing is recomputed, and once the track is out the cache goes too, nothing stays on the GPU between two requests. `--keep-loaded` keeps everything resident on a card with the budget.

The ABC score is the white box of the pipeline: read it, edit a chord or a bar, hand it back, and the model realizes your version instead of writing a new one. A track also comes back with its semantic codes, and feeding them back re-renders it without the autoregressive stage, so the flow matching steps, the noise seed or the output format can be iterated for a fraction of the cost.

VRAM: the KV cache sized on the 24576 token context is the other big term; `--max-seq` trades context for memory. A 65 s song in Q8_0 peaks at 5.8 GB at the full context and 3.8 GB at `--max-seq 8192`.

## License

The model weights are licensed by their authors under [Creative Commons Attribution-NonCommercial 4.0 International](https://creativecommons.org/licenses/by-nc/4.0/) (CC BY-NC 4.0), and this repository redistributes them under the same terms. **Non-commercial use only.**

- Original work: YuE2 by [MAP](https://huggingface.co/m-a-p), from [m-a-p/YuE2-3B](https://huggingface.co/m-a-p/YuE2-3B) and [m-a-p/YuE2-Vae](https://huggingface.co/m-a-p/YuE2-Vae); the transcriber is SheetSage2 by MAP, from [m-a-p/SheetSage2](https://huggingface.co/m-a-p/SheetSage2), built on MERT2 (MERT-v2-FullSong) by MAP, from [m-a-p/MERT-v2-FullSong](https://huggingface.co/m-a-p/MERT-v2-FullSong), both released under the same CC BY-NC 4.0 terms; the audio tokenizer is the v9 tokenizer head by Mothersuperior, from [Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4](https://huggingface.co/Mothersuperior/yue2-mothersuperior-realaudio-tokenizer-v4), derived from YuE2-3B and released under CC BY-NC 4.0.
- Modifications: the checkpoint tensors are converted to the GGUF container, keeping their native dtype, and quantized to Q8_0, Q6_K and Q5_K_M for the backbone and to Q8_0 for the audio encoder, the transcriber and the audio tokenizer. The SheetSage2 LoRA factors ship as released; yue2.cpp merges them into the MERT2 attention projections at load, the arithmetic the reference applies. No weight is retrained, fine tuned or otherwise altered in substance.
- The upstream license text ships in this repository as `LICENSE`, and the weights are offered as-is, without warranties of any kind.

Nothing here is endorsed by or affiliated with the authors of YuE2.

## Acknowledgements

Independent C++/GGML implementation based on [YuE2](https://github.com/multimodal-art-projection/YuE) by MAP, on SheetSage2 and MERT2 by MAP for the transcriber, and on the audio tokenizer and the realaudio NAR adapter by Mothersuperior. All original model weights are theirs, this is a native backend.

```bibtex
@article{yuan2025yue,
	title = {{YuE}: Scaling Open Foundation Models for Long-Form Music Generation},
	author = {Yuan, Ruibin and Lin, Hanfeng and Guo, Shuyue and Zhang, Ge and Pan, Jiahao and Zang, Yongyi and Liu, Haohe and Liang, Yiming and Ma, Wenye and Du, Xingjian and Ye, Zhen and Ma, Yinghao and Xue, Wei and Tan, Xu and Guo, Yike},
	journal = {arXiv preprint arXiv:2503.08638},
	year = {2025},
	eprint = {2503.08638},
	archivePrefix = {arXiv},
	url = {https://arxiv.org/abs/2503.08638}
}

@inproceedings{li2024mert,
	title = {MERT: Acoustic Music Understanding Model with Large-Scale Self-supervised Training},
	author = {Li, Yizhi and Yuan, Ruibin and Zhang, Ge and Ma, Yinghao and Chen, Xingran and Yin, Hanzhi and Xiao, Chenghao and Lin, Chenghua and Ragni, Anton and Benetos, Emmanouil and Gyenge, Norbert and Dannenberg, Roger and Liu, Ruibo and Chen, Wenhu and Xia, Gus and Shi, Yemin and Huang, Wenhao and Wang, Zili and Guo, Yike and Fu, Jie},
	booktitle = {International Conference on Learning Representations},
	year = {2024},
	url = {https://proceedings.iclr.cc/paper_files/paper/2024/hash/33dffa2e3d2ab74a783d1a8c292f66d9-Abstract-Conference.html}
}
```

## Links

- [yue2.cpp](https://github.com/ServeurpersoCom/yue2.cpp) - source code
- [YuE](https://github.com/multimodal-art-projection/YuE) - original Python implementation
- [m-a-p on Hugging Face](https://huggingface.co/m-a-p) - original weights
