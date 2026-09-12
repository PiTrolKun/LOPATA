"""Revalidate frozen raw responses in new containers without model inference."""
import hashlib
import json
from pathlib import Path
import shutil
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent))
from book_source import HERE,save
from jelly_store import Store

root=HERE/'book'
source=json.loads((root/'source.json').read_text(encoding='utf-8'))
original=root/'book-02'
target=root/'verified'
target.mkdir(exist_ok=False)
for model in ('gliner','nuextract','runeweaver'):
    destination=target/model
    destination.mkdir()
    base=original/model
    metadata=json.loads((base/'run.json').read_text(encoding='utf-8'))
    metadata['raw_responses_from']=str(base)
    metadata['validation']='Whole-word entity mentions; no semantic corrections'
    metadata['storage_sha256']=hashlib.sha256((HERE/'jelly_store.py').read_bytes()).hexdigest()
    save(destination/'run.json',metadata)
    store=Store(destination/'память.lopata',source,model,metadata['storage_sha256'])
    for chunk in source['chunks']:
        filename=f"chunk-{chunk['id']:03}.json"
        raw=base/filename
        response=json.loads(raw.read_text(encoding='utf-8'))
        shutil.copy2(raw,destination/filename)
        store.ingest(chunk,response)
    save(destination/'audit.json',store.audit())
    store.db.close()
    shutil.copy2(HERE/'jelly_store.py',destination/'contract_jelly_store.py')
    print(model,'revalidated')
