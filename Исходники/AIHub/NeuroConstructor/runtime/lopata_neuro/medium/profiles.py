import json
from functools import lru_cache
from pathlib import Path

FAMILIES = {
    "medium": ("Qwen/Qwen3-ASR-0.6B","Qwen/Qwen3-VL-2B-Instruct","catalog.json"),
    "heavy": ("Qwen/Qwen3-ASR-1.7B","Qwen/Qwen3-VL-8B-Instruct","heavy.json"),
}


@lru_cache(maxsize=2)
def catalog(kind):
    return json.loads(Path(__file__).with_name(FAMILIES[kind][2]).read_text("utf-8"))


def visual_ids(kind):
    return ["OmniParser/detect-v3","OmniParser/icon-caption",FAMILIES[kind][1].split("/")[1].removesuffix("-Instruct"),
            "PaddleOCR-VL","PP-OCRv5/det","PP-OCRv5/en","PP-OCRv5/eslav","PP-DocLayoutV2"]
