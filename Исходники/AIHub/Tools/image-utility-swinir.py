"""LOPATA single-image SwinIR classical SR worker; no network or automatic downloads.

Architecture/checkpoints: JingyunLiang/SwinIR (Apache-2.0), DF2K s64w8.
Tiled inference uses CPU accumulation to keep the enlarged canvas out of VRAM.
"""
import argparse
import importlib.util
import os
from pathlib import Path
import sys


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--dependencies", required=True)
    parser.add_argument("--check", action="store_true")
    parser.add_argument("--input")
    parser.add_argument("--output")
    parser.add_argument("--weights")
    parser.add_argument("--scale", type=int, choices=(2, 3, 4, 8), default=4)
    parser.add_argument("--tile", type=int, default=256)
    parser.add_argument("--overlap", type=int, default=32)
    parser.add_argument("--device", default="auto")
    args = parser.parse_args()
    # Embedded Python's isolated _pth may omit the worker directory.
    sys.path.insert(0, str(Path(__file__).resolve().parent))
    sys.path.insert(0, args.dependencies)
    import numpy as np
    import torch
    from PIL import Image
    from runtime_hardware import require_supported_torch
    require_supported_torch(torch)
    if args.check:
        print("LOPATA_READY", flush=True)
        return
    if not args.input or not args.output or not args.weights:
        parser.error("input, output and weights are required")
    if Path(args.output).exists() or Path(args.output).resolve() == Path(args.input).resolve():
        raise ValueError("Output must be a new separate file.")
    if args.tile < 0 or args.tile and (args.tile % 8 or args.tile < 8 or args.overlap >= args.tile) or args.overlap < 0:
        raise ValueError("Invalid tile/overlap.")
    torch.set_num_threads(max(1, min(8, (os.cpu_count() or 2) // 2)))
    if args.device == "auto":
        from runtime_hardware import select_torch_device
        selected, memory, reason = select_torch_device(torch, "auto")
        device = torch.device(selected)
        print(f"LOPATA_DEVICE {selected}: {reason}; memory={memory}", flush=True)
    else:
        # Preserve the user's explicit device choice; a failed device must not
        # silently run a different profile.
        device = torch.device(args.device)
        print(f"LOPATA_DEVICE {device}: explicit prepared device", flush=True)
    spec = importlib.util.spec_from_file_location("lopata_swinir", Path(__file__).with_name("image-utility-swinir-network.py"))
    network = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(network)
    model = network.SwinIR(upscale=args.scale, in_chans=3, img_size=64, window_size=8,
                          img_range=1., depths=[6] * 6, embed_dim=180, num_heads=[6] * 6,
                          mlp_ratio=2, upsampler="pixelshuffle", resi_connection="1conv")
    checkpoint = torch.load(args.weights, map_location="cpu", weights_only=True)
    model.load_state_dict(checkpoint.get("params", checkpoint), strict=True)
    del checkpoint
    model.eval().to(device)
    with Image.open(args.input) as original:
        rgba = original.convert("RGBA") if "A" in original.getbands() or "transparency" in original.info else None
        rgb = original.convert("RGB")
        pixels = np.array(rgb, dtype=np.float32) / 255.
    height, width = pixels.shape[:2]
    padded_h = ((height + 7) // 8) * 8
    padded_w = ((width + 7) // 8) * 8
    # Symmetric extension also handles one-pixel-wide inputs.
    pixels = np.pad(pixels, ((0, padded_h - height), (0, padded_w - width), (0, 0)), mode="symmetric")
    tensor = torch.from_numpy(pixels.transpose(2, 0, 1).copy()).unsqueeze(0)
    with torch.inference_mode():
        if args.tile == 0:
            result = model(tensor.to(device)).float().cpu()
            print("LOPATA_PROGRESS 1", flush=True)
        else:
            tile = min(args.tile, padded_h, padded_w)
            # Very small images can be smaller than the requested overlap.
            overlap = min(args.overlap, tile - 1)
            stride = tile - overlap
            rows = list(range(0, padded_h - tile, stride)) + [padded_h - tile]
            cols = list(range(0, padded_w - tile, stride)) + [padded_w - tile]
            result = torch.zeros((1, 3, padded_h * args.scale, padded_w * args.scale), dtype=torch.float32)
            counts = torch.zeros((1, 1, padded_h * args.scale, padded_w * args.scale), dtype=torch.float32)
            count = 0
            for y in rows:
                for x in cols:
                    patch = tensor[..., y:y + tile, x:x + tile].to(device)
                    prediction = model(patch).float().cpu()
                    ys, xs, side = y * args.scale, x * args.scale, tile * args.scale
                    result[..., ys:ys + side, xs:xs + side].add_(prediction)
                    counts[..., ys:ys + side, xs:xs + side].add_(1.)
                    count += 1
                    print(f"LOPATA_PROGRESS {count / (len(rows) * len(cols)):.6f}", flush=True)
                    del patch, prediction
            result.div_(counts)
    result = result[..., :height * args.scale, :width * args.scale]
    # Outside inference_mode, create a normal tensor before in-place conversion.
    array = result.squeeze(0).clamp(0, 1).permute(1, 2, 0).mul_(255).round_().byte().numpy()
    output = Image.fromarray(array)
    if rgba is not None:
        output.putalpha(rgba.getchannel("A").resize(output.size, Image.Resampling.LANCZOS))
    output.save(args.output, format="PNG")


if __name__ == "__main__":
    main()
