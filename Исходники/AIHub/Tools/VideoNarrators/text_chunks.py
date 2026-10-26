"""Lossless plain-text packet planning, below each pinned engine's practical call budget."""
import re

# These are conservative invocation ceilings, not limits on a project or its text.
# Native tokenizers/chunkers still run inside each bounded invocation.
LIMITS = {'kokoro-sveta': 320, 'kokoro-heart': 320, 'kitten2': 240,
          'vosk010': 400, 'supertonic3': 240, 'qwen3-17b': 240,
          'qwen3-06b': 240, 'orpheus3b': 160, 'silero56': 360, 'cosyvoice300': 240}
PLAN_VERSION = 1


def units(text):
    # Number expansion can make a short written number much longer when spoken.
    return sum(4 if c.isdigit() else 1 for c in text)


def plan(text, engine):
    limit = LIMITS[engine]
    if not isinstance(text, str) or not text.strip():
        raise ValueError('Narration text is empty.')
    start = 0
    while start < len(text):
        end, cost = start, 0
        while end < len(text):
            size = 4 if text[end].isdigit() else 1
            if cost + size > limit:
                break
            cost += size
            end += 1
        window = text[start:end]
        paragraphs = list(re.finditer(r'\n[ \t]*\n+', window))
        if paragraphs:
            end = start + paragraphs[0].end()
        elif end < len(text):
            sentences = list(re.finditer(r'[.!?…]["\'»\)\]]*\s+', window))
            spaces = list(re.finditer(r'\s+', window))
            boundary = sentences[-1].end() if sentences else spaces[-1].end() if spaces else len(window)
            end = start + boundary
        if end <= start:
            raise ValueError('The narrator packet planner made no progress.')
        yield start, end, text[start:end]
        start = end
