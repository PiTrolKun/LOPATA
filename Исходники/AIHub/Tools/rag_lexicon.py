"""Resumable local dictionary import, independent of the book/vector generation."""
import argparse
from concurrent.futures import ThreadPoolExecutor
from contextlib import closing
import hashlib
import json
import os
from pathlib import Path
import queue
import re
import shutil
import sqlite3
import sys
import threading
import time
import uuid

sys.path.insert(0, str(Path(__file__).resolve().parent))
from rag_book_load import CpuProbe
from runtime_hardware import system_memory
from rag_lexicon_sources import PROFILE, ADAPTERS, entries

BATCH = 1000
BATCH_BYTES = 1024**2


def digest(path):
    h = hashlib.sha256()
    with open(path, 'rb') as stream:
        while data := stream.read(1024**2):
            h.update(data)
    return h.hexdigest()


def read(path):
    with open(path, encoding='utf-8-sig') as stream:
        return json.load(stream)


def atomic(path, value):
    path = Path(path)
    path.parent.mkdir(parents=True, exist_ok=True)
    temporary = path.with_name(path.name + '.' + uuid.uuid4().hex + '.tmp')
    try:
        with temporary.open('w', encoding='utf-8', newline='\n') as stream:
            json.dump(value, stream, ensure_ascii=False)
            stream.flush()
            os.fsync(stream.fileno())
        os.replace(temporary, path)
    finally:
        temporary.unlink(missing_ok=True)


def emit(stage, done=0, total=0, detail=''):
    print(json.dumps(dict(stage=stage,done=done,total=total,detail=detail),ensure_ascii=False),flush=True)


def current(library):
    root = Path(library)/'lexicon'
    pointer = read(root/'current.json')
    version = pointer['version']
    if not isinstance(version,str) or not re.fullmatch('[a-f0-9]{32}',version):
        raise ValueError('Invalid dictionary version')
    folder = root/'versions'/version
    if digest(folder/'manifest.json') != pointer['manifest_sha256']:
        raise ValueError('Dictionary manifest checksum mismatch')
    manifest = read(folder/'manifest.json')
    if manifest['profile'] != PROFILE or manifest['version'] != version or digest(folder/'lexicon.sqlite') != manifest['database_sha256']:
        raise ValueError('Dictionary database checksum or profile mismatch')
    return folder,manifest


def connect(path):
    db = sqlite3.connect(path)
    db.execute('PRAGMA foreign_keys=ON')
    db.execute('PRAGMA synchronous=FULL')
    db.execute('PRAGMA cache_size=-32768')
    db.executescript('''
      CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY,value TEXT NOT NULL);
      CREATE TABLE IF NOT EXISTS sources(id TEXT PRIMARY KEY,adapter TEXT NOT NULL,sha256 TEXT NOT NULL,
        metadata TEXT NOT NULL,position INTEGER NOT NULL DEFAULT 0,accepted INTEGER NOT NULL DEFAULT 0,
        rejected INTEGER NOT NULL DEFAULT 0,complete INTEGER NOT NULL DEFAULT 0,reasons TEXT NOT NULL DEFAULT '{}',
        examples TEXT NOT NULL DEFAULT '[]');
      CREATE TABLE IF NOT EXISTS entries(id INTEGER PRIMARY KEY,source_id TEXT NOT NULL REFERENCES sources(id),
        ordinal INTEGER NOT NULL,word TEXT NOT NULL,language TEXT NOT NULL,kind TEXT NOT NULL,
        data TEXT NOT NULL,requires_context INTEGER NOT NULL,locator TEXT NOT NULL,UNIQUE(source_id,ordinal));
      CREATE TABLE IF NOT EXISTS lookup(key TEXT NOT NULL,entry_id INTEGER NOT NULL REFERENCES entries(id) ON DELETE CASCADE,
        PRIMARY KEY(key,entry_id)) WITHOUT ROWID;
      CREATE INDEX IF NOT EXISTS entry_source ON entries(source_id);
      CREATE INDEX IF NOT EXISTS lookup_entry ON lookup(entry_id);
      CREATE VIRTUAL TABLE IF NOT EXISTS search USING fts5(terms,entry_id UNINDEXED);
    ''')
    return db


class Budget:
    """Soft aggregate CPU budget for bounded parsing; never imports torch or a model."""
    def __init__(self,target,stop):
        self.target,self.stop = max(10,min(90,target)),stop
        self.probe = CpuProbe() if os.name=='nt' else None
        self.mean,self.sample = None,0
        self.lock = threading.Lock()
        if self.probe:
            self.probe.read()

    def before(self):
        while not self.stop.is_set():
            with self.lock:
                if time.monotonic() >= self.sample:
                    cpu = self.probe.read() if self.probe else None
                    self.mean = cpu if self.mean is None or cpu is None else self.mean*.65+cpu*.35
                    self.sample = time.monotonic()+1
                cpu = self.mean
            total,available = system_memory() if os.name=='nt' else (0,0)
            if (cpu is None or cpu<=self.target+3) and (not total or available>512*1024**2):
                return True
            self.stop.wait(.2)
        return False


def glossary_text(item):
    data=item['data']
    return ' '.join([item['word'], *data.get('definitions',[]), *data.get('translations',[]),
        *(g for sense in data.get('senses',[]) for g in sense.get('glosses',[])), *data.get('synonyms',[])])


def prepare_sources(db,sources):
    pending=[]
    for source in sources:
        old=db.execute('SELECT sha256,position,complete,metadata FROM sources WHERE id=?',(source['id'],)).fetchone()
        metadata=json.dumps(source,ensure_ascii=False,sort_keys=True)
        if old and old[0]==source['sha256'] and read_metadata_identity(old[3])==read_metadata_identity(metadata):
            if not old[2]:
                pending.append((source,old[1]))
            continue
        with db:
            db.execute('DELETE FROM search WHERE entry_id IN (SELECT id FROM entries WHERE source_id=?)',(source['id'],))
            db.execute('DELETE FROM entries WHERE source_id=?',(source['id'],))
            db.execute('DELETE FROM sources WHERE id=?',(source['id'],))
            db.execute('INSERT INTO sources(id,adapter,sha256,metadata) VALUES(?,?,?,?)',
                (source['id'],source['adapter'],source['sha256'],metadata))
        pending.append((source,0))
    return pending


def read_metadata_identity(value):
    value=json.loads(value)
    value.setdefault('adapter_revision',1)
    return {k:v for k,v in value.items() if k not in ('snapshot',)}


def import_pending(db,pending,target,progress=emit,fail_after_batches=None):
    messages=queue.Queue(maxsize=4)
    stop=threading.Event()
    budget=Budget(target,stop)
    workers=max(1,min(4,int((os.cpu_count() or 1)*target/100),len(pending)))
    def send(message):
        while not stop.is_set():
            try:
                messages.put(message,timeout=.2)
                return
            except queue.Full:
                pass
    def parse(source,position):
        try:
            batch=[];batch_bytes=0
            for value in entries(source,position):
                if stop.is_set():
                    return
                batch.append(value)
                if value[1] is not None:
                    item=value[1]
                    item['_data_json']=json.dumps(item['data'],ensure_ascii=False,separators=(',',':'))
                    batch_bytes+=len(item['_data_json'].encode('utf-8'))+sum(len(k)*4 for k in item['keys'])+1024
                else:
                    batch_bytes+=256
                if len(batch)>=BATCH or batch_bytes>=BATCH_BYTES:
                    if not budget.before():
                        return
                    send((source,batch,False,None));batch=[];batch_bytes=0
            if budget.before():
                send((source,batch,True,None))
        except Exception as error:
            send((source,[],False,error))
    count=0;complete=0
    pool=ThreadPoolExecutor(max_workers=workers,thread_name_prefix='lexicon-reader')
    try:
        for source,position in pending:
            pool.submit(parse,source,position)
        while complete<len(pending):
            source,batch,done,error=messages.get()
            if error:
                raise ValueError('Dictionary file could not be read: '+source['id']) from error
            ident=source['id']
            saved=db.execute('SELECT position,accepted,rejected,reasons,examples FROM sources WHERE id=?',(ident,)).fetchone()
            position,accepted,rejected=saved[:3]
            reasons,examples=json.loads(saved[3]),json.loads(saved[4])
            with db:
                for ordinal,item,reason in batch:
                    if ordinal<=position:
                        raise ValueError('Non-monotonic dictionary checkpoint')
                    position=ordinal
                    if item is None:
                        if reason!='comment_or_empty':
                            rejected+=1;reasons[reason]=reasons.get(reason,0)+1
                            if len(examples)<20:
                                examples.append(dict(ordinal=ordinal,reason=reason))
                        continue
                    cursor=db.execute('INSERT INTO entries(source_id,ordinal,word,language,kind,data,requires_context,locator) VALUES(?,?,?,?,?,?,?,?)',
                        (ident,ordinal,item['word'],item['language'],item['kind'],item['_data_json'],
                         int(item['requires_context']),item['locator'] or source['source_url']))
                    eid=cursor.lastrowid
                    db.executemany('INSERT INTO lookup(key,entry_id) VALUES(?,?)',((k,eid) for k in item['keys']))
                    if item['kind'] in ('meaning','forms'):
                        db.execute('INSERT INTO search(terms,entry_id) VALUES(?,?)',(glossary_text(item),eid))
                    accepted+=1
                if done and accepted==0:
                    raise ValueError('No admissible dictionary entries in '+ident)
                db.execute('UPDATE sources SET position=?,accepted=?,rejected=?,complete=?,reasons=?,examples=? WHERE id=?',
                    (position,accepted,rejected,int(done),json.dumps(reasons),json.dumps(examples),ident))
            count+=1
            if done:
                complete+=1
            progress('LexiconIndex',complete,len(pending),f'{ident}: {position}; accepted {accepted}; excluded {rejected}')
            if fail_after_batches is not None and count>=fail_after_batches:
                raise InterruptedError('Explicit interrupted-import fixture')
    finally:
        stop.set()
        pool.shutdown(wait=True,cancel_futures=True)


def build(library,work,request,target,progress=emit,fail_after_batches=None):
    work=Path(work);work.mkdir(parents=True,exist_ok=True)
    sources=[dict(source,adapter_revision=2 if source['adapter']=='openrussian' else 1) for source in request['sources']]
    if request.get('schema')!=1 or request.get('profile')!=PROFILE or not sources:
        raise ValueError('Invalid dictionary request')
    if len({s['id'] for s in sources})!=len(sources):
        raise ValueError('Duplicate dictionary source identity')
    source_root=(Path(library)/'lexicon/sources').resolve()
    for source in sources:
        if source['adapter'] not in ADAPTERS or not Path(source['snapshot']).resolve().is_relative_to(source_root):
            raise ValueError('Unsafe dictionary snapshot/profile')
        if digest(source['snapshot'])!=source['sha256']:
            raise ValueError('Dictionary source checksum mismatch')
        for license_file in source['license_files']:
            if not Path(license_file['path']).resolve().is_relative_to(source_root) or digest(license_file['path'])!=license_file['sha256']:
                raise ValueError('Dictionary license checksum mismatch')
    identity=hashlib.sha256(json.dumps([PROFILE,[read_metadata_identity(json.dumps(s)) for s in sources]],sort_keys=True).encode()).hexdigest()
    database=work/'lexicon-checkpoint.sqlite'
    if not database.exists() and (Path(library)/'lexicon/current.json').exists():
        progress('LexiconMerge')
        old,_=current(library)
        with closing(sqlite3.connect((old/'lexicon.sqlite').as_uri()+'?mode=ro',uri=True)) as src, closing(sqlite3.connect(database)) as dst:
            src.backup(dst)
        with closing(sqlite3.connect(database)) as copied:
            with copied:
                copied.execute("DELETE FROM meta WHERE key='import_identity'")
    with closing(connect(database)) as db:
        old=db.execute("SELECT value FROM meta WHERE key='import_identity'").fetchone()
        if old and old[0]!=identity:
            raise ValueError('Dictionary checkpoint input changed')
        with db:
            db.execute("INSERT OR REPLACE INTO meta VALUES('profile',?)",(PROFILE,))
            db.execute("INSERT OR REPLACE INTO meta VALUES('import_identity',?)",(identity,))
        pending=prepare_sources(db,sources)
        if pending:
            import_pending(db,pending,target,progress,fail_after_batches)
        progress('LexiconValidate')
        if db.execute('PRAGMA integrity_check').fetchone()[0]!='ok' or db.execute('PRAGMA foreign_key_check').fetchall():
            raise ValueError('Dictionary database validation failed')
        if db.execute('SELECT count(*) FROM sources WHERE complete=0').fetchone()[0]:
            raise ValueError('Incomplete dictionary source')
        if not db.execute('SELECT count(*) FROM entries').fetchone()[0]:
            raise ValueError('No admissible dictionary entries')
        counts=dict(db.execute('SELECT kind,count(*) FROM entries GROUP BY kind'))
        all_sources=[]
        for ident,metadata,accepted,rejected,reasons,examples in db.execute('SELECT id,metadata,accepted,rejected,reasons,examples FROM sources ORDER BY id'):
            all_sources.append(dict(id=ident,metadata=json.loads(metadata),accepted=accepted,rejected=rejected,
                reasons=json.loads(reasons),examples=json.loads(examples)))
        summary=dict(schema=1,profile=PROFILE,records=sum(counts.values()),kinds=counts,sources=all_sources,
            excluded=sum(s['rejected'] for s in all_sources),private=True,shipping_corpus=False,
            limitations=['Exact word/form lookup and lexical full text; not vector semantic search.',
                'Source assertions are retained without model rewriting; ambiguity needs context.'])
    version=uuid.uuid4().hex
    destination=Path(library)/'lexicon/versions'/version
    destination.mkdir(parents=True)
    shutil.copyfile(database,destination/'lexicon.sqlite')
    with (destination/'lexicon.sqlite').open('r+b') as published:
        os.fsync(published.fileno())
    summary.update(version=version,database_sha256=digest(destination/'lexicon.sqlite'),database_bytes=database.stat().st_size)
    atomic(destination/'manifest.json',summary)
    (destination/'SOURCE_NOTICE.txt').write_text('\n\n'.join(
        s['metadata']['attribution']+'\n'+s['metadata']['source_url']+'\n'+s['metadata']['license']+'\n'+s['metadata']['revision'] for s in all_sources),encoding='utf-8')
    atomic(Path(library)/'lexicon/current.json',dict(version=version,manifest_sha256=digest(destination/'manifest.json')))
    progress('LexiconReady',summary['records'],summary['records'],str(summary['excluded']))
    return summary


if __name__=='__main__':
    parser=argparse.ArgumentParser()
    for name in ['input','work','library']:
        parser.add_argument('--'+name,required=True)
    parser.add_argument('--cpu-target',type=int,default=50,choices=range(10,91))
    args=parser.parse_args()
    build(args.library,args.work,read(args.input),args.cpu_target)
