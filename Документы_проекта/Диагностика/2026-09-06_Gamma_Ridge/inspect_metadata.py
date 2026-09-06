import urllib.request,json,struct,io
from pathlib import Path
out=Path(__file__).resolve().parent;out.mkdir(parents=True,exist_ok=True)
repo='empero-ai/Qwen3.8-27B-Ridge-GGUF'
def read(url,limit=2000000,partial=False):
    req=urllib.request.Request(url,headers={'Range':f'bytes=0-{limit-1}'} if partial else {})
    with urllib.request.urlopen(req,timeout=35) as response:return response.read(limit)
api=json.loads(read('https://huggingface.co/api/models/'+repo+'?blobs=true'))
files=[f for f in api['siblings'] if f['rfilename'].endswith('.gguf')]
result={'checked':'2026-09-06','repository':repo,'revision':api['sha'],'files':files,'headers':[]}
for f in files:
    url='https://huggingface.co/'+repo+'/resolve/'+api['sha']+'/'+f['rfilename']
    b=io.BytesIO(read(url,65536,True))
    def v(fmt):return struct.unpack('<'+fmt,b.read(struct.calcsize('<'+fmt)))[0]
    def string():return b.read(v('Q')).decode()
    assert b.read(4)==b'GGUF'
    version,tensors,count=v('I'),v('Q'),v('Q');meta={}
    for _ in range(count):
        key,typ=string(),v('I')
        if typ==9:break
        meta[key]=string() if typ==8 else v({0:'B',1:'b',2:'H',3:'h',4:'I',5:'i',6:'f',7:'?',10:'Q',11:'q',12:'d'}[typ])
    result['headers'].append(dict(url=url,version=version,tensors=tensors,metadata=meta))
(out/'metadata.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
(out/'model-card.md').write_bytes(read('https://huggingface.co/'+repo+'/raw/'+api['sha']+'/README.md'))
print(json.dumps(result,ensure_ascii=False,indent=2))
