"""Read-only audit: separate exact format from requested field correctness."""
import ast
import json
from pathlib import Path
import sqlite3

HERE=Path(__file__).resolve().parent
for path in HERE.glob('*.py'):
    ast.parse(path.read_text(encoding='utf-8'))
result={}
for name in ('gliner','nuextract','runeweaver'):
    rows=[json.loads(p.read_text(encoding='utf-8')) for p in (HERE/'runs'/name).glob('*.json') if p.name!='settings.json' and not p.name.startswith('read-') and p.name!='server.json']
    rows=[r for r in rows if 'expected' in r]
    result[name]=dict(cases=len(rows),exact_pass=sum(r['pass'] for r in rows))
reads=[json.loads(p.read_text(encoding='utf-8')) for p in (HERE/'runs/runeweaver').glob('read-*.json')]
result['consumer']=dict(cases=len(reads),exact_pass=sum(r['pass'] for r in reads),requested_fields_pass=sum(all(r.get('output',{}).get(k)==v for k,v in r['expected'].items()) for r in reads))
with sqlite3.connect((HERE/'правки.lopata').as_uri()+'?mode=ro',uri=True) as db:
    result['database']=dict(integrity=db.execute('PRAGMA integrity_check').fetchone()[0],foreign_keys=db.execute('PRAGMA foreign_key_check').fetchall(),facts=db.execute('SELECT count(*) FROM fact').fetchone()[0],revisions=db.execute('SELECT count(*) FROM history').fetchone()[0])
print(json.dumps(result,ensure_ascii=False,indent=2))
