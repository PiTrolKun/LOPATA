---

license: mit
language:

- ru
- en
pipeline_tag: sentence-similarity
tags:
- sentence-transformers
- feature-extraction
- sentence-similarity
- transformers
- embeddings
library_name: sentence-transformers

---

## GigaChat 3B Bidirectional Embedding Model

- Базовая LLM: собственная предобученная модель с архитектурой Qwen3 (36 слоёв, hidden 2048, 16 attention-голов / 8 KV-голов, head_dim 128), self-attention сделан **двунаправленным** (encoder-style)
- Тип пулинга: Mean pooling (усреднение)
- Размерность эмбеддинга: 2048
- Параметры: ~3B (веса в формате `bfloat16`)

Следующая итерация серии Giga-Embeddings. Модель текстовых эмбеддингов на основе архитектуры Qwen3, адаптированная под **двунаправленное (encoder-style) внимание** и обученная с контрастивной функцией потерь (InfoNCE). Модель строит плотные эмбеддинги предложений/абзацев для задач поиска (retrieval), семантического сравнения, классификации и кластеризации, показывая высокое качество на русском и английском языках.

## Пулинг и нормализация

Модель обучалась с **mean pooling + L2-нормализацией**. Использование CLS/last-token пулинга даст неверные результаты. Если вы используете `transformers` напрямую, необходимо самостоятельно усреднить (mean-pool) по не-паддинговым токенам и затем применить L2-нормализацию (см. пример ниже). В примерах для `sentence-transformers` и vLLM это делается автоматически. Сравнивайте эмбеддинги через косинусную близость (скалярное произведение нормированных векторов).

## Инструктивность

Модель обучалась в **инструктивном стиле**: для retrieval и других асимметричных задач необходимо добавлять инструкцию к **запросу** (query), а документы кодируются как есть, без инструкции. Формат:

```
Instruct: {описание задачи}
Query: {ваш текст}
```

Для **симметричных задач** (STS, дедупликация) можно использовать общую инструкцию либо не использовать её вовсе. Инструкцию выбирают под конкретную задачу — единственного «правильного» промпта не существует. Важно отметить, что инструкцию нужно добавлять только перед запросом, а не перед документом.

## FAQ

1. Нужно ли добавлять инструкции к запросу?

Для асимметричных задач (retrieval) — да, добавьте к запросу инструкцию из одного предложения, описывающую задачу. Документы кодируются как есть, без инструкции. Для симметричных задач (STS, дедупликация) можно использовать общую инструкцию либо не использовать её вовсе.

2. Какой пулинг использовать?

**Mean pooling** (усреднение) по не-паддинговым токенам с последующей **L2-нормализацией**. Использование CLS/last-token пулинга даст неверные результаты.

3. Почему мои воспроизведённые результаты немного отличаются от указанных в карточке модели?

Разные версии библиотек `transformers` и `pytorch` могут вызывать незначительные, но ненулевые различия в результатах.

---



## GigaChat 3B Bidirectional Embedding Model

- Base LLM: self-pretrained model with Qwen3 architecture (36 layers, hidden 2048, 16 attention heads / 8 KV heads, head_dim 128), self-attention made **bidirectional** (encoder-style)
- Pooling Type: Mean pooling
- Embedding Dimension: 2048
- Parameters: ~3B (weights are `bfloat16`)

The next iteration of the Giga-Embeddings series. A text embedding model based on the Qwen3 architecture, adapted for **bidirectional (encoder-style) attention** and trained with a contrastive (InfoNCE) objective. It produces dense sentence/passage embeddings for retrieval, semantic similarity, classification and clustering, with strong Russian and English performance.

## Pooling & normalization (important)

This model was trained with **mean pooling + L2 normalization**. Using CLS/last-token pooling will give wrong results. If you use `transformers` directly, you must mean-pool over non-padding tokens yourself and then L2-normalize (see the example below). The `sentence-transformers` and vLLM examples do this for you. Compare embeddings with cosine similarity (dot product of normalized vectors).

## Instructions / prompts

The model was trained in the **instruction style**: for retrieval and other asymmetric tasks, prepend a task instruction to the **query** (documents are embedded raw). The format is:

```
Instruct: {task description}
Query: {your text}
```

For symmetric tasks (STS, deduplication) you can either use a generic instruction or none at all. Choose the instruction per task; there is no single "correct" prompt.

## FAQ

1. Do I need to add instructions to the query?

For asymmetric tasks (retrieval), yes — prepend a one-sentence task instruction to the query. Documents are embedded raw, without an instruction. For symmetric tasks (STS, deduplication) you can use a generic instruction or none at all.

2. Which pooling should I use?

**Mean pooling** over non-padding tokens, followed by **L2 normalization**. Using CLS/last-token pooling will give wrong results.

3. Why are my reproduced results slightly different from those reported?

Different versions of the `transformers` and `pytorch` libraries can cause small but non-zero differences in results.

---



## Metrics\*


| Benchmark                | old 3b  | Giga-Embeddings-instruct-3B-0826     | Giga-Embeddings-instruct-10B-A1.8B-0826 |
| ----------------------- | ----- | --------- | ------------------------ |
| **MTEB (rus)**          | 74.16 | <u>74.57</u> | **74.99** |
| **MTEB (eng)**          | 71.07 | <u>71.93</u> | **72.23** |
| **MTEB (code)**         | 62.37 | <u>76.93</u> | **78.40** |
| **MTEB (multilingual)** | 55.51 | <u>63.9</u>  | **65.60**|




| Model / backend            | 512 tok   | 1024 tok   | 2048 tok   | throughput vs 10B-A1.8B   |
|:---------------------------|:------------|:-------------|:-------------|:--------------------------|
| Giga-Embeddings-instruct-3B-0826 / vLLM              | 87.9k/s       | 91.5k/s        | 90.4k/s        | 0.8x                      |
| Giga-Embeddings-instruct-10B-A1.8B-0826 / vLLM             | **112.6k/s**      | **114.5k/s**       | **102.3k**/s       | **1.0x**                      |
| Nemotron 8B / vLLM         | 42.6k/s       | 43.2k/s        | 41.7k/s        | 0.38x                     |
| Qwen3 Embedding 4B / vLLM  | 70.1k/s       | 73.2k/s        | 71.2k/s        | 0.64x                     |
| F2LLM-v2-8B / vLLM         | 43.2k/s       | 43.4k/s        | 42.6k/s        | 0.38x                     |
| NV-Embed-v2 / Transformers | 25.6k/s       | 26.2k/s        | 25.6k/s        | 0.23x                     |


\* All metrics were measured on an H100 GPU with a batch size of 16.

---



## Usage


<details>
<summary>Sentence Transformers</summary>

```python
from sentence_transformers import SentenceTransformer

model = SentenceTransformer(
    "ai-sage/Giga-Embeddings-instruct-3B-0826",
    trust_remote_code=True,   # needed for the bidirectional modeling code
)

instruction = "Given a query, retrieve relevant passages"
queries = [f"Instruct: {instruction}\nQuery: Где столица России?"]
documents = ["Москва — столица Российской Федерации.",
             "Париж — столица Франции."]

q_emb = model.encode(queries, normalize_embeddings=True)
d_emb = model.encode(documents, normalize_embeddings=True)
print(model.similarity(q_emb, d_emb))
```
</details>


<details>
<summary>Transformers (manual mean pooling)</summary>

```python
import torch
import torch.nn.functional as F
from transformers import AutoModel, AutoTokenizer

path = "ai-sage/Giga-Embeddings-instruct-3B-0826"
tok = AutoTokenizer.from_pretrained(path, trust_remote_code=True)
model = AutoModel.from_pretrained(path, trust_remote_code=True,
                                  dtype=torch.bfloat16).cuda().eval()

def encode(texts):
    enc = tok(texts, return_tensors="pt", padding=True, truncation=True, max_length=512)
    enc = {k: v.cuda() for k, v in enc.items()}
    with torch.no_grad():
        hidden = model(**enc).last_hidden_state
    mask = enc["attention_mask"].unsqueeze(-1).to(hidden.dtype)
    emb = (hidden * mask).sum(1) / mask.sum(1).clamp(min=1e-6)   # mean pool
    return F.normalize(emb, dim=-1)                              # L2 normalize

instr = "Given a query, retrieve relevant passages"
q = encode([f"Instruct: {instr}\nQuery: Где столица России?"])
d = encode(["Москва — столица Российской Федерации.", "Париж — столица Франции."])
print((q @ d.T).cpu())
```

</details>

<details>
<summary>vLLM</summary>

vLLM serves this model as an embedding model using its native Qwen3 implementation. Bidirectional attention is enabled with `is_causal=false`; no custom code is required on the vLLM side.

Install the latest vLLM release from PyPI:

```bash
pip install --upgrade vllm
```

```python
from vllm import LLM
from vllm.config import PoolerConfig

llm = LLM(
    model="ai-sage/Giga-Embeddings-instruct-3B-0826",
    runner="pooling",
    convert="embed",
    hf_overrides={"is_causal": False, "architectures": ["Qwen3ForCausalLM"]},
    pooler_config=PoolerConfig(pooling_type="MEAN", use_activation=True),
    trust_remote_code=True,
)

instr = "Given a query, retrieve relevant passages"
outs = llm.encode([f"Instruct: {instr}\nQuery: Где столица России?",
                   "Москва — столица Российской Федерации."],
                  pooling_task="embed")
embs = [o.outputs.data for o in outs]
```

Or via the OpenAI-compatible server:

```bash
vllm serve ai-sage/Giga-Embeddings-instruct-3B-0826 \
    --runner pooling --convert embed \
    --hf-overrides '{"is_causal": false, "architectures": ["Qwen3ForCausalLM"]}' \
    --pooler-config '{"pooling_type": "MEAN", "use_activation": true}' \
    --trust-remote-code
```

The examples use `--pooler-config` and `use_activation=True` for mean pooling with L2 normalization.

</details>

<details>
<summary>SGLang</summary>

Support comes from PR **[#35531 – *[Model] Add qwen3 bidirectional
embedding*](https://github.com/sgl-project/sglang/pull/35531)**, which adds the
`Qwen3BidirectionalModel` architecture used by
[`ai-sage/Giga-Embeddings-instruct-3B-0826`](https://huggingface.co/ai-sage/Giga-Embeddings-instruct-3B-0826).

- **Source branch:** `feat/qwen3-bidirectional-embedding` on fork `Lossfull/sglang`
- **Pinned commit:** `604a3634d235b11dcf4abd4bc012cfa1f7bde43b`
- The change is **pure Python** (no CUDA/kernel rebuild needed).

> Once the PR is merged this whole guide collapses to "use a recent SGLang
> release." Until then, use one of the two methods below.

## 1. Official SGLang Docker + apply the PR patch (recommended)

The official image already ships SGLang as an **editable install** with all CUDA
kernels prebuilt. The PR is pure Python, so you just **patch the files in
place** — nothing is compiled, and the Python source stays matched to the
image's kernels. This is the method that was verified end-to-end for this guide.

```bash
# 1. Start the verified image (its SGLang is editable at /sgl-workspace/sglang).
docker run --gpus all -it --shm-size 16g \
  -p 30000:30000 \
  -v ~/.cache/huggingface:/root/.cache/huggingface \
  --entrypoint /bin/bash \
  lmsysorg/sglang:nightly-dev-20260818-c0b6474b

# --- everything below runs INSIDE the container ---

# 2. Download the PR diff.
curl -fL -H "Accept: application/vnd.github.v3.diff" \
  -o /tmp/pr35531.diff \
  https://api.github.com/repos/sgl-project/sglang/pulls/35531

# 3. Apply it onto the image's editable source tree (takes effect immediately).
cd /sgl-workspace/sglang
git apply -v /tmp/pr35531.diff        # or: patch -p1 < /tmp/pr35531.diff

# 4. Sanity check: the new architecture must resolve to the native class.
python3 -c "from sglang.srt.models.registry import ModelRegistry; \
c,a=ModelRegistry.resolve_model_cls('Qwen3BidirectionalModel'); \
print('OK:', a, '->', c.__module__)"
# Expect: OK: Qwen3BidirectionalModel -> sglang.srt.models.qwen3_embedding
```
## 2. Build from source (no Docker)

Use this on a bare CUDA machine (or a plain PyTorch container). It builds the
matching kernels, so it is heavier but fully self-contained.

```bash
# Clone the PR branch (or the exact commit).
git clone https://github.com/Lossfull/sglang.git
cd sglang
git checkout feat/qwen3-bidirectional-embedding
# Optional: pin the exact reviewed commit
# git checkout 604a3634d235b11dcf4abd4bc012cfa1f7bde43b

# Install SGLang + all runtime deps (compiles/pulls sgl-kernel, flashinfer, ...).
pip install --upgrade pip
pip install -e "python[all]"
```

## 3. Serve the model

Same command regardless of install method:

```bash
python3 -m sglang.launch_server \
  --model-path ai-sage/Giga-Embeddings-instruct-3B-0826 \
  --is-embedding \
  --trust-remote-code \
  --host 0.0.0.0 --port 30000
```

- `--is-embedding` — serve as an embedding model (the arch is auto-classified as
  non-generative anyway, but this is explicit and safe).
- `--trust-remote-code` — **required** (custom config class in the checkpoint).
- Disabled CUDA graph / radix cache / chunked prefill are applied
  automatically — you don't set them.
- Multi-GPU: add `--tp-size N` if you want to shard across GPUs.

The server is ready when you see: `The server is fired up and ready to roll!`

---

## 4. Test it

### cURL

```bash
curl -s http://localhost:30000/v1/embeddings \
  -H "Content-Type: application/json" \
  -d '{
        "model": "ai-sage/Giga-Embeddings-instruct-3B-0826",
        "input": "What is the capital of France?"
      }' | python3 -c "import sys,json; d=json.load(sys.stdin); \
e=d['data'][0]['embedding']; print('dim:', len(e), 'first5:', e[:5])"
```



</details>

<details>
  <summary>Fine-tune guide</summary>

  # Finetuning Giga-Embeddings with ms-swift

This guide shows how to contrastively finetune the **Giga-Embeddings** models with
[ms-swift](https://github.com/modelscope/ms-swift) using an InfoNCE loss, for both
**LoRA** and **full-parameter** training.

Covered models (HuggingFace):

| Model | Size | Architecture |
|---|---|---|
| [`ai-sage/Giga-Embeddings-instruct-480M-0826`](https://huggingface.co/ai-sage/Giga-Embeddings-instruct-480M-0826) | 0.48B | Qwen3 bidirectional |
| [`ai-sage/Giga-Embeddings-instruct-3B-0826`](https://huggingface.co/ai-sage/Giga-Embeddings-instruct-3B-0826) | 3B | Qwen3 bidirectional |
| [`ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826`](https://huggingface.co/ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826) | 10B MoE (A1.8B) | DeepSeek-V3 bidirectional |

All are mean-pooling `SentenceTransformer` models, so we load them through ms-swift's
`SentenceTransformersLoader`.

---

## Requirements

```bash
# Install the latest ms-swift release from PyPI.
pip install --upgrade ms-swift

# sentence-transformers pinned to 5.3.0.
pip install "sentence-transformers==5.3.0"
```

torch and transformers are pulled in automatically. A CUDA GPU is required for training.

For **full-parameter finetuning of the 10B MoE model** (multi-GPU, DeepSpeed ZeRO-3),
also install DeepSpeed:

```bash
pip install deepspeed
```

---

## 1. Register the model

The Giga-Embeddings architectures aren't in ms-swift's built-in registry, so register
them once and point them at `SentenceTransformersLoader`. Save this as
`custom_register.py`:

```python
# custom_register.py
from swift.model import Model, ModelGroup, ModelMeta, register_model
from swift.model.register import SentenceTransformersLoader
from swift.template import TemplateType

# Qwen3-bidirectional models (480M, 3B)
register_model(ModelMeta(
    'giga_embeddings',
    [ModelGroup([
        Model('ai-sage/Giga-Embeddings-instruct-480M-0826', 'ai-sage/Giga-Embeddings-instruct-480M-0826'),
        Model('ai-sage/Giga-Embeddings-instruct-3B-0826', 'ai-sage/Giga-Embeddings-instruct-3B-0826'),
    ])],
    SentenceTransformersLoader,
    template=TemplateType.dummy,
    architectures=['Qwen3BidirectionalModel'],
))

# DeepSeek-V3-bidirectional MoE model (10B-A1.8B)
#
# Under DeepSpeed ZeRO-3, MoE routing makes different ranks run different expert
# submodules, which breaks ZeRO-3's per-parameter cross-rank coordination
# ("Detected a disagreement on list length between rank0 and rankN"). The fix is to
# mark the MoE block as a ZeRO-3 leaf module. We do it in a small loader subclass
# (harmless when ZeRO-3 / DeepSpeed isn't used — only needed for the 10B on ZeRO-3).
class GigaMoESentenceTransformersLoader(SentenceTransformersLoader):
    def get_model(self, model_dir, config, processor, model_kwargs):
        model = super().get_model(model_dir, config, processor, model_kwargs)
        try:
            from deepspeed.utils import set_z3_leaf_modules
            set_z3_leaf_modules(model, ['DeepseekV3MoE'])
        except Exception:
            pass
        return model

register_model(ModelMeta(
    'giga_embeddings_moe',
    [ModelGroup([
        Model('ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826', 'ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826'),
    ])],
    GigaMoESentenceTransformersLoader,
    template=TemplateType.dummy,
    architectures=['DeepseekV3BidirectionalModel'],
))
```

Pass it to any `swift sft` command with `--custom_register_path custom_register.py`.

---

## 2. Prepare your dataset

One JSON object per line. Each row is a query with one positive document and any
number of hard negatives:

```json
{"messages": [{"role": "user", "content": "What is the capital of France?"}],
 "positive_messages": [[{"role": "assistant", "content": "Paris is the capital of France."}]],
 "negative_messages": [[{"role": "assistant", "content": "Berlin is the capital of Germany."}],
                       [{"role": "assistant", "content": "The Eiffel Tower is a landmark."}]]}
```

- `messages` — the query / anchor.
- `positive_messages` — list of positive documents (≥ 1).
- `negative_messages` — list of hard negatives (optional but recommended).

ms-swift lays each row out as anchor + positive + negatives with labels
`[1, 0, 0, …]`, which is what the InfoNCE loss consumes.

If your retrieval task uses an instruction prefix, prepend it to the query text (the
models were trained with `Instruct: <task>\nQuery: <query>`).

---

## 3. Train

The examples use these InfoNCE environment variables (they are read from the
environment, not passed as CLI flags):

```bash
export INFONCE_TEMPERATURE=0.05
export INFONCE_USE_BATCH=True       # in-batch negatives (see note below)
export INFONCE_HARD_NEGATIVES=7     # hard negatives per query (match your data)
```

> **In-batch negatives (`INFONCE_USE_BATCH`)**: keep `True` for general retrieval data
> where each query has a distinct positive. Set it to `False` if your dataset has a
> **small set of shared positive documents** (e.g. many queries mapping to the same
> handful of answers) — otherwise another query's positive becomes a false negative for
> yours and hurts training. When `False`, rely on the curated hard negatives.

### LoRA

```bash
swift sft \
  --custom_register_path custom_register.py \
  --model_type giga_embeddings \
  --model ai-sage/Giga-Embeddings-instruct-480M-0826 \
  --use_hf true \
  --task_type embedding \
  --loss_type infonce \
  --tuner_type lora \
  --lora_rank 8 --lora_alpha 32 \
  --dataset ./train.jsonl \
  --split_dataset_ratio 0.0 \
  --max_length 512 \
  --num_train_epochs 1 \
  --per_device_train_batch_size 8 \
  --learning_rate 1e-4 \
  --torch_dtype bfloat16 \
  --attn_impl sdpa \
  --logging_steps 5 --save_steps 500 \
  --output_dir ./output
```

### Full parameter

Same as above with `--tuner_type full` and a lower learning rate. For the 480M and 3B
this fits comfortably on a single 80 GB GPU:

```bash
swift sft \
  --custom_register_path custom_register.py \
  --model_type giga_embeddings \
  --model ai-sage/Giga-Embeddings-instruct-3B-0826 \
  --use_hf true \
  --task_type embedding --loss_type infonce \
  --tuner_type full \
  --dataset ./train.jsonl --split_dataset_ratio 0.0 \
  --max_length 512 --num_train_epochs 1 \
  --per_device_train_batch_size 4 --learning_rate 1e-5 \
  --torch_dtype bfloat16 --attn_impl sdpa \
  --logging_steps 5 --save_steps 500 \
  --output_dir ./output
```

### Multi-GPU

For multiple GPUs, launch with `NPROC_PER_NODE`. Plain DDP (no ZeRO) works well and
fits full-parameter finetuning of the 3B on 8×80 GB:

```bash
NPROC_PER_NODE=8 CUDA_VISIBLE_DEVICES=0,1,2,3,4,5,6,7 swift sft \
  --custom_register_path custom_register.py \
  --model_type giga_embeddings \
  --model ai-sage/Giga-Embeddings-instruct-3B-0826 \
  --use_hf true \
  --task_type embedding --loss_type infonce \
  --tuner_type full \
  --dataset ./train.jsonl --split_dataset_ratio 0.0 \
  --max_length 2048 --num_train_epochs 1 \
  --per_device_train_batch_size 4 \
  --learning_rate 1e-5 --warmup_ratio 0.03 --lr_scheduler_type cosine \
  --gradient_checkpointing true \
  --torch_dtype bfloat16 --attn_impl sdpa \
  --dataloader_num_workers 4 --dataset_num_proc 8 \
  --logging_steps 5 --save_steps 500 --save_total_limit 3 \
  --output_dir ./output
```

Tips:
- `--dataset_num_proc N` parallelizes tokenization (helps for large datasets).
- With in-batch negatives on, they are gathered across **all** GPUs, giving a larger
  effective negative pool as you add GPUs.

---

## 4. The 10B MoE model

Use `--model_type giga_embeddings_moe` and
`--model ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826`. Everything above applies,
plus:

**Memory.** Full-parameter finetuning of the 10B does not fit on a single 80 GB GPU
with standard AdamW. Use **multi-GPU DeepSpeed ZeRO-3**:

```bash
NPROC_PER_NODE=8 swift sft \
  --custom_register_path custom_register.py \
  --model_type giga_embeddings_moe \
  --model ai-sage/Giga-Embeddings-instruct-10B-A1.8B-0826 \
  --use_hf true \
  --task_type embedding --loss_type infonce \
  --tuner_type full \
  --deepspeed zero3 \
  --gradient_checkpointing true \
  --dataset ./train.jsonl --split_dataset_ratio 0.0 \
  --max_length 2048 --num_train_epochs 1 \
  --per_device_train_batch_size 2 --learning_rate 1e-5 \
  --torch_dtype bfloat16 --attn_impl sdpa \
  --logging_steps 5 --save_steps 500 \
  --output_dir ./output
```

(Requires `pip install deepspeed`.) **LoRA** on the 10B fits on a single 80 GB GPU
without DeepSpeed — use `--tuner_type lora` as in §3.

**Expert routing.** ms-swift's embedding trainer does not add a MoE load-balancing
auxiliary loss. For long full-parameter runs, monitor expert utilization.

---

## 5. Verify a finetuned checkpoint

Reload with sentence-transformers and confirm positives score higher than negatives.
A correct full-parameter checkpoint reloads with **no** "missing keys" warnings:

```python
from sentence_transformers import SentenceTransformer

m = SentenceTransformer("./output/<run>/checkpoint-<N>", trust_remote_code=True, device="cuda")
emb = m.encode(
    ["What is the capital of France?",
     "Paris is the capital of France.",
     "Berlin is in Germany."],
    convert_to_tensor=True, normalize_embeddings=True,
)
print("cos(query, positive) =", float(emb[0] @ emb[1]))  # should be clearly higher
print("cos(query, negative) =", float(emb[0] @ emb[2]))
```

- **Full-parameter** output is a complete `SentenceTransformer` checkpoint — load the
  output directory directly.
- **LoRA** output is an adapter. Load the base model and apply the adapter, or merge
  first with `swift export --adapters ./output/<run>/checkpoint-<N> --merge_lora true`.

---

## InfoNCE options reference

Set via environment variables:

| Variable | Default | Meaning |
|---|---|---|
| `INFONCE_TEMPERATURE` | `0.1` | Softmax temperature (lower = sharper). |
| `INFONCE_USE_BATCH` | `True` | Use in-batch (and cross-GPU) negatives. |
| `INFONCE_HARD_NEGATIVES` | – | Hard negatives kept per query. |
| `INFONCE_MASK_FAKE_NEGATIVE` | `False` | Mask in-batch negatives scoring above the positive (guards against false negatives). |
| `INFONCE_INCLUDE_QQ` / `INFONCE_INCLUDE_DD` | `False` | Add query-query / doc-doc terms to the denominator (Qwen3-Embedding style). |

Key `swift sft` flags:

| Flag | Meaning |
|---|---|
| `--task_type embedding` | Enable embedding training (ST pooling + embedding trainer). |
| `--loss_type infonce` | InfoNCE contrastive loss. |
| `--tuner_type lora` / `full` | LoRA (default) vs full-parameter. |
| `--use_hf true` | Resolve `--model` from the HuggingFace Hub. |
| `--custom_register_path` | Path to `custom_register.py`. |
| `--split_dataset_ratio 0.0` | No automatic validation split. |

---

## Troubleshooting

- **`AttributeError: 'NoneType' object has no attribute 'items'` during save** — upgrade
  ms-swift with `pip install --upgrade ms-swift` to get the SentenceTransformer
  full-parameter save fix (see Requirements).
- **Reloaded model gives base-model quality / "missing keys" on load after full
  finetuning** — your `sentence-transformers` is newer than 5.3; pin `==5.3.0`.
- **Model downloads from ModelScope instead of HuggingFace (or is not found)** — add
  `--use_hf true`.

</details>

[Paper](https://arxiv.org/abs/2608.23806)