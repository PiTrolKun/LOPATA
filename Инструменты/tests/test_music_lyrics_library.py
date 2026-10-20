"""Deterministic checkpoint/invalidation tests without model downloads or private works."""
import array
from contextlib import closing
import importlib.util
import json
from pathlib import Path
import sqlite3
import sys
import tempfile
from types import SimpleNamespace
import unittest
from unittest.mock import patch

TOOLS = Path(__file__).resolve().parents[2] / 'Исходники' / 'AIHub' / 'Tools'
sys.path.insert(0, str(TOOLS))
spec = importlib.util.spec_from_file_location('lyric_worker', TOOLS / 'music_lyrics_library.py')
worker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(worker)


class Tokenizer:
    def __call__(self, text, **kwargs):
        return {'input_ids': list(range(len(text))), 'offset_mapping': [(i, i + 1) for i in range(len(text))]}


class WorkerTests(unittest.TestCase):
    def test_checkpoint_reuses_complete_vectors_and_removes_stale_sections(self):
        with tempfile.TemporaryDirectory(prefix='lopata-lyric-worker-') as folder:
            root = Path(folder)
            args = SimpleNamespace(model='unused', mode='build', input=str(root / 'input.json'),
                                   output=str(root / 'ready.json'), database=str(root / 'vectors.sqlite'), device='cpu')
            sections = [dict(entryId=str(i), revision=1, title='Work', language='ru', genre='rock', tags='sea',
                             role='Chorus', start=0, length=30, text=f'A unique shore, number {i}.') for i in range(18)]
            request = dict(generation=1, fingerprint='first', sections=sections)
            Path(args.input).write_text(json.dumps(request), encoding='utf-8')
            processed = []

            def encode(_torch, _model, _device, _tokenizer, texts):
                if len(processed) >= 8:
                    raise RuntimeError('controlled interruption')
                processed.extend(texts)
                return [[1.] + [0.] * (worker.DIMENSIONS - 1) for _ in texts]

            fake_transformers = SimpleNamespace(AutoTokenizer=SimpleNamespace(from_pretrained=lambda *a, **kw: Tokenizer()))
            with patch.dict(sys.modules, transformers=fake_transformers), patch.object(worker, 'encoder', return_value=(None, None, 'cpu')), patch.object(worker, 'encode', side_effect=encode):
                with self.assertRaisesRegex(RuntimeError, 'controlled interruption'):
                    worker.run(args)
            self.assertFalse(Path(args.output).exists())
            with closing(sqlite3.connect(args.database)) as db:
                self.assertEqual(8, db.execute('SELECT count(*) FROM vectors WHERE vector IS NOT NULL').fetchone()[0])
                self.assertEqual(0, db.execute('SELECT count(*) FROM meta').fetchone()[0])
            resumed = []

            def finish(_torch, _model, _device, _tokenizer, texts):
                resumed.extend(texts)
                return [[1.] + [0.] * (worker.DIMENSIONS - 1) for _ in texts]

            with patch.dict(sys.modules, transformers=fake_transformers), patch.object(worker, 'encoder', return_value=(None, None, 'cpu')), patch.object(worker, 'encode', side_effect=finish):
                worker.run(args)
                self.assertEqual(10, len(resumed))
                self.assertTrue(all('rock' in passage and 'Chorus' in passage for passage in resumed))
                request.update(generation=2, fingerprint='second', sections=sections[:2])
                Path(args.input).write_text(json.dumps(request), encoding='utf-8')
                resumed.clear(); worker.run(args)
                self.assertEqual([], resumed)
                sections[0]['style'] = 'staccato'
                request.update(generation=3, fingerprint='third')
                Path(args.input).write_text(json.dumps(request), encoding='utf-8')
                resumed.clear(); worker.run(args)
                self.assertEqual(1, len(resumed))
                self.assertIn('Style: staccato', resumed[0])
            with closing(sqlite3.connect(args.database)) as db:
                self.assertEqual(2, db.execute('SELECT count(*) FROM vectors').fetchone()[0])
            args.mode = 'query'
            Path(args.input).write_text(json.dumps(dict(query='long ' * 1000, fingerprint='wrong')), encoding='utf-8')
            with patch.dict(sys.modules, transformers=fake_transformers):
                with self.assertRaisesRegex(ValueError, 'IndexChanged'):
                    worker.run(args)

    def test_long_query_is_bounded_without_touching_the_source(self):
        source = 'ёж🎵' * 2000
        result = worker.short_text(Tokenizer(), source, 500)
        self.assertEqual(500, len(result))
        self.assertEqual('ёж🎵' * 2000, source)


if __name__ == '__main__':
    unittest.main()
