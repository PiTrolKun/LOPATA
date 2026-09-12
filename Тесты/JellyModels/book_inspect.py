"""Read-only memory view for inspection and later model-tool experiments."""
import argparse
import json
from pathlib import Path
import sqlite3
import sys
sys.stdout.reconfigure(encoding='utf-8')

def read_records(path, query=''):
    with sqlite3.connect(path.resolve().as_uri()+'?mode=ro',uri=True) as db:
        db.row_factory=sqlite3.Row
        rows=db.execute('''SELECT c.chapter,r.chunk_id,r.layer,n.name AS subject,r.predicate,
            r.object_text,r.claim_type,r.perspective,r.story_time,r.status,e.start,e.end,e.quote,r.producer
            FROM record r JOIN entity n ON n.id=r.subject_id JOIN evidence e ON e.id=r.evidence_id
            JOIN chunk c ON c.id=r.chunk_id ORDER BY r.chunk_id,r.rowid''').fetchall()
        # Python casefold handles Cyrillic, unlike SQLite's default ASCII LIKE collation.
        return [dict(row) for row in rows if not query or query.casefold() in
                ' '.join(str(row[k] or '') for k in ('subject','object_text','predicate','quote')).casefold()]

def export(path):
    rows=read_records(path)
    output=['# Извлечённые записи: '+path.parent.name,
            '\nВсе записи — непроверенные предложения модели. Точная цитата подтверждает место в тексте, но не правильность трактовки.\n']
    for row in rows:
        output.extend([f"## Фрагмент {row['chunk_id']} · {row['layer']} · {row['subject']}",
            f"{row['predicate']} → {row['object_text']}",
            f"Тип: {row['claim_type']}; перспектива: {row['perspective'] or 'не указана'}; время: {row['story_time'] or 'не указано'}.",
            f"Источник: глава {row['chapter']}, символы {row['start']}–{row['end']}.",
            '> '+row['quote'].replace('\n','\n> '),''])
    path.with_name('READABLE.md').write_text('\n\n'.join(output),encoding='utf-8')
    path.with_name('records.json').write_text(json.dumps(rows,ensure_ascii=False,indent=2),encoding='utf-8')
    return len(rows)

if __name__=='__main__':
    p=argparse.ArgumentParser()
    p.add_argument('database',type=Path)
    p.add_argument('--query',default='')
    p.add_argument('--export',action='store_true')
    a=p.parse_args()
    print(export(a.database) if a.export else json.dumps(read_records(a.database,a.query),ensure_ascii=False,indent=2))
