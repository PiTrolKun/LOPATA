"""Check the native record API separately from the full-book memory task."""
import json
from pathlib import Path
import sys
import time
sys.path.insert(0,str(Path(__file__).resolve().parent))
sys.stdout.reconfigure(encoding='utf-8')
from book_adapters import TorchAdapter
from book_source import HERE,save

adapter=TorchAdapter('gliner')
model=adapter.model
cases=[]
for lang,text in [('en','Alice bought apples and Bob bought oranges.'),
                  ('ru','Алиса купила яблоки, а Борис купил апельсины.')]:
    schema=model.create_schema().structure('purchase',mode='natural',anchor='buyer').field(
        'buyer',dtype='str',cardinality='required_one').field('item',dtype='str',cardinality='required_one')
    with adapter.torch.inference_mode(): result=model.extract(text,schema)
    cases.append(dict(case=lang,text=text,result=result,schema=schema.build()))
source=json.loads((HERE/'book/source.json').read_text(encoding='utf-8'))
for cid in (17,22,23):
    text=source['chunks'][cid-1]['text']
    schema=model.create_schema().relations(['gives_to','married_to','located_in','loses_to'])
    with adapter.torch.inference_mode(): result=model.extract(text,schema,include_spans=True)
    cases.append(dict(case=f'book-{cid}',text=text,result=result,schema=schema.build()))
save(HERE/'book/gliner-native-check.json',cases)
for case in cases: print(case['case'],json.dumps(case['result'],ensure_ascii=False),flush=True)
