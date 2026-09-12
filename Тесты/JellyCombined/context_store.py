"""Stand snapshots from real files and SQLite, with simulated author approval."""
import hashlib
import json
from pathlib import Path
import sqlite3

HERE=Path(__file__).resolve().parent
ROOT=HERE.parents[1]

def save(path,value):
    path.parent.mkdir(parents=True,exist_ok=True)
    path.write_text(json.dumps(value,ensure_ascii=False,indent=2),encoding='utf-8')

def digest(text): return hashlib.sha256(text.encode('utf-8')).hexdigest()

class Project:
    def __init__(self,path,fixture):
        self.path=path
        self.fixture=fixture
        path.mkdir(parents=True,exist_ok=False)
        (path/'001.txt').write_text(fixture['chapter'],encoding='utf-8')
        self.editor(fixture['initial_editor'])
        save(path/'fixture.json',fixture)
        with self.db() as db:
            db.executescript('''PRAGMA user_version=1;
            CREATE TABLE fact(id TEXT PRIMARY KEY,version INTEGER,data TEXT NOT NULL);
            CREATE TABLE revision(id INTEGER PRIMARY KEY,fact_id TEXT,old_data TEXT,new_data TEXT,approved_by TEXT);
            ''')
            for fact in fixture['facts']:
                db.execute('INSERT INTO fact VALUES (?,1,?)',(fact['id'],json.dumps(dict(fact,status='approved_by_stand_fixture'),ensure_ascii=False)))
    def db(self): return sqlite3.connect(self.path/'память.lopata')
    def editor(self,text):
        if len(text)>7500: raise ValueError('Editor limit exceeded; no truncation')
        (self.path/'002.txt').write_text(text,encoding='utf-8')
    def revise(self):
        self.editor(self.fixture['corrected_editor'])
        with self.db() as db:
            old=db.execute("SELECT data FROM fact WHERE id='key'").fetchone()[0]
            fact=json.loads(old)
            fact.update(self.fixture['key_revision'])
            new=json.dumps(fact,ensure_ascii=False)
            db.execute("UPDATE fact SET version=2,data=? WHERE id='key'",(new,))
            db.execute("INSERT INTO revision(fact_id,old_data,new_data,approved_by) VALUES ('key',?,?,'stand_fixture')",(old,new))
    def snapshot(self,rag,omit=False):
        draft=(self.path/'002.txt').read_text(encoding='utf-8')
        chapter=(self.path/'001.txt').read_text(encoding='utf-8')
        with self.db() as db:
            facts=[dict(version=v,**json.loads(d)) for v,d in db.execute('SELECT version,data FROM fact ORDER BY id')]
            changes=[dict(fact_id=k,previous=json.loads(o),current=json.loads(n),approved_by=a) for k,o,n,a in db.execute('SELECT fact_id,old_data,new_data,approved_by FROM revision')]
        return dict(anchor=self.fixture['anchor'],editorAnchor=dict(number='002',sha256=digest(draft),characters=len(draft),state='preliminary'),
            workingDraft=draft,projectHistory=[dict(number='001',state='completed',sha256=digest(chapter),text=chapter)],
            jelly=[] if omit else facts,jellyRevisions=[] if omit else changes,
            referenceRag=[] if omit else rag,
            provenance=dict(rag='Actual Giga CPU / Qdrant / LiteraryRagReader results, fixed stand queries; not model-selected',
                            authorEdits='Simulated by stand fixture, no human approval claimed',
                            removedForControl=['jelly','referenceRag'] if omit else []))
