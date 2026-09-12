"""Stand-only editing transaction. Original sources never become user-edit evidence."""
import hashlib
import json
import sqlite3
import uuid

def encode(value): return json.dumps(value,ensure_ascii=False,sort_keys=True)

class Revisions:
    def __init__(self,path):
        self.db=sqlite3.connect(path)
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.execute('PRAGMA synchronous=FULL')
        self.db.executescript('''
        CREATE TABLE IF NOT EXISTS source(id TEXT PRIMARY KEY,text TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS fact(id TEXT PRIMARY KEY,version INTEGER NOT NULL,source_id TEXT REFERENCES source(id),data TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS proposal(id TEXT PRIMARY KEY,fact_id TEXT REFERENCES fact(id),base_version INTEGER,data TEXT NOT NULL,origin TEXT NOT NULL,edit_text TEXT,status TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS history(id INTEGER PRIMARY KEY,fact_id TEXT REFERENCES fact(id),version INTEGER,old_data TEXT,new_data TEXT,source_id TEXT REFERENCES source(id),origin TEXT,approved_by TEXT,UNIQUE(fact_id,version));
        ''')
    def seed(self,key,text,data):
        source=hashlib.sha256(text.encode()).hexdigest()
        with self.db:
            self.db.execute('INSERT OR IGNORE INTO source VALUES (?,?)',(source,text))
            self.db.execute('INSERT INTO fact VALUES (?,?,?,?)',(key,1,source,encode(data)))
    def read(self,key):
        row=self.db.execute('SELECT version,source_id,data FROM fact WHERE id=?',(key,)).fetchone()
        return dict(version=row[0],source_id=row[1],data=json.loads(row[2]))
    def propose(self,key,data,origin,edit_text=None):
        if set(data)!=set(('giver','item','recipient','state')): raise ValueError('Unexpected fields')
        if not all(isinstance(v,str) and v for v in data.values()): raise ValueError('Unresolved field')
        if data['state'] not in ('completed','not_happened','planned'): raise ValueError('Unknown event state')
        current=self.read(key)
        token=uuid.uuid4().hex
        with self.db:
            self.db.execute('INSERT INTO proposal VALUES (?,?,?,?,?,?,?)',
                (token,key,current['version'],encode(data),origin,edit_text,'pending'))
        return token
    def confirm(self,token):
        with self.db:
            self.db.execute('BEGIN IMMEDIATE')
            row=self.db.execute('SELECT fact_id,base_version,data,origin,status FROM proposal WHERE id=?',(token,)).fetchone()
            if not row or row[4]!='pending': raise ValueError('Not a pending proposal')
            key,base,data,origin,_=row
            current=self.read(key)
            if current['version']!=base: raise ValueError('Stale proposal')
            self.db.execute('INSERT INTO history(fact_id,version,old_data,new_data,source_id,origin,approved_by) VALUES (?,?,?,?,?,?,?)',
                (key,base+1,encode(current['data']),data,current['source_id'],origin,'stand_fixture'))
            self.db.execute('UPDATE fact SET data=?,version=? WHERE id=?',(data,base+1,key))
            self.db.execute("UPDATE proposal SET status='confirmed' WHERE id=?",(token,))
    def packet(self,key):
        current=self.read(key)
        text=self.db.execute('SELECT text FROM source WHERE id=?',(current['source_id'],)).fetchone()[0]
        return dict(active=current,original_text=text,
            warning='Edited memory requires checking against original text; this is not a proven semantic conflict',
            revisions=self.db.execute('SELECT version,origin,approved_by FROM history WHERE fact_id=? ORDER BY version',(key,)).fetchall())
