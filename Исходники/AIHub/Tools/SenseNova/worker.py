"""Local SenseNova U1.5 worker. One loaded checkpoint serves both visual roles."""
import argparse
import base64
import contextlib
from io import BytesIO
import json
from pathlib import Path
import sys
import traceback
import zipfile


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--model-root", type=Path, required=True)
    parser.add_argument("--packages", type=Path, required=True)
    parser.add_argument("--device", required=True)
    options = parser.parse_args()
    root = options.model_root
    options.packages.mkdir()
    for wheel in sorted((root / "wheels").glob("*.whl")):
        with zipfile.ZipFile(wheel) as archive:
            archive.extractall(options.packages)
    sys.path[:0] = [str(root / "runtime" / "src"), str(options.packages)]

    with contextlib.redirect_stdout(sys.stderr):
        engine = Engine(root, options.device)
    print(json.dumps({"ready": True, "capacity": engine.capacity}), flush=True)
    for line in sys.stdin:
        try:
            request = json.loads(line)
            with contextlib.redirect_stdout(sys.stderr):
                value = engine.run(request)
            print(json.dumps({"ok": True, "value": value}, ensure_ascii=False), flush=True)
        except Exception:
            traceback.print_exc(file=sys.stderr)
            print(json.dumps({"ok": False}), flush=True)
            return 1
    return 0


class Engine:
    def __init__(self, root, device):
        import torch
        from sensenova_u1.utils import load_and_merge_lora_weight_from_safetensors, load_model_and_tokenizer

        self.torch = torch
        self.device = torch.device(device)
        if self.device.type == "cuda":
            free, total = torch.cuda.mem_get_info(self.device)
            torch.cuda.set_per_process_memory_fraction(min(0.9, 0.9 * free / total), self.device)
        self.model, self.tokenizer = load_model_and_tokenizer(
            str(root / "weights"), dtype=torch.bfloat16, device=self.device, for_offload=True)
        adapter, = (root / "adapter").glob("*8step-V2.safetensors")
        self.model = load_and_merge_lora_weight_from_safetensors(self.model, str(adapter))
        self.capacity = self.model.config.llm_config.max_position_embeddings

    def offload(self):
        from sensenova_u1.utils import make_offload_ctx

        return make_offload_ctx(self.model, 2, self.device)

    def run(self, request):
        operation = request["operation"]
        with self.torch.inference_mode():
            if operation in ("count", "chat"):
                return self.chat(request, count=operation == "count")
            if operation == "draw":
                return self.draw(request)
        raise ValueError("Unknown SenseNova operation")

    def chat(self, request, count):
        from PIL import Image
        from sensenova_u1.models.neo_unify.conversation import get_conv_template
        from sensenova_u1.models.neo_unify.utils import load_image_native

        torch = self.torch
        template = get_conv_template(self.model.template)
        image_tensors, image_grids = [], []
        for message in request["messages"]:
            role, content = message["role"], message["content"]
            if role == "system":
                template.system_message = content
                continue
            if isinstance(content, list):
                text = []
                for item in content:
                    if item["type"] == "text":
                        text.append(item["text"])
                    elif item["type"] == "image_url":
                        data = item["image_url"]["url"]
                        prefix, encoded = data.split(",", 1)
                        if prefix not in ("data:image/jpeg;base64", "data:image/png;base64"):
                            raise ValueError("Expected a local embedded image")
                        with Image.open(BytesIO(base64.b64decode(encoded, validate=True))) as image:
                            pixels, grid = load_image_native(image, max_pixels=1024 * 1024)
                        image_tensors.append(pixels)
                        image_grids.append(grid)
                        text.append("<image>")
                content = "\n".join(text)
            template.append_message(template.roles[0 if role == "user" else 1], content)
        template.append_message(template.roles[1], None)
        prompt = template.get_prompt() + "<think>\n\n</think>\n\n"
        grid = torch.cat(image_grids) if image_grids else torch.empty((0, 2), dtype=torch.long)
        for height, width in grid.tolist():
            tokens = int(height * width * self.model.downsample_ratio ** 2)
            prompt = prompt.replace("<image>", "<img>" + "<IMG_CONTEXT>" * tokens + "</img>", 1)
        inputs = self.tokenizer(prompt, return_tensors="pt")
        if count:
            return inputs.input_ids.shape[-1]
        self.model.img_context_token_id = self.tokenizer.convert_tokens_to_ids("<IMG_CONTEXT>")
        self.model.img_start_token_id = self.tokenizer.convert_tokens_to_ids("<img>")
        with self.offload() as model:
            result = model.generate(
                pixel_values=torch.cat(image_tensors).to(self.device, dtype=self.model.dtype) if image_tensors else None,
                input_ids=inputs.input_ids.to(self.device), attention_mask=inputs.attention_mask.to(self.device),
                grid_hw=grid.to(self.device), max_new_tokens=request["max_tokens"], do_sample=False,
                eos_token_id=self.tokenizer.convert_tokens_to_ids(template.sep.strip()))
        answer = self.tokenizer.decode(result[0], skip_special_tokens=True).strip()
        if "</think>" in answer:
            answer = answer.rsplit("</think>", 1)[-1].strip()
        if not answer or answer.startswith("<think>"):
            raise ValueError("SenseNova did not return a final answer")
        return answer

    def draw(self, request):
        from PIL import Image

        images = []
        for source in request["images"]:
            with Image.open(source) as photo:
                image = photo.convert("RGB")
                image.thumbnail((1024, 1024))
                images.append(image)
        options = dict(image_size=(request["width"], request["height"]), cfg_scale=1.0, timestep_shift=3.0,
                       num_steps=8, seed=request["seed"])
        with self.offload() as model:
            if images:
                output = model.it2i_generate(self.tokenizer, request["prompt"], images, **options)
            else:
                output = model.t2i_generate(self.tokenizer, request["prompt"], **options)
        pixels = (((output[0].cpu().float().permute(1, 2, 0) + 1) / 2).clamp(0, 1) * 255)
        Image.fromarray(pixels.to(self.torch.uint8).numpy()).save(request["output"], format="PNG")
        return True


if __name__ == "__main__":
    sys.exit(main())
