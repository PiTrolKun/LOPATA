"""Disclosed eSpeak Russian frontend. EN/ZH delegate to the unchanged author tokenizer."""
import importlib.util
import json
from pathlib import Path
import re


def load_author_tokenizer(factory):
    # Author G2P eagerly opens six eSpeak voices, including Japanese/MBROLA,
    # although the published frontend accepts only EN/ZH. The bundled Windows
    # eSpeak cannot open that unused voice. Defer initialization until actual
    # phonemize calls; used voices still execute the original EspeakBackend.
    import phonemizer.backend as backend
    original = backend.EspeakBackend
    class DeferredBackend:
        def __init__(self, *args, **kwargs):
            self.args, self.kwargs, self.value = args, kwargs, None
        def __getattr__(self, name):
            if self.value is None: self.value = original(*self.args, **self.kwargs)
            return getattr(self.value, name)
    backend.EspeakBackend = DeferredBackend
    try:
        return factory()
    finally:
        backend.EspeakBackend = original


class ExperimentalFrontend:
    def __init__(self, source, author_factory, russian=True):
        self.source = Path(source)
        self.author_factory = author_factory
        self.author = None
        self.russian = russian
        self.ru = None
        self.vocab = json.loads((self.source / 'g2p/g2p/vocab.json').read_text(encoding='utf-8'))['vocab']
        self.rows = []

    def _parts(self, phone):
        if phone in self.vocab:
            return [phone]
        # Explicit approximate mappings into the trained IPA inventory: Russian
        # lateral -> l, central rounded ё vowel -> o. Quality is experimental.
        # A trailing eSpeak quote annotation is removed, not a lyric character.
        # Every replacement is included in the receipt.
        normalized = phone.replace('ɭ', 'l').replace('ɵ', 'o')
        if normalized.endswith('"'):
            normalized = normalized[:-1]
        # Compound phones and palatalisation may already exist as separate
        # trained IPA tokens. Never discard unsupported phones.
        keys = sorted((p for p in self.vocab if p and p != '_'), key=len, reverse=True)
        def split(offset):
            if offset == len(normalized): return []
            for key in keys:
                if normalized.startswith(key, offset):
                    rest = split(offset + len(key))
                    if rest is not None: return [key] + rest
            return None
        result = split(0)
        if not result: raise ValueError(f'Russian phoneme is absent from the trained DiffRhythm vocabulary: {phone!r}. No phoneme was discarded.')
        return result

    def encode(self, text):
        if not re.search('[А-Яа-яЁё]', text):
            if self.author is None: self.author = load_author_tokenizer(self.author_factory)
            tokens = self.author.encode(text)
            if text.strip() and not tokens: raise ValueError('Author tokenizer produced no lyric tokens.')
            self.rows.append(dict(text=text, language='author-en-zh', tokens=len(tokens)))
            return tokens
        if not self.russian:
            raise ValueError('The author DiffRhythm 2 frontend does not support Russian. Enable experimental_ru to test the eSpeak adapter.')
        if self.ru is None:
            path = self.source / 'g2p/g2p/text_tokenizers.py'
            spec = importlib.util.spec_from_file_location('_lopata_diff_text', path)
            module = importlib.util.module_from_spec(spec); spec.loader.exec_module(module)
            self.ru = module.TextTokenizer(language='ru')
        phones = self.ru(text).split('|')
        parts = [self._parts(phone) for phone in phones if phone]
        tokens = [self.vocab[p] + 1 for group in parts for p in group]
        if not tokens: raise ValueError('Russian tokenizer produced no lyric tokens.')
        changes = {phone: group for phone, group in zip([p for p in phones if p], parts) if group != [phone]}
        self.rows.append(dict(text=text, language='experimental-ru-espeak-v1', tokens=len(tokens), phones=phones, substitutions=changes))
        return tokens
