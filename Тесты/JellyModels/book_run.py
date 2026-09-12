"""Resumable full-book experiment; each model uses a separate process/database."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import sys
import time
sys.path.insert(0,str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding='utf-8')
sys.dont_write_bytecode=True
from book_source import HERE, save, prepare
from jelly_store import Store
from book_adapters import TorchAdapter, RuneAdapter, TEMPLATE, INSTRUCTIONS

def run():
    parser=argparse.ArgumentParser()
    parser.add_argument('model',choices=['gliner','nuextract','runeweaver'])
    parser.add_argument('--run',default='book-01')
    parser.add_argument('--limit',type=int,default=0)
    parser.add_argument('--source',default='source.json',help='Source fixture path relative to book directory')
    args=parser.parse_args()
    if not args.run or any(c not in 'abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_' for c in args.run):
        raise ValueError('Invalid run directory')
    root=HERE/'book'
    root.mkdir(exist_ok=True)
    source_path=(root/args.source).resolve()
    if not source_path.is_relative_to(root.resolve()): raise ValueError('Source fixture outside stand')
    if args.source!='source.json' and not source_path.exists(): raise ValueError('Missing source fixture')
    source=json.loads(source_path.read_text(encoding='utf-8')) if source_path.exists() else prepare(root)
    if hashlib.sha256(source['text'].encode()).hexdigest()!=source['sha256']:
        raise ValueError('Stored source checksum mismatch')
    if any(source['text'][c['start']:c['end']]!=c['text'] for c in source['chunks']):
        raise ValueError('Stored chunk offsets mismatch')
    destination=root/args.run/args.model
    destination.mkdir(parents=True,exist_ok=True)
    # Prevent simultaneous writers to the same run; OS releases lock after a crash.
    import msvcrt
    with (destination/'run.lock').open('a+b') as lock:
        lock.seek(0)
        if not lock.read(1): lock.write(b'0');lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(),msvcrt.LK_NBLCK,1)
        contract=hashlib.sha256((json.dumps(TEMPLATE)+INSTRUCTIONS+(HERE/'book_adapters.py').read_text(encoding='utf-8')+
                                (HERE/'jelly_store.py').read_text(encoding='utf-8')).encode()).hexdigest()
        store=Store(destination/'память.lopata',source,args.model,contract)
        for name in ('book_adapters.py','jelly_store.py','book_source.py','book_run.py'):
            snapshot=destination/('contract_'+name)
            if not snapshot.exists(): snapshot.write_bytes((HERE/name).read_bytes())
        chunks=source['chunks'][:args.limit or None]
        pending=[c for c in chunks if not store.done(c['id'])]
        if not pending:
            save(destination/'audit.json',store.audit())
            print('Already complete',store.audit(),flush=True)
            return
        adapter=None
        started=time.perf_counter()
        try:
            print('Loading',args.model, 'pending',len(pending),'PID',os.getpid(),flush=True)
            adapter=RuneAdapter(destination) if args.model=='runeweaver' else TorchAdapter(args.model)
            loaded=time.perf_counter()-started
            save(destination/'run.json',dict(model=args.model,pid=os.getpid(),load_seconds=loaded,
                contract=contract,source_sha256=source['sha256'],template=TEMPLATE,instructions=INSTRUCTIONS))
            print('Loaded',args.model,round(loaded,2),'s',flush=True)
            for chunk in pending:
                response_path=destination/f"chunk-{chunk['id']:03}.json"
                if response_path.exists():
                    response=json.loads(response_path.read_text(encoding='utf-8'))
                else:
                    tick=time.perf_counter()
                    try: response=adapter.extract(chunk['text'])
                    except Exception as error: response=dict(error=f'{type(error).__name__}: {error}')
                    response.update(chunk_id=chunk['id'],seconds=time.perf_counter()-tick)
                    save(response_path,response)
                store.ingest(chunk,response)
                audit=store.audit()
                save(destination/'audit.json',audit)
                print(args.model,chunk['id'],'/',len(source['chunks']),round(response['seconds'],2),'s',
                      'records',audit['record'],'rejected',audit['rejected'],response.get('error','OK'),flush=True)
            save(destination/'audit.json',store.audit())
        finally:
            store.db.close()
            if adapter: adapter.close()
            print('Stopped',args.model,round(time.perf_counter()-started,2),'s',flush=True)

if __name__=='__main__': run()
