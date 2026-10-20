import json, urllib.request, hashlib, concurrent.futures, time
from pathlib import Path
ROOT=Path(r'H:\AI_HUB'); EVIDENCE=ROOT/'Тесты'/'BookSmartStand'; RUNTIME=ROOT/'Runtime'/'BookSmartStand'
SELECTIONS={
 'PaddlePaddle/PaddleOCR-VL-1.6-GGUF':('511b09642bb324401f15f97cc23bc67e8f0a291d',['PaddleOCR-VL-1.6-GGUF.gguf','PaddleOCR-VL-1.6-GGUF-mmproj.gguf']),
 'bartowski/ATH-MaaS_OvisOCR2-GGUF':('ab22420f3d44201d3aa5a62ca49a665a46b507e9',['ATH-MaaS_OvisOCR2-Q8_0.gguf','mmproj-ATH-MaaS_OvisOCR2-f16.gguf']),
}
def get(url): return urllib.request.urlopen(url,timeout=120)
def sha(p):
 h=hashlib.sha256()
 with p.open('rb') as f:
  while b:=f.read(8*1024*1024): h.update(b)
 return h.hexdigest()
def download(item):
 p=RUNTIME/item['repository'].replace('/','--')/item['file']; p.parent.mkdir(parents=True,exist_ok=True)
 if p.exists() and p.stat().st_size==item['size'] and sha(p)==item['sha256']: return str(p)
 part=p.with_suffix(p.suffix+'.part')
 print('Downloading', item['file'],item['size'],flush=True)
 with get(item['url']) as r,part.open('wb') as f:
  while b:=r.read(4*1024*1024): f.write(b)
 if part.stat().st_size!=item['size'] or sha(part)!=item['sha256']: raise ValueError('Download hash mismatch '+str(part))
 part.replace(p);print('Verified',p.name,flush=True); return str(p)
def main():
 pins=[]
 for repo,(revision,files) in SELECTIONS.items():
  data=json.load(get('https://huggingface.co/api/models/'+repo+'/revision/'+revision+'?blobs=true'))
  (EVIDENCE/(repo.replace('/','--')+'.api.json')).write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf8')
  (EVIDENCE/(repo.replace('/','--')+'.README.md')).write_bytes(get('https://huggingface.co/'+repo+'/raw/'+data['sha']+'/README.md').read())
  for name in files:
   s=next(s for s in data['siblings'] if s['rfilename']==name)
   pins.append(dict(repository=repo,revision=data['sha'],file=name,size=s['size'],sha256=s['lfs']['sha256'],license=data['cardData']['license'],official=repo.startswith('PaddlePaddle/'),url='https://huggingface.co/'+repo+'/resolve/'+data['sha']+'/'+name))
 data=json.load(get('https://huggingface.co/api/models/ATH-MaaS/OvisOCR2/revision/1fc9221b7823a371d6e97f92d527cc847e24e107?blobs=true'))
 (EVIDENCE/'ATH-MaaS--OvisOCR2.api.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf8')
 (EVIDENCE/'ATH-MaaS--OvisOCR2.README.md').write_bytes(get('https://huggingface.co/ATH-MaaS/OvisOCR2/raw/'+data['sha']+'/README.md').read())
 (EVIDENCE/'model-pins.json').write_text(json.dumps(pins,ensure_ascii=False,indent=2),encoding='utf8')
 with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool: list(pool.map(download,pins))
if __name__=='__main__': main()
