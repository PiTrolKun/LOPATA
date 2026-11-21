import json
import os
import runpy
import sys
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_TELEMETRY"] = "1"

import torch
from PIL import Image
from safetensors.torch import load_file
from transformers import Florence2Config, Florence2ForConditionalGeneration, Florence2Processor, GenerationConfig


def load_weights(directory):
    # The audited Microsoft checkpoint predates built-in Transformers support.
    # Apply the official converter's tensor names to its safetensors file.
    processor_dir = directory / "processor"
    converter = runpy.run_path(str(directory / "convert_mapping.py"))
    config = Florence2Config.from_pretrained(processor_dir, local_files_only=True)
    source = load_file(directory / "model.safetensors")
    mapping = converter["multi_modal_projector"]()
    for stage, depth in enumerate(config.vision_config.depths):
        mapping.extend(converter["vision_conv_embeddings"](stage))
        for block in range(depth):
            mapping.extend(converter["vision_spatial_block"](stage, block))
            mapping.extend(converter["vision_channel_block"](stage, block))
    mapping.extend(converter["language_model"](source))
    tied = ("model.language_model.shared.weight", "model.language_model.encoder.embed_tokens.weight",
            "model.language_model.decoder.embed_tokens.weight", "lm_head.weight")
    shared = source["language_model.model.shared.weight"]
    assert shared.shape[0] == config.image_token_id
    additions = config.text_config.vocab_size - shared.shape[0]
    embeddings = torch.cat((shared, shared.mean(0, keepdim=True).repeat(additions, 1))).float()
    weights = dict.fromkeys(tied, embeddings)
    for original, converted in mapping:
        if converted not in tied and original in source:
            value = source[original]
            weights[converted] = value.T.contiguous().float() if original == "image_projection" else value.float()
    unused = set(source).difference(original for original, _ in mapping)
    assert all(name == "language_model.final_logits_bias" and source[name].count_nonzero() == 0 for name in unused), unused
    with torch.device("meta"):
        model = Florence2ForConditionalGeneration(config)
    model.load_state_dict(weights, strict=True, assign=True)
    model.tie_weights()
    model.generation_config = GenerationConfig.from_pretrained(processor_dir, local_files_only=True)
    model.generation_config.suppress_tokens = list(range(config.image_token_id, config.text_config.vocab_size))
    return model.eval(), Florence2Processor.from_pretrained(processor_dir, local_files_only=True, use_fast=False)


def overlap(a, b):
    common = max(0, min(a[2], b[2]) - max(a[0], b[0])) * max(0, min(a[3], b[3]) - max(a[1], b[1]))
    return common / max(1, (a[2] - a[0]) * (a[3] - a[1]) + (b[2] - b[0]) * (b[3] - b[1]) - common)


def main(folder, weights):
    torch.set_num_threads(min(os.cpu_count() or 1, 6))
    torch.set_num_interop_threads(1)
    image = Image.open(folder / "source.jpg").convert("RGB")
    task = "<CAPTION_TO_PHRASE_GROUNDING>"
    text = (folder / "description.txt").read_text(encoding="utf-8")
    # Keep the chosen task fixed even when a description contains literal markup.
    description = text.replace("<", " ").replace(">", " ")
    model, processor = load_weights(weights)
    data = processor(images=image, text=task + description, return_tensors="pt")
    with torch.inference_mode():
        ids = model.generate(**data, max_new_tokens=160, num_beams=3, do_sample=False)
    answer = processor.batch_decode(ids, skip_special_tokens=False)[0]
    parsed = processor.post_process_generation(text=answer, task=task, image_size=image.size)[task]
    boxes = []
    for box in parsed["bboxes"]:
        scaled = [min(1000, max(0, round(value * 1000 / size))) for value, size in zip(box, image.size * 2)]
        if scaled[2] > scaled[0] and scaled[3] > scaled[1] and all(overlap(scaled, seen) < .75 for seen in boxes):
            boxes.append(scaled)
    (folder / "boxes.json").write_text(json.dumps(boxes), encoding="utf-8")


if __name__ == "__main__":
    main(*(Path(value) for value in sys.argv[1:]))
