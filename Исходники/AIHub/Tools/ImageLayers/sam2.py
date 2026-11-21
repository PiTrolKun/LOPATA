"""Build still-image masks from the frame and normalized target boxes."""

import json
import math
import os
import sys
from pathlib import Path

os.environ["HF_HUB_OFFLINE"] = "1"
os.environ["HF_HUB_DISABLE_PROGRESS_BARS"] = "1"

import numpy as np
import torch
from PIL import Image
from torch.nn import functional as F
from transformers import Sam2Model


class Sam2Image:
    def __init__(self, root):
        self.model, info = Sam2Model.from_pretrained(
            root, local_files_only=True, use_safetensors=True, output_loading_info=True)
        if info["missing_keys"] or info["mismatched_keys"]:
            raise RuntimeError("The SAM 2.1 checkpoint is missing weights for the image model.")
        self.model.eval()
        settings = json.loads((root / "preprocessor_config.json").read_text(encoding="utf-8"))
        self.size = (settings["size"]["height"], settings["size"]["width"])
        self.rescale = settings["rescale_factor"]
        self.mean = torch.tensor(settings["image_mean"])[None, :, None, None]
        self.std = torch.tensor(settings["image_std"])[None, :, None, None]

    @torch.inference_mode()
    def embed(self, image):
        values = torch.from_numpy(np.array(image, dtype=np.uint8)).permute(2, 0, 1)[None].float()
        resized = F.interpolate(values, self.size, mode="bilinear", align_corners=False, antialias=True).round()
        return self.model.get_image_embeddings((resized * self.rescale - self.mean) / self.std)

    @torch.inference_mode()
    def mask(self, size, box, embedding):
        width, height = size
        model_height, model_width = self.size
        scale = torch.tensor([model_width / width, model_height / height] * 2)
        result = self.model(image_embeddings=embedding, input_boxes=torch.tensor([[box]]) * scale,
                            multimask_output=False)
        logits = F.interpolate(result.pred_masks[0], (height, width), mode="bilinear", align_corners=False)
        score = float(result.iou_scores[0, 0, 0])
        return (logits[0, 0] > 0).numpy(), score if math.isfinite(score) else 0

    def cropped_mask(self, image, box):
        l, t, r, b = box
        pad_x, pad_y = (r - l) * .25, (b - t) * .25
        left, top = max(0, math.floor(l - pad_x)), max(0, math.floor(t - pad_y))
        right, bottom = min(image.width, math.ceil(r + pad_x)), min(image.height, math.ceil(b + pad_y))
        crop = image.crop((left, top, right, bottom))
        mask, score = self.mask(crop.size, [l - left, t - top, r - left, b - top], self.embed(crop))
        result = np.zeros((image.height, image.width), dtype=bool)
        result[top:bottom, left:right] = mask
        return result, score


def main(folder, weights):
    torch.set_num_threads(min(os.cpu_count() or 1, 8))
    torch.set_num_interop_threads(1)
    with Image.open(folder / "source.png") as opened:
        rgba = opened.convert("RGBA")
        image = Image.alpha_composite(Image.new("RGBA", rgba.size, (255, 255, 255, 255)), rgba).convert("RGB")
    targets = json.loads((folder / "boxes.json").read_text(encoding="utf-8"))
    worker = Sam2Image(weights)
    frame_embedding = None
    for index, target in enumerate(targets):
        box = [value * extent / 1000 for value, extent in zip(target, image.size * 2)]
        small = min(target[2] - target[0], target[3] - target[1]) < 50
        if small:
            mask, score = worker.cropped_mask(image, box)
        else:
            if frame_embedding is None:
                frame_embedding = worker.embed(image)
            mask, score = worker.mask(image.size, box, frame_embedding)
            if score < .5 or not mask.any():
                alternative, confidence = worker.cropped_mask(image, box)
                if alternative.any() and (not mask.any() or confidence > score):
                    mask = alternative
        (folder / f"output-{index}.bits").write_bytes(np.packbits(mask, bitorder="little").tobytes())


if __name__ == "__main__":
    main(Path(sys.argv[1]), Path(sys.argv[2]))
