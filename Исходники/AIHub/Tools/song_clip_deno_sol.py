"""Opt-in sparse sampling for the Deno RTX video worker."""

import faulthandler
from functools import wraps
import json
import logging
from pathlib import Path
import sys
from types import SimpleNamespace

sys.path.insert(0, str(Path(__file__).resolve().parent))
from song_clip_deno import DenoMontage, montage


class AcceleratedDeno(DenoMontage):
    def __init__(self, request, job):
        overlay = Path(request["deno"]["torch"])
        if not overlay.is_absolute() or not overlay.is_dir():
            raise ValueError("Missing accelerated Deno runtime.")
        montage.reject_links(overlay)
        sys.path.insert(0, str(overlay))
        super().__init__(request, job)

    def prepare_attention(self, config):
        import comfy_kitchen as kitchen
        from comfy_extras.nodes_sparse_attention import BlockSparseAttention

        if (self.torch.__version__ != "2.10.0+cu130"
                or not Path(self.torch.__file__).resolve().is_relative_to(Path(config["torch"]).resolve())
                or not kitchen.sol_attn_is_available(self.device)):
            raise RuntimeError("SongClip.DenoAccelerateUnavailable")
        sage = super().prepare_attention(config)
        self.sparse_calls = 0

        def count(kernel):
            @wraps(kernel)
            def call(*args, **kwargs):
                result = kernel(*args, **kwargs)
                self.sparse_calls += 1
                return result

            return call

        kitchen.sol_attn = count(kitchen.sol_attn)
        kitchen.sol_attn_chunked = count(kitchen.sol_attn_chunked)

        def sparse(model):
            return BlockSparseAttention.execute(
                model, {"selection": "sol-attn", "tau": 1.3}, .2, .9,
                dense_blocks="", min_tokens=12288, extra_tokens=0,
                sink_conditioning="exact_kv_and_rows", verbose=False,
            )

        montage.emit("stage", stage="deno-acceleration")
        if sage is None:
            # Profiles that use PyTorch attention apply the same sparse node after
            # the sigma shift. The base worker continues to own dense fallback.
            shift = self.shift

            def execute(*args, **kwargs):
                return sparse(shift.execute(*args, **kwargs)[0])

            self.shift = SimpleNamespace(execute=execute)
            return None

        def patch(model, kernel, compile_model):
            return sparse(sage.patch(model, kernel, compile_model)[0])

        return SimpleNamespace(patch=patch)

    def generate_shot(self, shot, audio, images, prompt, seed):
        before = self.sparse_calls
        result = super().generate_shot(shot, audio, images, prompt, seed)
        montage.emit("deno-accelerated", index=shot["index"], calls=self.sparse_calls - before)
        return result


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, stream=sys.stderr, format="%(asctime)s %(levelname)s %(message)s", force=True)
    faulthandler.enable(file=sys.stderr)
    try:
        verify = len(sys.argv) == 3 and sys.argv[1] == "--verify"
        source = sys.argv[-1]
        request = json.loads(Path(source).read_text(encoding="utf-8-sig"))
        montage.NativeMontage = AcceleratedDeno
        montage.run(request, source, verify_only=verify)
    except Exception as error:
        montage.emit("error", fraction=0, message=str(error), type=type(error).__name__)
        logging.exception("Accelerated Deno RTX generation failed")
        sys.exit(1)
