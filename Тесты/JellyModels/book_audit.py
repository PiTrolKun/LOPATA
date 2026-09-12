"""Produce mechanical metrics without declaring semantic facts correct."""
import json
from pathlib import Path
import sqlite3
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding='utf-8')
from book_source import HERE,save
from book_inspect import export,read_records

root=HERE/'book'/(sys.argv[1] if len(sys.argv)>1 else 'book-02')
results=[]
for path in sorted(root.glob('*/память.lopata')):
    with sqlite3.connect(path.resolve().as_uri()+'?mode=ro',uri=True) as db:
        text=db.execute('SELECT text FROM source').fetchone()[0]
        stats=dict(model=path.parent.name,bytes=path.stat().st_size,
            chunks=db.execute('SELECT count(*) FROM chunk').fetchone()[0],
            processed=db.execute('SELECT count(*) FROM extraction').fetchone()[0],
            errors=db.execute('SELECT count(*) FROM extraction WHERE error IS NOT NULL').fetchone()[0],
            entities=db.execute('SELECT count(*) FROM entity').fetchone()[0],
            records=db.execute('SELECT count(*) FROM record').fetchone()[0],
            layers=dict(db.execute('SELECT layer,count(*) FROM record GROUP BY layer')),
            claim_types=dict(db.execute('SELECT claim_type,count(*) FROM record GROUP BY claim_type')),
            rejected=dict(db.execute('SELECT reason,count(*) FROM rejected GROUP BY reason')),
            processing_seconds=db.execute('SELECT sum(seconds) FROM extraction').fetchone()[0],
            predicate_equals_subject=db.execute('SELECT count(*) FROM record r JOIN entity e ON e.id=r.subject_id WHERE r.predicate=e.name').fetchone()[0],
            empty_targets=db.execute("SELECT count(*) FROM record WHERE object_text=''").fetchone()[0],
            bad_evidence=sum(text[s:e]!=q for s,e,q in db.execute('SELECT start,end,quote FROM evidence')),
            integrity=db.execute('PRAGMA integrity_check').fetchone()[0],
            foreign_keys=db.execute('PRAGMA foreign_key_check').fetchall())
        metadata=path.with_name('run.json')
        stats['load_seconds']=json.loads(metadata.read_text(encoding='utf-8'))['load_seconds'] if metadata.exists() else None
        export(path)
        results.append(stats)
save(root/'comparison.json',results)
print(json.dumps(results,ensure_ascii=False,indent=2))
