"""Mechanical checks independent of model quality."""
from pathlib import Path
import sys
import tempfile
import subprocess
import unittest
sys.path.insert(0,str(Path(__file__).resolve().parent))
from jelly_store import Store

class StorageTests(unittest.TestCase):
    def test_transaction_resume_and_provenance(self):
        text='Лера передала ключ Антону.'
        chunk=dict(id=1,chapter='I',start=0,end=len(text),text=text)
        source=dict(sha256='fixture',corpus='reference',text=text,source_path='fixture.txt',chunks=[chunk])
        row=dict(subject='Лера',predicate='передала',object='Антону',evidence=text,claim_type='narrated')
        response=dict(seconds=.1,output=dict(events=[row,dict(row,evidence='Выдуманная цитата')]))
        with tempfile.TemporaryDirectory(dir=Path(__file__).parent) as folder:
            path=Path(folder)/'память.lopata'
            store=Store(path,source,'test','v1')
            store.ingest(chunk,response)
            audit=store.audit()
            self.assertEqual((audit['record'],audit['rejected'],audit['bad_evidence']),(1,1,0))
            self.assertEqual(audit['foreign_keys'],[])
            self.assertEqual(audit['integrity'],'ok')
            store.db.close()
            store=Store(path,source,'test','v1')
            store.ingest(chunk,response)
            self.assertEqual(store.audit(),audit)
            with self.assertRaises(ValueError): store.entity(chunk,'Лер')
            with self.assertRaises(RuntimeError):
                with store.db:
                    store.db.execute("INSERT INTO meta VALUES ('rollback','test')")
                    raise RuntimeError('Simulated interruption')
            self.assertIsNone(store.db.execute("SELECT value FROM meta WHERE key='rollback'").fetchone())
            self.assertEqual(store.db.execute('SELECT quote FROM evidence WHERE length(quote)>10').fetchone()[0],text)
            store.db.close()
            # Leave a real hot journal by exiting without close/rollback.
            child='import sqlite3,os,sys; c=sqlite3.connect(sys.argv[1]); c.execute("PRAGMA synchronous=FULL"); c.execute("INSERT INTO meta VALUES (?,?)",("crashed","uncommitted")); os._exit(7)'
            stopped=subprocess.run([sys.executable,'-c',child,str(path)],timeout=10,capture_output=True)
            self.assertEqual(stopped.returncode,7)
            store=Store(path,source,'test','v1')
            self.assertIsNone(store.db.execute("SELECT value FROM meta WHERE key='crashed'").fetchone())
            self.assertEqual(store.audit(),audit)
            store.db.close()

if __name__=='__main__': unittest.main()
