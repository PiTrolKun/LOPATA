"""Create a separate smaller-input control; never overwrite the primary experiment."""
import copy
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent))
from book_source import HERE,save

source=json.loads((HERE/'book/source.json').read_text(encoding='utf-8'))
cid=int(sys.argv[1])
original=next(c for c in source['chunks'] if c['id']==cid)
source['chunks']=[]
text=original['text']
offset=0
while offset<len(text):
    end=min(offset+900,len(text))
    if end<len(text):
        boundary=text.rfind(' ',offset+450,end)
        if boundary!=-1: end=boundary+1
    source['chunks'].append(dict(id=cid*100+len(source['chunks'])+1,chapter=original['chapter'],
        start=original['start']+offset,end=original['start']+end,text=text[offset:end]))
    offset=end
source['control_of_chunk']=cid
save(HERE/'book'/f'source-small-{cid}.json',source)
print(len(source['chunks']),'parts; exact source coverage',sum(len(c['text']) for c in source['chunks'])==len(text))
