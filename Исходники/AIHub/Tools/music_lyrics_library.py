"""Personal lyric index using the existing pinned Giga encoder and SQLite. No downloads."""
import argparse
import array
from contextlib import closing
import hashlib
import json
import math
import re
from pathlib import Path
import sqlite3
import sys

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rag_books import encoder, encode, bounded, atomic, valid_vector, emit, REVISION, DIMENSIONS

PROTOCOL = 'lopata-lyrics-sections-mean-l2-370-v2'


def short_text(tokenizer, text, maximum):
    """Bound derived retrieval text; never truncate the canonical work."""
    if len(tokenizer(text)['input_ids']) <= maximum:
        return text
    low, high = 0, len(text)
    while low < high:
        middle = (low + high + 1) // 2
        if len(tokenizer(text[:middle])['input_ids']) <= maximum:
            low = middle
        else:
            high = middle - 1
    return text[:low]


def run(args):
    from transformers import AutoTokenizer
    tokenizer = AutoTokenizer.from_pretrained(args.model, trust_remote_code=True, local_files_only=True)
    request = json.loads(Path(args.input).read_text(encoding='utf-8-sig'))
    db_path = Path(args.database)
    db_path.parent.mkdir(parents=True, exist_ok=True)
    if args.mode == 'build':
        with closing(sqlite3.connect(db_path)) as db:
            db.execute('PRAGMA synchronous=FULL')
            db.executescript('''CREATE TABLE IF NOT EXISTS meta(id INTEGER PRIMARY KEY,payload TEXT);
              CREATE TABLE IF NOT EXISTS vectors(id TEXT PRIMARY KEY,data TEXT NOT NULL,vector BLOB);''')
            meta = dict(protocol=PROTOCOL, revision=REVISION, dimensions=DIMENSIONS,
                        generation=request['generation'], fingerprint=request['fingerprint'])
            wanted = {}
            for section in request['sections']:
                for start, end in bounded(section['text'], 0, len(section['text']), tokenizer):
                    fragment = section['text'][start:end]
                    metadata = '\n'.join(str(section.get(k, '')) for k in ['language', 'genre', 'tags', 'role'])
                    if section.get('style'):
                        metadata += '\nStyle: ' + str(section['style'])
                    passage = short_text(tokenizer, metadata, 64) + '\n' + fragment
                    key = hashlib.sha256((PROTOCOL + REVISION + passage).encode()).hexdigest()
                    # Identical refrains may reuse vectors, but every hit keeps its actual work identity.
                    identity = section['entryId'] + ':' + str(section['start']) + ':' + str(start)
                    wanted[identity] = (key, dict(section, text=fragment, fragmentStart=start, _passage=passage))
            cached = {}
            for data, blob in db.execute('SELECT data,vector FROM vectors WHERE vector IS NOT NULL'):
                value = json.loads(data)
                values = array.array('f'); values.frombytes(blob)
                if value.get('_encoder') == REVISION and valid_vector(values):
                    cached[value['_hash']] = blob
            with db:
                db.execute('DELETE FROM vectors')
                db.execute('DELETE FROM meta')
                for identity, (key, data) in wanted.items():
                    data.update(_hash=key, _encoder=REVISION)
                    db.execute('INSERT INTO vectors VALUES(?,?,?)',
                               (identity, json.dumps(data, ensure_ascii=False), cached.get(key)))
            missing = db.execute('SELECT count(*) FROM vectors WHERE vector IS NULL').fetchone()[0]
            torch = model = device = None
            if missing:
                torch, model, device = encoder(args.model, args.device)
            while True:
                batch = db.execute('SELECT id,data FROM vectors WHERE vector IS NULL LIMIT 8').fetchall()
                if not batch:
                    break
                vectors = encode(torch, model, device, tokenizer, [json.loads(row[1])['_passage'] for row in batch])
                with db:
                    for (identity, _), vector in zip(batch, vectors):
                        if not valid_vector(vector):
                            raise ValueError('Invalid lyric vector')
                        db.execute('UPDATE vectors SET vector=? WHERE id=?',
                                   (array.array('f', vector).tobytes(), identity))
                left = db.execute('SELECT count(*) FROM vectors WHERE vector IS NULL').fetchone()[0]
                emit('Indexing', len(wanted) - left, len(wanted), device=str(device))
            with db:
                db.execute('INSERT INTO meta VALUES(1,?)', (json.dumps(meta),))
            atomic(Path(args.output), dict(ready=True, count=len(wanted), **meta))
        return
    with closing(sqlite3.connect(db_path.as_uri() + '?mode=ro', uri=True)) as db:
        row = db.execute('SELECT payload FROM meta WHERE id=1').fetchone()
        meta = json.loads(row[0]) if row else {}
        if (meta.get('protocol') != PROTOCOL or meta.get('revision') != REVISION
                or meta.get('fingerprint') != request['fingerprint']):
            raise ValueError('Music.Library.IndexChanged')
        query = request['query']
        value = short_text(tokenizer, 'Instruct: Given a query, retrieve relevant song lyrics passages\nQuery: ' + query, 500)
        torch, model, device = encoder(args.model, args.device)
        vector = encode(torch, model, device, tokenizer, [value])[0]
        hits = []
        query_words = set(re.findall(r'[^\W\d_]+', query.lower()))
        cyrillic = len(re.findall('[а-яё]', query.lower()))
        latin = len(re.findall('[a-z]', query.lower()))
        language = 'ru' if cyrillic > latin else 'en'
        for data, blob in db.execute('SELECT data,vector FROM vectors'):
            record = json.loads(data)
            values = array.array('f'); values.frombytes(blob)
            if not valid_vector(values):
                raise ValueError('Invalid lyric vector')
            score = sum(a * b for a, b in zip(values, vector))
            if not math.isfinite(score):
                raise ValueError('Invalid lyric score')
            # Cosine is relevance, not a rhyme/quality rating. Suppress weak semantic matches;
            # then prefer the query language and explicit section/style cues, without inventing metadata.
            if score < .34:
                continue
            cues = set(re.findall(r'[^\W\d_]+', (str(record.get('genre', '')) + ' ' + str(record.get('style', '')) + ' ' + str(record.get('tags', ''))).lower()))
            role = str(record.get('role', '')).lower()
            role_match = any(word in role for word in query_words if word in {'chorus', 'verse', 'bridge', 'припев', 'куплет', 'бридж'})
            rank = score + (.06 if record.get('language') == language else 0) + min(3, len(cues & query_words)) * .02 + (.03 if role_match else 0)
            hits.append(dict(record, score=score, _rank=rank))
        hits.sort(key=lambda r: r['_rank'], reverse=True)
        counts = {}; selected = []
        for hit in hits:
            identity = hit['entryId']; counts[identity] = counts.get(identity, 0) + 1
            if counts[identity] <= 2:
                selected.append(hit)
            if len(selected) == request.get('maximumHits', 8):
                break
        atomic(Path(args.output), dict(hits=selected, **meta))


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--mode', choices=['build', 'query'], required=True)
    for name in ['model', 'input', 'database', 'output']:
        parser.add_argument('--' + name, required=True)
    parser.add_argument('--device', default='auto')
    try:
        run(parser.parse_args())
    except Exception as error:
        print(str(error), file=sys.stderr)
        raise
