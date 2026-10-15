"""Read the standalone LOPATA Rhyme library using Python 3.11+ and SQLite only."""
import argparse
from contextlib import closing
import hashlib
import json
from pathlib import Path, PurePosixPath
import re
import sqlite3
import unicodedata


def read(path):
    return json.loads(path.read_text(encoding='utf-8-sig'))


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def inside(root, name):
    path = PurePosixPath(name)
    if path.is_absolute() or '..' in path.parts or '\\' in name or ':' in name:
        raise ValueError('Unsafe manifest path')
    target = (root / Path(*path.parts)).resolve()
    if root not in target.parents:
        raise ValueError('Path outside library')
    return target


def open_layer(root, lexicon=False):
    if lexicon:
        root = root/'lexicon'
    pointer = read(root/'current.json')
    if not re.fullmatch('[a-f0-9]{32}', pointer['version']):
        raise ValueError('Invalid version')
    folder = root/'versions'/pointer['version']
    if digest(folder/'manifest.json') != pointer['manifest_sha256']:
        raise ValueError('Manifest checksum mismatch')
    manifest = read(folder/'manifest.json')
    if manifest['version'] != pointer['version']:
        raise ValueError('Version mismatch')
    filename = 'lexicon.sqlite' if lexicon else 'library.sqlite'
    database = folder/filename
    if digest(database) != manifest['database_sha256']:
        raise ValueError('Database checksum mismatch')
    return database, manifest


def verify(root):
    receipt = read(root/'corpus.json')
    for item in receipt['files']:
        path = inside(root, item['path'])
        if path.stat().st_size != item['bytes'] or digest(path) != item['sha256']:
            raise ValueError('File checksum mismatch: ' + item['path'])
    for lexical in (False, True):
        path, manifest = open_layer(root, lexical)
        with closing(sqlite3.connect(path.as_uri()+'?mode=ro', uri=True)) as db:
            if db.execute('PRAGMA integrity_check').fetchone()[0] != 'ok' or db.execute('PRAGMA foreign_key_check').fetchall():
                raise ValueError('SQLite integrity failure')
            table, field = ('entries', 'records') if lexical else ('chunks', 'chunks')
            if db.execute('SELECT count(*) FROM '+table).fetchone()[0] != manifest[field]:
                raise ValueError('Row count mismatch')
    return dict(verified=True, id=receipt['id'], release=receipt['release'],
                book_chunks=receipt['book_chunks'], lexical_records=receipt['lexical_records'])


def lookup(root, word):
    path, _ = open_layer(root, True)
    key = unicodedata.normalize('NFC', word).lower()
    key = re.sub(r"\+(?=[аеёиоуыэюя])|(?<=[аеёиоуыэюя])'", '', key)
    key = re.sub(r'\s+', ' ', key.replace('\u0301', '').replace('\u0300', '').replace('ё', 'е')).strip()
    with closing(sqlite3.connect(path.as_uri()+'?mode=ro', uri=True)) as db:
        rows = db.execute('''SELECT e.word,e.kind,e.data,e.requires_context,s.id,s.metadata,e.locator
          FROM entries e JOIN sources s ON s.id=e.source_id JOIN lookup l ON l.entry_id=e.id
          WHERE l.key=? ORDER BY CASE s.adapter WHEN 'ruaccent.omographs' THEN 0
          WHEN 'ruaccent.yo_homographs' THEN 1 WHEN 'ruaccent.accents' THEN 2 WHEN 'cmudict' THEN 3
          WHEN 'openrussian' THEN 4 ELSE 5 END,e.id LIMIT 80''', (key,)).fetchall()
    hits = [dict(word=w, kind=k, data=json.loads(d), requires_context=bool(c), source=s,
                 license=json.loads(m)['license'], attribution=json.loads(m)['attribution'], locator=l)
            for w,k,d,c,s,m,l in rows]
    for kind in ('stress', 'yo'):
        if any(h['kind'] == kind and h['requires_context'] for h in hits):
            hits = [h for h in hits if h['kind'] != kind or h['requires_context']]
    stress = {unicodedata.normalize('NFC', v).lower() for h in hits if h['kind'] == 'stress' for v in h['data']['variants']}
    for hit in hits:
        if hit['kind'] == 'stress' and len(stress) > 1 or hit['kind'] == 'pronunciation' and sum(h['kind']=='pronunciation' for h in hits) > 1:
            hit['requires_context'] = True
    return hits


if __name__ == '__main__':
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--library', type=Path, required=True)
    parser.add_argument('--verify', action='store_true')
    parser.add_argument('--word')
    args = parser.parse_args()
    root = args.library.resolve()
    if not args.verify and not args.word:
        parser.error('Choose --verify or --word')
    if args.verify:
        print(json.dumps(verify(root), ensure_ascii=False, indent=2))
    if args.word:
        print(json.dumps(lookup(root, args.word), ensure_ascii=False, indent=2))
