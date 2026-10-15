"""Durable publication tests without running/downloading a model."""
import array
from contextlib import closing
import json
import os
from pathlib import Path
import sys
import tempfile
import unittest

sys.path.insert(0, sys.argv.pop(1))
import rag_books as rag
import rag_book_load as load
from unittest.mock import patch


class Tokenizer:
    def __call__(self, text, **kwargs):
        return dict(input_ids=list(range(len(text))), offset_mapping=[(i, i+1) for i in range(len(text))])


class Books(unittest.TestCase):
    def test_resource_batches_are_bounded_and_oom_reduces_ceiling(self):
        self.assertEqual(1, load.batch_capacity('cuda:0', 512*1024**2, 24*load.GIB))
        self.assertEqual(16, load.batch_capacity('cuda:0', 20*load.GIB, 24*load.GIB))
        self.assertEqual(8, load.batch_capacity('cpu', 20*load.GIB, 32*load.GIB))
        class Probe:
            def read(self): return 0
        class Torch:
            def set_num_threads(self, value): self.threads = value
        torch = Torch()
        budget = load.EmbeddingLoad(torch, 'cpu', 50, lambda *a, **k: None, Probe())
        with patch.object(load, 'system_memory', return_value=(32*load.GIB, 20*load.GIB)):
            for _ in range(20): budget.success()
            self.assertEqual(8, budget.batch)
            self.assertTrue(budget.shrink())
            for _ in range(20): budget.success()
            self.assertEqual(4, budget.batch)
            budget.batch = 1
            self.assertFalse(budget.shrink())

    def test_external_cpu_pressure_waits_before_next_native_batch(self):
        class Probe:
            value = 100
            def read(self): return self.value
        class Torch:
            def set_num_threads(self, value): pass
        probe, sleeps = Probe(), []
        def sleep(seconds):
            sleeps.append(seconds); probe.value = 0
        budget = load.EmbeddingLoad(Torch(), 'cpu', 50, lambda *a, **k: None, probe, sleep)
        # Fake time advances by one interval without a busy spin or a real load generator.
        with patch.object(load.time, 'monotonic', side_effect=range(100, 140)):
            budget.next_sample = 0
            budget.before()
        self.assertTrue(sleeps)
        self.assertLessEqual(budget.average, 53)

    def test_exact_spans_do_not_truncate(self):
        text = "Ёлка и ударе́ние. " * 80
        spans = list(rag.spans(text, Tokenizer()))
        self.assertEqual(text, "".join(text[a:b] for a, b in spans))
        self.assertTrue(all(b-a <= 384 for a, b in spans))

    def test_atomic_publication_excludes_uncertain_units_and_preserves_previous(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary); source = root / "source.txt"
            source.write_text("Правило рифмы.\n\n° ° ° °", encoding="utf-8")
            page = root / "page.json"
            rag.atomic(page, dict(Source="s", Page=1, Text=source.read_text(encoding="utf-8"), Flags=[]))
            request = dict(Id="a"*32, Name="Test", revision=rag.REVISION, sources=[dict(Id="s", Name="Book", Sha256=rag.digest(source), Snapshot=str(source), Pages=[str(page)], Warnings=[])])
            with closing(rag.connect(root / "checkpoint.sqlite")) as db:
                rag.prepare(db, request, Tokenizer())
                # A whole uncertain unit is excluded, including its vectors.
                self.assertEqual(0, db.execute("SELECT count(*) FROM chunks").fetchone()[0])
                with self.assertRaises(ValueError):
                    rag.publish(db, None, root, request)
                self.assertFalse((root / "current.json").exists())
                db.execute("INSERT INTO chunks VALUES('good','s',2,0,14,'Правило рифмы.',?)", (array.array('f', [1.]+[0.]*(rag.DIMENSIONS-1)).tobytes(),))
                db.commit()
                rag.publish(db, None, root, request)
                _, manifest = rag.current(root)
                self.assertEqual(1, manifest["chunks"])
                version, _ = rag.current(root)
                with closing(__import__('sqlite3').connect(version / "library.sqlite")) as ready:
                    self.assertNotIn("exclusions", {r[0] for r in ready.execute("SELECT name FROM sqlite_master")})
                    self.assertEqual(1, ready.execute("SELECT count(*) FROM chunks").fetchone()[0])
                before = (root / "current.json").read_bytes()
                db.execute("DELETE FROM chunks"); db.commit()
                with self.assertRaises(ValueError):
                    rag.publish(db, None, root, dict(request, Id="b"*32))
                self.assertEqual(before, (root / "current.json").read_bytes())
                with (version / "library.sqlite").open('ab') as stream:
                    stream.write(b'corrupt')
                with self.assertRaises(ValueError):
                    rag.current(root)

    def test_completed_pages_and_vectors_resume_and_identity_is_checked(self):
        with tempfile.TemporaryDirectory() as temporary:
            root=Path(temporary); source=root/'source.txt'; source.write_text('Правило рифмы.', encoding='utf-8')
            page=root/'page.json'; rag.atomic(page, dict(Source='s',Page=1,Text='Правило рифмы.',Flags=[]))
            request=dict(revision=rag.REVISION,sources=[dict(Id='s',Name='Book',Sha256=rag.digest(source),Snapshot=str(source),Pages=[str(page)],Warnings=[])])
            path=root/'checkpoint.sqlite'
            with closing(rag.connect(path)) as db:
                rag.prepare(db,request,Tokenizer())
                with db: db.execute("UPDATE chunks SET vector=?", (b'checkpoint',))
            with closing(rag.connect(path)) as db:
                rag.prepare(db,request,Tokenizer())
                self.assertEqual([(b'checkpoint',)],db.execute('SELECT vector FROM chunks').fetchall())
                rag.atomic(page, dict(Source='s',Page=1,Text='Изменённые данные.',Flags=[]))
                with self.assertRaisesRegex(ValueError,'checksum changed'):
                    rag.prepare(db,request,Tokenizer())


if __name__=='__main__': unittest.main()
