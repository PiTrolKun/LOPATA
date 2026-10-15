"""Publish an explicitly reviewed subset of a ready library; never modify its source.

Only Python's standard library is needed. This is a data packaging tool, not an
application updater. The source and destination must be different directories.
"""
import argparse
import array
from contextlib import closing
import hashlib
import json
import math
from pathlib import Path
import shutil
import sqlite3
import uuid


def sha(path):
    with Path(path).open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def read(path):
    return json.loads(Path(path).read_text(encoding='utf-8-sig'))


def write(path, data):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')


def ready(root, filename):
    pointer = read(root / 'current.json')
    version = pointer['version']
    if uuid.UUID(hex=version).hex != version:
        raise ValueError('Invalid version')
    folder = root / 'versions' / version
    if sha(folder / 'manifest.json') != pointer['manifest_sha256']:
        raise ValueError('Manifest checksum mismatch')
    manifest = read(folder / 'manifest.json')
    if sha(folder / filename) != manifest['database_sha256']:
        raise ValueError('Database checksum mismatch')
    return folder, manifest


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--source', type=Path, required=True)
    parser.add_argument('--destination', type=Path, required=True)
    parser.add_argument('--catalog', type=Path, required=True)
    args = parser.parse_args()
    source, destination = args.source.resolve(), args.destination.resolve()
    if source == destination or source in destination.parents or destination in source.parents:
        raise ValueError('Source/destination overlap')
    if destination.exists():
        raise ValueError('Destination already exists; use a new staging directory')
    catalog = args.catalog.resolve()
    curation = read(catalog / 'curation.json')
    book_folder, book = ready(source, 'library.sqlite')
    lex_folder, lex = ready(source / 'lexicon', 'lexicon.sqlite')
    if book['database_sha256'] != curation['input_book_database_sha256']:
        raise ValueError('Review does not cover this book database')
    if lex['database_sha256'] != curation['input_lexicon_database_sha256']:
        raise ValueError('Review does not cover this lexicon database')
    accepted = set(curation['accepted_chunk_ids'])
    if len(accepted) != len(curation['accepted_chunk_ids']):
        raise ValueError('Duplicate reviewed chunk')
    destination.mkdir(parents=True)
    for path in catalog.rglob('*'):
        if path.is_file() and path.name not in {'delivery.json', 'verification.json'}:
            target = destination / path.relative_to(catalog)
            target.parent.mkdir(parents=True, exist_ok=True)
            shutil.copy2(path, target)
    versions = []
    for layer, original, manifest, filename in [
            ('books', book_folder, book, 'library.sqlite'),
            ('lexicon', lex_folder, lex, 'lexicon.sqlite')]:
        root = destination if layer == 'books' else destination / 'lexicon'
        version = uuid.uuid4().hex
        folder = root / 'versions' / version
        folder.mkdir(parents=True)
        database = folder / filename
        shutil.copyfile(original / filename, database)
        with closing(sqlite3.connect(database)) as db:
            db.execute('PRAGMA foreign_keys=ON')
            if layer == 'books':
                existing = {r[0] for r in db.execute('SELECT id FROM chunks')}
                if not accepted <= existing or existing != accepted | {r['id'] for r in curation['excluded_chunks']}:
                    raise ValueError('Incomplete chunk review')
                with db:
                    db.executemany('DELETE FROM chunks WHERE id=?', [(x,) for x in existing - accepted])
                    for table in ('pages', 'exclusions'):
                        if db.execute("SELECT 1 FROM sqlite_master WHERE type='table' AND name=?", (table,)).fetchone():
                            db.execute('DELETE FROM ' + table)
                    db.execute('DELETE FROM meta')
                    db.execute('DELETE FROM sources WHERE id NOT IN (SELECT source_id FROM chunks)')
                for text, start, end, blob in db.execute('SELECT text,start,end,vector FROM chunks'):
                    values = array.array('f'); values.frombytes(blob)
                    if len(values) != 2048 or not all(math.isfinite(v) for v in values) or abs(sum(v*v for v in values)-1) > .005 or end-start != len(text):
                        raise ValueError('Invalid original text/vector')
                manifest['chunks'] = len(accepted)
                manifest['name'] = 'LOPATA Rhyme RU/EN'
                manifest['excluded_units'] = len(curation['excluded_chunks'])
                manifest['excluded_characters'] = sum(x['characters'] for x in curation['excluded_chunks'])
                manifest['exclusion_reasons'] = curation['exclusion_reasons']
            else:
                for item in manifest['sources']:
                    metadata = item['metadata']
                    metadata.pop('snapshot', None)
                    metadata['source_snapshot_bundled'] = False
                    for license_file in metadata['license_files']:
                        original_license = Path(license_file['path'])
                        if sha(original_license) != license_file['sha256']:
                            raise ValueError('License checksum mismatch')
                        relative = Path('licenses') / (license_file['sha256'] + '.txt')
                        shutil.copyfile(original_license, destination / relative)
                        license_file['path'] = relative.as_posix()
                    with db:
                        db.execute('UPDATE sources SET metadata=? WHERE id=?', (json.dumps(metadata, ensure_ascii=False), item['id']))
                with db:
                    db.execute("DELETE FROM meta WHERE key='import_identity'")
                if db.execute('SELECT count(*) FROM entries').fetchone()[0] != manifest['records']:
                    raise ValueError('Lexicon count mismatch')
            if db.execute('PRAGMA integrity_check').fetchone()[0] != 'ok' or db.execute('PRAGMA foreign_key_check').fetchall():
                raise ValueError('SQLite integrity failure')
            # Remove freed pages containing old local paths/checkpoint metadata.
            db.execute('VACUUM')
        manifest.update(version=version, private=False, shipping_corpus=True,
                        corpus_id='lopata-rhyme-ru-en', corpus_release='2026.10.15',
                        database_sha256=sha(database), source_manifest_sha256=sha(original/'manifest.json'),
                        source_database_sha256=read(original/'manifest.json')['database_sha256'],
                        distribution_notice='See LICENSE.md, sources.json and curation.json at library root.')
        if layer == 'lexicon':
            manifest['database_bytes'] = database.stat().st_size
        write(folder/'manifest.json', manifest)
        if layer == 'lexicon':
            (folder/'SOURCE_NOTICE.txt').write_text((destination/'ATTRIBUTION.md').read_text(encoding='utf-8'), encoding='utf-8')
        write(root/'current.json', dict(version=version, manifest_sha256=sha(folder/'manifest.json')))
        versions.append(dict(layer=layer, version=version, database_sha256=manifest['database_sha256']))
        print(json.dumps(dict(layer=layer, version=version), ensure_ascii=False), flush=True)
    payload = [dict(path=p.relative_to(destination).as_posix(), bytes=p.stat().st_size, sha256=sha(p))
               for p in sorted(destination.rglob('*')) if p.is_file()]
    write(destination/'corpus.json', dict(schema=1, id='lopata-rhyme-ru-en', release='2026.10.15',
          install_relative='Runtime/RAG/rhyme-ru-en', book_chunks=len(accepted),
          lexical_records=lex['records'], layers=versions, files=payload))
    print(json.dumps(dict(files=len(payload), bytes=sum(p['bytes'] for p in payload)), ensure_ascii=False))


if __name__ == '__main__':
    main()
