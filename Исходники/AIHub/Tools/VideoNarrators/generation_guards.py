"""Refuse a token-budget cutoff where the pinned upstream exposes a stop signal."""


def cpp_eos(report):
    chunks = report.get('chunks', [])
    if not chunks or any(chunk.get('terminated') is not True for chunk in chunks):
        raise RuntimeError('Native narrator stopped without EOS; refusing truncated audio.')


def transformer_eos(model):
    original = model.generate
    def generate(*args, **kwargs):
        result = original(*args, **kwargs)
        sequences = result.sequences if hasattr(result, 'sequences') else result
        rows = sequences.tolist()
        stops = kwargs.get('eos_token_id')
        stops = {stops} if isinstance(stops, int) else set(stops or [])
        prompt = args[0] if args else kwargs.get('input_ids')
        start = prompt.shape[-1] if prompt is not None else 0
        if not stops or any(not stops.intersection(row[start:]) for row in rows):
            raise RuntimeError('Narrator output stopped without EOS; refusing truncated audio.')
        return result
    model.generate = generate


class CheckedLlama:
    def __init__(self, model):
        self.model = model

    def __getattr__(self, name):
        return getattr(self.model, name)

    def __call__(self, *args, **kwargs):
        finished = False
        for event in self.model(*args, **kwargs):
            reason = event['choices'][0].get('finish_reason')
            if reason == 'length':
                raise RuntimeError('Narrator output token budget exhausted; refusing truncated audio.')
            if reason is not None:
                finished = True
            yield event
        if not finished:
            raise RuntimeError('Narrator stream ended without a completion signal.')
