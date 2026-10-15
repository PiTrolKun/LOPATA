"""Deterministic import/recovery contracts; no model and no network."""
import gzip
import io
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
import unittest
from contextlib import closing

sys.path.insert(0, sys.argv.pop(1))
from rag_lexicon import build, current, digest, read
from rag_lexicon_sources import ObjectStream, convert, key, PROFILE


class LexiconTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.root = Path(self.temp.name)
        self.sources = self.root/'lexicon/sources/fixture'
        self.sources.mkdir(parents=True)

    def tearDown(self):
        self.temp.cleanup()

    def source(self, adapter, body, ident='fixture'):
        path = self.sources/(ident+'.txt')
        path.write_text(body, encoding='utf-8')
        license_file = self.sources/'LICENSE.txt'
        license_file.write_text('Fixture rights notice', encoding='utf-8')
        return dict(id=ident,adapter=adapter,snapshot=str(path),sha256=digest(path),
                    source_url='https://example.org/fixture',revision='test-1',license='Test-only',attribution='Fixture author',
                    license_files=[dict(path=str(license_file),sha256=digest(license_file))])

    def request(self, *sources):
        return dict(schema=1,profile=PROFILE,sources=list(sources))

    def run_build(self, request, work='work', **kwargs):
        return build(self.root,self.root/work,request,90,progress=lambda *a:None,**kwargs)

    def test_json_stream_boundaries_and_truncation(self):
        class Chunks(io.StringIO):
            def read(self,n=-1): return super().read(min(3,n))
        value={'замок':['з+амок','зам+ок'],'number':123456789}
        self.assertEqual(dict(ObjectStream(Chunks(json.dumps(value,ensure_ascii=False))).pairs()),value)
        for bad in ['{"x": "truncated', '{"a":1}garbage', '{"a":1,}']:
            with self.assertRaises(ValueError): list(ObjectStream(io.StringIO(bad)).pairs())

    def test_ambiguous_stress_and_yo_not_flattened(self):
        item=convert({'adapter':'ruaccent.omographs'},('замок',['з+амок','зам+ок']))
        self.assertEqual(item['data']['variants'],['за́мок','замо́к'])
        self.assertTrue(item['requires_context'])
        item=convert({'adapter':'ruaccent.yo_homographs'},('все','всё'))
        self.assertEqual(item['data']['variants'],['все','всё'])
        self.assertTrue(item['requires_context'])
        self.assertEqual(key('соловьи́'), 'соловьи')
        self.assertEqual(key("don't"), "don't")

    def test_phonemes_and_neural_profile_rejected(self):
        item=convert({'adapter':'cmudict'},'read(2) R EH1 D')
        self.assertEqual(item['word'],'read')
        with self.assertRaises(ValueError):convert({'adapter':'cmudict'},'bad B1 AE1 D')
        source=self.source('ruaccent.accents_nn','{}')
        with self.assertRaises(ValueError):self.run_build(self.request(source))

    def test_openrussian_form_is_searchable_without_invention(self):
        item=convert({'adapter':'openrussian'},dict(bare='человек',accented="челове'к",translations_en='person',pl_nom="лю'ди",sg_only='0',pl_only='0'))
        self.assertIn('люди',item['keys'])
        self.assertEqual(item['data']['forms'][0]['form'],'лю́ди')
        self.assertEqual(len(item['data']['forms']),1)
        self.assertNotIn('0',item['keys'])

    def test_only_lexical_wiktionary_fields_are_imported(self):
        item=dict(word='кот',lang_code='ru',senses=[dict(glosses=['домашнее животное'],examples=[{'text':'DO NOT IMPORT'}])],
                  etymology_text='DO NOT IMPORT',sounds=[dict(ipa='kot',audio='DO NOT IMPORT')])
        converted=convert({'adapter':'kaikki-ru'},json.dumps(item))
        self.assertNotIn('DO NOT IMPORT',json.dumps(converted))
        self.assertIn('action=history',converted['data']['history_url'])

    def test_restart_is_idempotent_and_does_not_change_book_layer(self):
        books=self.root/'current.json';books.write_bytes(b'book-pointer-must-remain')
        source=self.source('cmudict','\n'.join('word'+str(i)+' W ER1 D' for i in range(2505)))
        request=self.request(source)
        with self.assertRaises(InterruptedError):self.run_build(request,fail_after_batches=1)
        self.assertFalse((self.root/'lexicon/current.json').exists())
        with closing(sqlite3.connect(self.root/'work/lexicon-checkpoint.sqlite')) as db:
            committed=db.execute('select count(*) from entries').fetchone()[0]
            self.assertGreater(committed,0);self.assertLess(committed,2505)
        manifest=self.run_build(request)
        self.assertEqual(manifest['records'],2505)
        self.assertEqual(books.read_bytes(),b'book-pointer-must-remain')
        folder,_=current(self.root)
        with closing(sqlite3.connect(folder/'lexicon.sqlite')) as db:
            self.assertEqual(db.execute('select count(distinct ordinal) from entries').fetchone()[0],2505)

    def test_failure_preserves_previous_ready_generation_and_other_sources(self):
        first=self.source('cmudict','cat K AE1 T')
        self.run_build(self.request(first))
        pointer=(self.root/'lexicon/current.json').read_bytes()
        bad=self.source('ruaccent.accents','{"слово":',ident='bad')
        with self.assertRaises(ValueError):self.run_build(self.request(bad),work='broken')
        self.assertEqual((self.root/'lexicon/current.json').read_bytes(),pointer)
        second=self.source('ruaccent.omographs','{"замок":["з+амок","зам+ок"]}',ident='stress')
        manifest=self.run_build(self.request(second),work='second')
        self.assertEqual(manifest['records'],2)
        self.assertEqual(len(manifest['sources']),2)

    def test_checksum_mismatch_is_rejected_before_publish(self):
        source=self.source('cmudict','cat K AE1 T');source['sha256']='0'*64
        with self.assertRaisesRegex(ValueError,'checksum'):self.run_build(self.request(source))
        self.assertFalse((self.root/'lexicon/current.json').exists())

    def test_wordnet_synsets_exclude_examples(self):
        source=self.source('wordnet','00001740-n:\n  members: [entity]\n  definition: [Something that exists]\n  example: [DO NOT IMPORT]\n')
        manifest=self.run_build(self.request(source))
        folder,_=current(self.root)
        with closing(sqlite3.connect(folder/'lexicon.sqlite')) as db:
            self.assertEqual(manifest['records'],1)
            self.assertNotIn('DO NOT IMPORT',db.execute('select data from entries').fetchone()[0])
            self.assertEqual(db.execute("select count(*) from search where search match 'exists'").fetchone()[0],1)

    def test_doubtful_rows_are_reported_and_excluded(self):
        source=self.source('cmudict','cat K AE1 T\nbroken X9\n;;; comment\n')
        manifest=self.run_build(self.request(source))
        self.assertEqual(manifest['records'],1)
        self.assertEqual(manifest['excluded'],1)
        self.assertEqual(manifest['sources'][0]['reasons'],{'invalid_phonemes':1})

    def test_large_glosses_are_checkpointed_before_row_limit(self):
        body='\n'.join(json.dumps(dict(word='слово'+str(i),lang_code='ru',senses=[dict(glosses=['а'*5000])]),ensure_ascii=False) for i in range(300))
        source=self.source('kaikki-ru',body)
        with self.assertRaises(InterruptedError):self.run_build(self.request(source),fail_after_batches=1)
        with closing(sqlite3.connect(self.root/'work/lexicon-checkpoint.sqlite')) as db:
            count=db.execute('select count(*) from entries').fetchone()[0]
            self.assertGreater(count,0); self.assertLess(count,300)
        self.assertEqual(self.run_build(self.request(source))['records'],300)

    def test_source_replacement_removes_old_forms_and_preserves_other_sources(self):
        first=self.source('cmudict','cat K AE1 T')
        second=self.source('ruaccent.accents','{"кот":"к+от"}',ident='stress')
        self.run_build(self.request(first,second))
        replacement=self.source('cmudict','dog D AO1 G')
        result=self.run_build(self.request(replacement),work='replacement')
        self.assertEqual(result['records'],2)
        folder,_=current(self.root)
        with closing(sqlite3.connect(folder/'lexicon.sqlite')) as db:
            self.assertEqual(db.execute("select count(*) from lookup where key='cat'").fetchone()[0],0)
            self.assertEqual(db.execute("select count(*) from lookup where key='dog'").fetchone()[0],1)
            self.assertEqual(db.execute("select count(*) from lookup where key='кот'").fetchone()[0],1)


if __name__=='__main__':unittest.main()
