"""Versioned evidence store. Model strings are data, never SQL or paths."""
import hashlib
import json
import re
import sqlite3
import uuid

DDL = '''
PRAGMA user_version=1;
CREATE TABLE meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
CREATE TABLE source(id TEXT PRIMARY KEY,corpus TEXT NOT NULL,text TEXT NOT NULL,original_path TEXT NOT NULL);
CREATE TABLE chunk(id INTEGER PRIMARY KEY,source_id TEXT REFERENCES source(id),chapter TEXT,start INTEGER,end INTEGER,text TEXT);
CREATE TABLE extraction(chunk_id INTEGER PRIMARY KEY REFERENCES chunk(id),raw TEXT NOT NULL,seconds REAL,error TEXT);
CREATE TABLE evidence(id TEXT PRIMARY KEY,chunk_id INTEGER REFERENCES chunk(id),start INTEGER,end INTEGER,quote TEXT NOT NULL);
CREATE TABLE entity(id TEXT PRIMARY KEY,chunk_id INTEGER REFERENCES chunk(id),name TEXT NOT NULL,kind TEXT NOT NULL,evidence_id TEXT REFERENCES evidence(id),identity_status TEXT NOT NULL);
CREATE TABLE record(id TEXT PRIMARY KEY,chunk_id INTEGER REFERENCES chunk(id),layer TEXT NOT NULL CHECK(layer IN ('event','assertion')),subject_id TEXT REFERENCES entity(id),predicate TEXT NOT NULL,object_text TEXT,object_id TEXT REFERENCES entity(id),claim_type TEXT NOT NULL,perspective TEXT,story_time TEXT,status TEXT NOT NULL,evidence_id TEXT REFERENCES evidence(id),producer TEXT NOT NULL);
CREATE TABLE rejected(id INTEGER PRIMARY KEY,chunk_id INTEGER REFERENCES chunk(id),payload TEXT NOT NULL,reason TEXT NOT NULL);
CREATE TABLE change_log(seq INTEGER PRIMARY KEY,record_id TEXT REFERENCES record(id),operation TEXT NOT NULL,at TEXT DEFAULT CURRENT_TIMESTAMP);
'''

class Store:
    def __init__(self, path, source, producer, contract):
        fresh = not path.exists()
        self.db = sqlite3.connect(path)
        self.db.execute('PRAGMA foreign_keys=ON')
        self.db.execute('PRAGMA journal_mode=DELETE')
        self.db.execute('PRAGMA synchronous=FULL')
        self.source, self.producer = source, producer
        if fresh:
            self.db.executescript(DDL)
            with self.db:
                self.db.executemany('INSERT INTO meta VALUES (?,?)', [('format','LOPATA Jelly stand'),('producer',producer),('contract',contract)])
                self.db.execute('INSERT INTO source VALUES (?,?,?,?)', (source['sha256'],source['corpus'],source['text'],source['source_path']))
                self.db.executemany('INSERT INTO chunk VALUES (?,?,?,?,?,?)',[(c['id'],source['sha256'],c['chapter'],c['start'],c['end'],c['text']) for c in source['chunks']])
        if self.db.execute('SELECT id FROM source').fetchone()[0] != source['sha256']:
            raise ValueError('Source changed: use a new run directory')
        for key, expected in [('producer',producer),('contract',contract)]:
            if self.db.execute('SELECT value FROM meta WHERE key=?',(key,)).fetchone()[0] != expected:
                raise ValueError('Run contract changed: use a new run directory')

    def done(self, cid):
        return self.db.execute('SELECT 1 FROM extraction WHERE chunk_id=?',(cid,)).fetchone() is not None

    def evidence(self, chunk, quote, whole_mention=False):
        if not isinstance(quote,str) or not quote.strip(): raise ValueError('missing evidence')
        candidates = [m.start() for m in re.finditer(re.escape(quote),chunk['text'])]
        if whole_mention:
            def word(c): return c.isalnum() or c=='_'
            candidates = [s for s in candidates
                if not (s>0 and word(chunk['text'][s-1]) and word(quote[0]))
                and not (s+len(quote)<len(chunk['text']) and word(chunk['text'][s+len(quote)]) and word(quote[-1]))]
        offset = candidates[0] if candidates else -1
        if offset < 0: raise ValueError('quote not verbatim in chunk')
        start = chunk['start']+offset
        key = hashlib.sha256(f"{self.source['sha256']}:{start}:{quote}".encode()).hexdigest()[:24]
        self.db.execute('INSERT OR IGNORE INTO evidence VALUES (?,?,?,?,?)',(key,chunk['id'],start,start+len(quote),quote))
        return key

    def entity(self, chunk, name, kind='unknown'):
        if not isinstance(name,str) or not name.strip(): raise ValueError('missing entity name')
        evidence = self.evidence(chunk,name,whole_mention=True)
        # Exact surface within a chunk is a mention group, NOT cross-chapter identity resolution.
        key = hashlib.sha256(f"{chunk['id']}:{name}".encode()).hexdigest()[:24]
        self.db.execute('INSERT OR IGNORE INTO entity VALUES (?,?,?,?,?,?)',
                        (key,chunk['id'],name,kind,evidence,'unresolved_mention_group'))
        return key

    def ingest(self, chunk, response):
        if self.done(chunk['id']): return
        with self.db:
            self.db.execute('INSERT INTO extraction VALUES (?,?,?,?)',
                            (chunk['id'],json.dumps(response,ensure_ascii=False),response['seconds'],response.get('error')))
            output = response.get('output',{})
            if not isinstance(output,dict): output = {}
            for layer in ('entities','events','assertions'):
                rows = output.get(layer,[])
                if not isinstance(rows,list):
                    rows = [dict(invalid_container=rows)]
                for row in rows:
                    self.db.execute('SAVEPOINT item')
                    try:
                        if not isinstance(row,dict): raise ValueError('record must be an object')
                        if layer == 'entities':
                            self.entity(chunk,row.get('name'),row.get('kind') or 'unknown')
                        else:
                            quote = row.get('evidence')
                            evidence = self.evidence(chunk,quote)
                            subject = self.entity(chunk,row.get('subject'))
                            predicate = row.get('predicate')
                            if not isinstance(predicate,str) or not predicate.strip(): raise ValueError('missing predicate')
                            obj = row.get('object') or ''
                            if not isinstance(obj,str): raise ValueError('object must be a string')
                            target = self.entity(chunk,obj) if obj and obj in chunk['text'] else None
                            claim = row.get('claim_type') or 'unknown'
                            if claim not in ('narrated','reported','belief','intention','hypothesis','unknown'):
                                raise ValueError('unknown claim_type')
                            key = uuid.uuid4().hex
                            self.db.execute('INSERT INTO record VALUES (?,?,?,?,?,?,?,?,?,?,?,?,?)',
                                (key,chunk['id'],'event' if layer=='events' else 'assertion',subject,predicate,obj,target,
                                 claim,row.get('perspective') or None,row.get('story_time') or None,
                                 'unreviewed',evidence,self.producer))
                            self.db.execute('INSERT INTO change_log(record_id,operation) VALUES (?,?)',(key,'extracted'))
                        self.db.execute('RELEASE item')
                    except (ValueError,TypeError) as error:
                        self.db.execute('ROLLBACK TO item')
                        self.db.execute('RELEASE item')
                        self.db.execute('INSERT INTO rejected(chunk_id,payload,reason) VALUES (?,?,?)',
                                        (chunk['id'],json.dumps(row,ensure_ascii=False),str(error)))

    def audit(self):
        counts = {t:self.db.execute(f'SELECT COUNT(*) FROM {t}').fetchone()[0]
                  for t in ('chunk','extraction','entity','record','evidence','rejected','change_log')}
        counts['layers'] = dict(self.db.execute('SELECT layer,count(*) FROM record GROUP BY layer'))
        counts['errors'] = self.db.execute('SELECT count(*) FROM extraction WHERE error IS NOT NULL').fetchone()[0]
        counts['integrity'] = self.db.execute('PRAGMA integrity_check').fetchone()[0]
        counts['foreign_keys'] = self.db.execute('PRAGMA foreign_key_check').fetchall()
        counts['bad_evidence'] = sum(self.source['text'][s:e]!=q for s,e,q in self.db.execute('SELECT start,end,quote FROM evidence'))
        return counts
