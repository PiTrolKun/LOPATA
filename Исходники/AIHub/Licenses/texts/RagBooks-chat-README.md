---
license: mit
language:
- ru
- en
base_model:
- ai-sage/GigaChat3-10B-A1.8B-base
pipeline_tag: text-generation
library_name: transformers
tags:
- instruct
- moe
- multilingual
- tool-use
- long-context
---

# GigaChat 3.1 Lightning

`GigaChat 3.1 Lightning` is the compact instruct model of the GigaChat 3.1 family. It is a Mixture-of-Experts (MoE) model with 10B total parameters and 1.8B active parameters, designed for fast multilingual assistant workloads, reasoning, code, function calling, and product-style deployment.

For high-performance inference, an fp8 version of the model is available - [GigaChat3.1-10B-A1.8B](https://huggingface.co/ai-sage/GigaChat3.1-10B-A1.8B).

bf16 version is also avaliable - [GigaChat3.1-10B-A1.8B-bf16](https://huggingface.co/ai-sage/GigaChat3.1-10B-A1.8B-bf16).

More details can be found in the [Habr article](https://habr.com/en/companies/sberbank/articles/1014146/).

## Model architecture

GigaChat 3.1 Lightning uses a custom MoE architecture with the following key components.

### Mixture-of-Experts (MoE)

The model has 10B total parameters with 1.8B active parameters at inference time. This allows it to scale model capacity aggressively while keeping the active compute budget much lower than that of an equally large dense model.

### Multi-head Latent Attention (MLA)

Instead of standard multi-head attention, the model uses MLA, which compresses the KV cache into a latent representation. This reduces memory usage and improves inference throughput, especially in long-context settings.

### Multi-Token Prediction (MTP)

The model is trained with MTP, which allows it to predict multiple tokens per forward pass. In production systems, this can be used with speculative or parallel decoding techniques to improve throughput.

## Training data

The base GigaChat 3 training corpus spans 10 languages and includes books, academic material, code datasets, and mathematics datasets. All data goes through deduplication, language filtering, and automatic quality checks based on heuristics and classifiers.

Synthetic data remains a major contributor to quality. Across the broader training corpus, we used approximately 5.5 trillion synthetic tokens, including:

- question-answer data generated from source texts,
- reverse-prompt chains for structured data generation,
- model-authored notes embedded inside texts,
- millions of synthetic tasks with solutions in mathematics and olympiad-style programming,
- synthetic tests for code and reasoning tasks.

For the 3.1 release, we made major data improvements:

- Hard-domain expansion at Stage 1.5: stronger coverage of mathematics, finance, physics, engineering, biology, chemistry, and medicine.
- Stricter quality validation: our internal `Revisor` pipeline was extended with stronger checks for Markdown, LaTeX, and answer-format correctness.
- LLM-judge validation: SFT and DPO data is validated with judges selected for the task type and response structure.
- On-policy DPO data: preference pairs were generated from preview-model behavior, making them better aligned with real model failure modes.
- Better product-oriented data: we expanded data for search-and-citation scenarios, file-aware code interpretation, personalization, and agentic dialogues with executable tool calls.
- Improved answer style: we also revised formatting and writing guidelines to improve readability, correctness, and overall response quality.


## Post-training improvements

### DPO in native FP8

Unlike the preview release, `GigaChat 3.1 Lightning` includes a full DPO stage. This stage was redesigned for the MoE setup and trained in native FP8, not just quantized after training.

Important changes include:

- MTP heads trained during DPO for better consistency between main-model predictions and MTP predictions,
- weighted gamma with exponential decay over long sequences,
- stronger tuning of batch size and DPO contribution,
- better robustness against loop-inducing failure modes.

In our experiments, native FP8 DPO not only recovered the quality that could be lost with post-training FP8 quantization, but in some cases even exceeded the BF16 result while using substantially less memory.

### Faster post-training

We also optimized the SFT pipeline with a combination of sequence packing, dynamic sequence parallelism, and additional pipeline optimizations. This reduced training cost significantly and improved GPU utilization, especially on long-context workloads.

## Inference

One of the key advantages of `GigaChat3.1-10B-A1.8B` is its inference speed. The model (especially in MTP mode) demonstrates throughput comparable to that of significantly smaller dense models.
We measured this using vllm 0.17.1rc1.dev158+g600a039f5, concurrency=32, 1xH100 80gb SXM5.
[Link to code.](https://gist.github.com/chameleon-lizard/07c5fdc658da63b0fdf105ae5a752344)

| Model | Output tps | Total tps | TPOT | Diff vs Lightning BF16 |
|---|---|---|---|---|
| GigaChat-3.1-Lightning BF16 | 2 866 | 5 832 | 9.52 | +0.0% |
| GigaChat-3.1-Lightning BF16 + MTP | 3 346 | 6 810 | 8.25 | +16.7% |
| GigaChat-3.1-Lightning FP8 | 3 382 | 6 883 | 7.63 | +18.0% |
| GigaChat-3.1-Lightning FP8 + MTP | 3 958 | 8 054 | 6.92 | +38.1% |
| YandexGPT-5-Lite-8B | 3 081 | 6 281 | 7.62 | +7.5% |


## Benchmark Results

| Domain | Metric | GigaChat-3-Lightning | **GigaChat-3.1-Lightning** | Qwen3-1.7B-Instruct | Qwen3-4B-Instruct | SmolLM3 | gemma-3-4b-it |
|---|---|---:|---:|---:|---:|---:|---:|
| General | MMLU RU | 0.683 | 0.6803 | - | 0.597 | 0.500 | 0.519 |
| General | RUBQ | 0.652 | 0.6646 | - | 0.317 | 0.636 | 0.382 |
| General | MMLU PRO | 0.606 | 0.6176 | 0.410 | 0.685 | 0.501 | 0.410 |
| General | MMLU EN | 0.740 | 0.7298 | 0.600 | 0.708 | 0.599 | 0.594 |
| General | BBH | 0.453 | 0.5758 | 0.3317 | 0.717 | 0.416 | 0.131 |
| General | SuperGPQA | 0.273 | 0.2939 | 0.209 | 0.375 | 0.246 | 0.201 |
| Code | Human Eval Plus | 0.695 | 0.7317 | 0.628 | 0.878 | 0.701 | 0.713 |
| Total | Average | 0.586 | 0.631 | 0.458 | 0.612 | 0.514 | 0.421 |


## Arena Results

| Arena | GigaChat-2-Lite-30.1 | GigaChat-3-Lightning | **GigaChat-3.1-Lightning** | YandexGPT-5-Lite-8B | SmolLM3 | gemma-3-4b-it | Qwen3-4B | Qwen3-4B-Instruct-2507 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| Arena Hard Logs V3 | 23.700 | 14.3 | 46.700 | 17.9 | 18.1 | 38.7 | 27.7 | 61.5 |
| Validator SBS Pollux | 32.500 | 24.3 | 55.700 | 10.3 | 13.7 | 34.000 | 19.8 | 56.100 |
| Total Average | 28.100 | 19.3 | 51.200 | 14.1 | 15.9 | 36.35 | 23.75 | 58.800 |


## Usage Example

llama-cpp version>=8495 (58c81f7e8)

cmake version>=3.28.3

Build llama-cpp
```
git clone https://github.com/ggml-org/llama.cpp.git
cd llama.cpp
cmake -B build -DGGML_CUDA=ON # build for CUDA
cmake --build build --config Release --target llama-server -j 8
```

Start the server
```shell
# Specify the full path to your model.gguf
export MODEL_PATH="/path/to/your/model.gguf"

./build/bin/llama-server \
    -m $MODEL_PATH \
    -np 1 \
    -cb \
    -ctk q8_0 \
    -ctv q8_0 \
    -fa on \
    --n-gpu-layers 999 \ # number of layers on GPU
    --ctx-size 32768 \
    --port 8080 \
    --host 0.0.0.0 \
    --jinja
```

Request example
```shell
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
    "tool_choice": "none",
    "messages": [
      {
        "role": "user",
        "content": "Докажи теорему о неподвижной точке"
      }
    ],
    "max_tokens": 1000,
    "temperature": 0
  }'
```

Request example with function call
```shell
curl http://localhost:8080/v1/chat/completions \
  -H "Content-Type: application/json" \
  -d '{
  "temperature": 0,
  "messages": [
    {
      "role": "user",
      "content": "Какая сейчас погода в Москве?"
    }
  ],
  "tools": [
    {
      "type": "function",
      "function": {
        "name": "get_weather",
        "description": "Получить информацию о текущей погоде в указанном городе.",
        "parameters": {
          "type": "object",
          "properties": {
            "city": {
              "type": "string",
              "description": "Название города (например, Москва, Казань)."
            }
          },
          "required": ["city"]
        }
      }
    }
  ]
}'
```
