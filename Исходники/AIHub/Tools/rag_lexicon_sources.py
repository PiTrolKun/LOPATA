"""Bounded, explicit dictionary adapters. No generative correction or example import."""
import csv
import gzip
import json
from pathlib import Path
import re
import unicodedata
from urllib.parse import quote

PROFILE = 'lexical-exact-v1'
ADAPTERS = {'cmudict', 'wordnet', 'openrussian', 'kaikki-ru',
            'ruaccent.accents', 'ruaccent.omographs', 'ruaccent.yo_words', 'ruaccent.yo_homographs'}
MAX_RECORD = 2 * 1024 * 1024
VOWELS = 'аеёиоуыэюяАЕЁИОУЫЭЮЯ'
PHONE_VOWELS = set('AA AE AH AO AW AY EH ER EY IH IY OW OY UH UW'.split())
PHONES = set('AA AE AH AO AW AY B CH D DH EH ER EY F G HH IH IY JH K L M N NG OW OY P R S SH T TH UH UW V W Y Z ZH'.split())


def key(value):
    value = unicodedata.normalize('NFC', value).lower()
    value = re.sub(r'\+(?=[аеёиоуыэюя])', '', value)
    value = re.sub(r"(?<=[аеёиоуыэюя])'", '', value)
    return ' '.join(value.replace('\u0301', '').replace('\u0300', '').replace('ё', 'е').split())


def clean(value, maximum=8000):
    if not isinstance(value, str) or not value.strip() or len(value) > maximum:
        raise ValueError('invalid_text')
    if '\ufffd' in value or any(ord(c) < 32 and c not in '\n\r\t' for c in value):
        raise ValueError('invalid_characters')
    return value.strip()


def strings(values, maximum=512):
    if not isinstance(values, list) or len(values) > maximum:
        raise ValueError('invalid_list')
    return list(dict.fromkeys(clean(x) for x in values))


def accented(value, mode):
    value = clean(value, 200)
    if mode == 'plus':
        if re.search(r'\+(?![' + VOWELS + '])', value):
            raise ValueError('invalid_stress_marker')
        value = re.sub(
            r'\+([' + VOWELS + '])', lambda m: m[1] + '\u0301', value)
    else:
        if re.search(r"(?<![" + VOWELS + r"])'", value):
            raise ValueError('invalid_stress_marker')
        value = re.sub(r"([" + VOWELS + r"])'", lambda m: m[1] + '\u0301', value)
    if any(i == 0 or value[i-1] not in VOWELS for i,c in enumerate(value) if c == '\u0301'):
        raise ValueError('invalid_stress_position')
    return value


def record(word, language, kind, data, aliases=(), contextual=False, locator=''):
    word = clean(word, 200)
    if not any(c.isalpha() for c in word):
        raise ValueError('invalid_headword')
    aliases = list(dict.fromkeys([word, *aliases]))
    lookup = sorted({key(clean(x, 200)) for x in aliases})
    if not lookup or any(not x for x in lookup):
        raise ValueError('invalid_lookup')
    return dict(word=word, language=language, kind=kind, data=data, keys=lookup,
                requires_context=contextual, locator=locator)


class ObjectStream:
    """Iterate a JSON object's properties without materializing the dictionary."""
    def __init__(self, stream):
        self.stream, self.buffer, self.pos, self.eof = stream, '', 0, False
        self.decoder = json.JSONDecoder()

    def fill(self):
        self.buffer = self.buffer[self.pos:]
        self.pos = 0
        if len(self.buffer) > MAX_RECORD:
            raise ValueError('Dictionary entry exceeds bounded JSON record size')
        chunk = self.stream.read(65536)
        self.eof = not chunk
        self.buffer += chunk

    def whitespace(self):
        while True:
            while self.pos < len(self.buffer) and self.buffer[self.pos].isspace():
                self.pos += 1
            if self.pos < len(self.buffer) or self.eof:
                return
            self.fill()

    def token(self, wanted):
        self.whitespace()
        if self.pos >= len(self.buffer) or self.buffer[self.pos] != wanted:
            raise ValueError('Malformed JSON object delimiter')
        self.pos += 1

    def value(self):
        self.whitespace()
        while True:
            try:
                value, end = self.decoder.raw_decode(self.buffer, self.pos)
                # A numeric token may end at the buffer boundary before more digits arrive.
                if end == len(self.buffer) and not self.eof:
                    self.fill()
                    continue
                self.pos = end
                return value
            except json.JSONDecodeError:
                if self.eof:
                    raise ValueError('Malformed or truncated JSON object') from None
                self.fill()

    def pairs(self):
        self.token('{')
        self.whitespace()
        if self.pos < len(self.buffer) and self.buffer[self.pos] != '}':
            while True:
                name = self.value()
                if not isinstance(name, str):
                    raise ValueError('Dictionary key must be a string')
                self.token(':')
                yield name, self.value()
                self.whitespace()
                if self.pos < len(self.buffer) and self.buffer[self.pos] == '}':
                    break
                self.token(',')
        self.token('}')
        self.whitespace()
        if self.pos < len(self.buffer):
            raise ValueError('Trailing data after JSON dictionary')


def open_text(path):
    return gzip.open(path, 'rt', encoding='utf-8-sig', errors='strict') if str(path).endswith('.gz') else open(path, encoding='utf-8-sig', errors='strict')


def lines(stream):
    number = 0
    while True:
        line = stream.readline(MAX_RECORD+1)
        if not line:
            return
        number += 1
        if len(line) > MAX_RECORD:
            while line and not line.endswith('\n'):
                line = stream.readline(MAX_RECORD+1)
            yield number, None
        else:
            yield number, line.rstrip('\r\n')


def raw_entries(source):
    adapter, path = source['adapter'], Path(source['snapshot'])
    if adapter not in ADAPTERS:
        raise ValueError('Unsupported dictionary profile (neural stress is excluded)')
    if adapter == 'wordnet':
        # The approved profile uses synset files of at most 3.4 MB, one at a time.
        # Bound user files before creating YAML nodes; never use an unsafe loader.
        if path.stat().st_size > 16 * 1024**2:
            raise ValueError('WordNet YAML exceeds the bounded synset-file profile')
        import yaml
        with open_text(path) as stream:
            items = yaml.load(stream, Loader=getattr(yaml, 'CSafeLoader', yaml.SafeLoader))
        if not isinstance(items, dict):
            raise ValueError('Expected WordNet synset mapping')
        yield from enumerate(items.items(), 1)
    else:
        with open_text(path) as stream:
            if adapter.startswith('ruaccent.'):
                yield from enumerate(ObjectStream(stream).pairs(), 1)
            elif adapter == 'openrussian':
                header = stream.readline().rstrip('\r\n').split('\t')
                if len(set(header)) != len(header) or not {'bare', 'accented', 'translations_en'}.issubset(header):
                    raise ValueError('Unknown OpenRussian header')
                for n, line in lines(stream):
                    columns = line.split('\t') if line is not None else []
                    yield n, dict(zip(header, columns)) if len(columns) == len(header) else None
            else:
                yield from lines(stream)


def convert(source, value):
    adapter = source['adapter']
    if value is None:
        raise ValueError('invalid_row')
    if adapter.startswith('ruaccent.'):
        word, variants = value
        variants = variants if isinstance(variants, list) else [variants]
        variants = strings(variants, 64)
        yo = adapter.startswith('ruaccent.yo_')
        variants = [clean(v, 200) if yo else accented(v, 'plus') for v in variants]
        if not variants or any(key(v) != key(word) for v in variants):
            raise ValueError('stress_word_mismatch')
        contextual = adapter in ('ruaccent.omographs', 'ruaccent.yo_homographs') or len(variants) > 1
        if adapter == 'ruaccent.yo_homographs':
            variants = list(dict.fromkeys([word, *variants]))
        return record(word, 'ru', 'yo' if yo else 'stress', {'variants': variants}, contextual=contextual)
    if adapter == 'cmudict':
        if not value.strip() or value.startswith(';;;'):
            return None
        value = value.split(' #', 1)[0]
        fields = value.split()
        if len(fields) < 2:
            raise ValueError('invalid_phonemes')
        word = re.sub(r'\(\d+\)$', '', fields[0]).lower()
        phones = fields[1:]
        if any((p[:-1] not in PHONE_VOWELS if p[-1:] in '012' else p not in PHONES or p in PHONE_VOWELS) for p in phones) or not any(re.search('[012]$',p) for p in phones):
            raise ValueError('invalid_phonemes')
        return record(word, 'en', 'pronunciation', {'phonemes': phones})
    if adapter == 'openrussian':
        word = clean(value['bare'], 200)
        stressed = accented(value['accented'], 'apostrophe')
        if key(word) != key(stressed):
            raise ValueError('stress_word_mismatch')
        forms = []
        allowed = ('decl_', 'sg_', 'pl_', 'past_', 'presfut_', 'imperative_')
        for name, cell in value.items():
            if name in ('sg_only', 'pl_only'):
                continue
            if name.startswith(allowed) or name in ('comparative', 'superlative', 'short_m', 'short_f', 'short_n', 'short_pl'):
                for form in cell.split(','):
                    if form.strip():
                        forms.append({'form': accented(form, 'apostrophe'), 'tags': [name]})
        data = {'variants': [stressed], 'forms': forms}
        if value['translations_en'].strip():
            data['translations'] = [clean(value['translations_en'])]
        for name in ['gender', 'aspect', 'animate', 'indeclinable', 'sg_only', 'pl_only']:
            if value.get(name):
                data[name] = clean(value[name], 100)
        return record(word, 'ru', 'forms', data, [x['form'] for x in forms],
                      contextual=len({key(x) for x in [word, stressed]}) > 1)
    if adapter == 'wordnet':
        ident, item = value
        if not isinstance(ident, str) or not re.fullmatch(r'\d{8}-[nvars]', ident) or not isinstance(item, dict):
            raise ValueError('invalid_synset')
        members, definitions = strings(item.get('members', [])), strings(item.get('definition', []))
        if not members or not definitions:
            raise ValueError('missing_lexical_gloss')
        data = {'definitions': definitions, 'synonyms': members, 'synset': ident}
        for relation in ['hypernym', 'hyponym', 'similar', 'antonym', 'mero_part', 'holo_part', 'entails']:
            if relation in item:
                data[relation] = strings(item[relation])
        return record(members[0], 'en', 'meaning', data, members,
                      locator=source['source_url'] + '#synset=' + ident)
    item = json.loads(value)
    if not isinstance(item, dict) or item.get('lang_code') != 'ru':
        raise ValueError('outside_ru_lexical_scope')
    word = clean(item.get('word'), 200)
    senses = []
    synonyms = []
    for sense in item.get('senses', []):
        if not isinstance(sense, dict):
            raise ValueError('invalid_sense')
        glosses = strings(sense.get('glosses', []))
        if glosses:
            senses.append({'glosses': glosses, 'tags': strings(sense.get('tags', []))})
        synonyms.extend(s.get('word') for s in sense.get('synonyms', []) if isinstance(s, dict))
    if not senses:
        raise ValueError('missing_lexical_gloss')
    synonyms.extend(s.get('word') for s in item.get('synonyms', []) if isinstance(s, dict))
    forms = [{'form': clean(f.get('form'), 200), 'tags': strings(f.get('tags', []))}
             for f in item.get('forms', []) if isinstance(f, dict) and f.get('form') and f.get('form') != '-']
    ipa = strings([s['ipa'] for s in item.get('sounds', []) if isinstance(s, dict) and s.get('ipa')])
    data = {'senses': senses, 'forms': forms, 'synonyms': strings(synonyms), 'ipa': ipa}
    title = quote(word.replace(' ', '_'), safe='')
    data['history_url'] = 'https://ru.wiktionary.org/w/index.php?title=' + title + '&action=history'
    return record(word, 'ru', 'meaning', data, [f['form'] for f in forms],
                  locator='https://ru.wiktionary.org/wiki/' + title)


def entries(source, position=0):
    for ordinal, raw in raw_entries(source):
        if ordinal <= position:
            continue
        try:
            item = convert(source, raw)
            yield ordinal, item, '' if item is not None else 'comment_or_empty'
        except (ValueError, TypeError, KeyError, AttributeError, RecursionError) as error:
            yield ordinal, None, str(error)[:120] if isinstance(error, ValueError) else 'invalid_schema'
