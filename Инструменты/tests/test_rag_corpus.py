"""Packaging regression: an already published catalog must not hash its old receipt into itself."""
import array
from contextlib import closing
import hashlib
import importlib.util
import json
from pathlib import Path
import sqlite3
import subprocess
import sys
import tempfile
import unittest

TOOLS = Path(__file__).resolve().parents[1]
READER = TOOLS.parent/'Каталоги/RAG/rhyme-ru-en/read_library.py'


def write(path, value):
    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_text(json.dumps(value), encoding='utf-8')


def sha(path):
    return hashlib.sha256(path.read_bytes()).hexdigest()


class CorpusPackagingTests(unittest.TestCase):
    def test_repeated_export_verifies_without_old_receipts_and_keeps_source(self):
        with tempfile.TemporaryDirectory(prefix='lopata-corpus-test-') as temp:
            root = Path(temp); source = root/'source'; catalog = root/'catalog'; destination = root/'ready'
            catalog.mkdir(); (catalog/'licenses').mkdir()
            original_license = catalog/'licenses/original.txt'; original_license.write_bytes(b'Fixture attribution\r\n')
            (catalog/'ATTRIBUTION.md').write_text('Fixture author', encoding='utf-8')
            for name in ('corpus.json', 'delivery.json', 'verification.json', 'publication.json'):
                write(catalog/name, dict(stale=True))
            book = source/'versions'/('1'*32); lexicon = source/'lexicon/versions'/('2'*32)
            book.mkdir(parents=True); lexicon.mkdir(parents=True)
            with closing(sqlite3.connect(book/'library.sqlite')) as db, db:
                db.executescript('''CREATE TABLE sources(id TEXT PRIMARY KEY);
                    CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);
                    CREATE TABLE chunks(id TEXT PRIMARY KEY,source_id TEXT REFERENCES sources(id),page INTEGER,start INTEGER,end INTEGER,text TEXT,vector BLOB);
                    INSERT INTO sources VALUES('source'); INSERT INTO meta VALUES('identity','private');''')
                db.execute('INSERT INTO chunks VALUES(?,?,?,?,?,?,?)',('chunk','source',1,0,4,'text',array.array('f',[1.0]+[0.0]*2047).tobytes()))
            metadata=dict(id='lex',license='Fixture',attribution='Fixture author',snapshot=str(original_license),
                          license_files=[dict(path=str(original_license),sha256=sha(original_license))])
            with closing(sqlite3.connect(lexicon/'lexicon.sqlite')) as db, db:
                db.executescript('''CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT);
                    CREATE TABLE sources(id TEXT PRIMARY KEY,metadata TEXT);
                    CREATE TABLE entries(id INTEGER PRIMARY KEY);
                    INSERT INTO entries VALUES(1); INSERT INTO meta VALUES('import_identity','private');''')
                db.execute('INSERT INTO sources VALUES(?,?)',('lex',json.dumps(metadata)))
            for folder, filename, manifest, version, pointer in [
                (book,'library.sqlite',dict(chunks=1),'1'*32,source/'current.json'),
                (lexicon,'lexicon.sqlite',dict(profile='lexical-exact-v1',records=1,sources=[dict(id='lex',metadata=metadata)]),'2'*32,source/'lexicon/current.json')]:
                write(folder/'manifest.json',dict(manifest,version=version,database_sha256=sha(folder/filename)))
                write(pointer,dict(version=version,manifest_sha256=sha(folder/'manifest.json')))
            original_hashes={str(p):sha(p) for p in source.rglob('*') if p.is_file()}
            write(catalog/'curation.json',dict(input_book_database_sha256=sha(book/'library.sqlite'),
                  input_lexicon_database_sha256=sha(lexicon/'lexicon.sqlite'),accepted_chunk_ids=['chunk'],
                  excluded_chunks=[],exclusion_reasons={}))
            command=[sys.executable,'-B',str(TOOLS/'prepare-rag-corpus.py'),'--source',str(source),
                     '--destination',str(destination),'--catalog',str(catalog)]
            subprocess.run(command,check=True,capture_output=True)
            spec=importlib.util.spec_from_file_location('corpus_reader',READER)
            reader=importlib.util.module_from_spec(spec);spec.loader.exec_module(reader)
            self.assertTrue(reader.verify(destination)['verified'])
            paths={entry['path'] for entry in json.loads((destination/'corpus.json').read_text())['files']}
            self.assertFalse(paths & {'corpus.json','delivery.json','verification.json','publication.json'})
            self.assertEqual(original_hashes,{str(p):sha(p) for p in source.rglob('*') if p.is_file()})
            # Refuse replacement of an existing ready library, even when inputs are valid.
            ready_hash=sha(destination/'corpus.json')
            self.assertNotEqual(0,subprocess.run(command,capture_output=True).returncode)
            self.assertEqual(ready_hash,sha(destination/'corpus.json'))


if __name__ == '__main__':
    unittest.main()
