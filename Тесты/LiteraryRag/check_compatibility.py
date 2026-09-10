"""No pretrained weights. Check the pinned wrapper using a tiny random encoder."""
import importlib
import importlib.util
import json
from pathlib import Path
import sys
import zipfile

root = Path(__file__).resolve().parent
wheel = next((root / "compatibility").glob("transformers-*.whl"))
target = root / "compatibility" / "packages"
with zipfile.ZipFile(wheel) as archive:
    archive.extractall(target)
sys.path.insert(0, str(target))
sys.path.insert(0, str(root / "compatibility"))
import torch
import transformers
config_module = importlib.import_module("tiny.configuration_gigarembed")
model_module = importlib.import_module("tiny.modeling_gigarembed")
config = config_module.Qwen3BidirectionalConfig(vocab_size=32, hidden_size=32,
    intermediate_size=64, num_hidden_layers=2, num_attention_heads=2,
    num_key_value_heads=2, head_dim=16, max_position_embeddings=128, layer_types=["full_attention"] * 2)
config._attn_implementation = "sdpa"
model = model_module.Qwen3BidirectionalModel(config).eval()
with torch.inference_mode():
    x = torch.tensor([[1, 2, 3, 4], [1, 2, 3, 5]])
    hidden = model(input_ids=x, attention_mask=torch.ones_like(x)).last_hidden_state
    assert hidden.shape == (2, 4, 32)
    assert not torch.allclose(hidden[0, 0], hidden[1, 0]), "Future token must affect first token in bidirectional encoder"
    one = model(input_ids=x[:1], attention_mask=torch.ones_like(x[:1])).last_hidden_state
    padded = model(input_ids=torch.tensor([[1, 2, 3, 4, 0, 0]]), attention_mask=torch.tensor([[1, 1, 1, 1, 0, 0]])).last_hidden_state
    assert torch.allclose(one, padded[:, :4], atol=1e-5), "Padding must not alter valid tokens"
spec = importlib.util.spec_from_file_location("worker", root.parents[1] / "Исходники/AIHub/Tools/giga_embeddings.py")
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)
offsets = [(i, i + 1) for i in range(1350)]
spans = list(worker.windows(offsets, 510))
assert spans[0][0] == 0 and spans[-1][1] == 1350
assert all(b - a <= 510 for a, b in spans)
assert all(spans[i][0] < spans[i-1][1] for i in range(1, len(spans)))
print(json.dumps(dict(passed=True, torch=torch.__version__, transformers=transformers.__version__,
                     weights="tiny random, not pretrained Giga", bidirectional=True, padding=True, token_windows=True)))
