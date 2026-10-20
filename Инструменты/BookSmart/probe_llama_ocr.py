import argparse,base64,json,socket,subprocess,time,urllib.request,urllib.error,os,ctypes
from pathlib import Path
ROOT=Path(__file__).resolve().parents[2]
EVIDENCE=ROOT/'Тесты'/'BookSmartStand'; RUNTIME=ROOT/'Runtime'/'BookSmartStand'
PROMPT='\nExtract all readable content from the image in natural human reading order and output the result as a single Markdown document. For charts or images, represent them using an HTML image tag: <img src="images/bbox_{left}_{top}_{right}_{bottom}.jpg" />, where left, top, right, bottom are bounding box coordinates scaled to [0, 1000). Format formulas as LaTeX. Format tables as HTML: <table>...</table>. Transcribe all other text as standard Markdown. Preserve the original text without translation or paraphrasing.'
def run(args):
 pins=json.loads((EVIDENCE/'model-pins.json').read_text(encoding='utf8'))
 prefix='PaddlePaddle/' if args.model=='paddle' else 'bartowski/'
 files=[RUNTIME/p['repository'].replace('/','--')/p['file'] for p in pins if p['repository'].startswith(prefix)]
 model=next(p for p in files if 'mmproj' not in p.name); projector=next(p for p in files if 'mmproj' in p.name)
 with socket.socket() as s:s.bind(('127.0.0.1',0));port=s.getsockname()[1]
 tag=args.model+'-'+args.backend
 out=EVIDENCE/tag;out.mkdir(exist_ok=True)
 command=[args.server,'-m',str(model),'--mmproj',str(projector),'--host','127.0.0.1','--port',str(port),'-c','8192','-np','1','-t','8','-ngl',str(args.layers),'--temp','0','--no-warmup']
 if args.layers==0: command += ['--no-mmproj-offload','--device','none']
 record={'command':command,'model':args.model,'backend':args.backend,'start_utc':time.strftime('%Y-%m-%dT%H:%M:%SZ',time.gmtime())}
 start=time.perf_counter(); process=None
 try:
  with (out/'stdout.log').open('wb') as so,(out/'stderr.log').open('wb') as se:
   process=subprocess.Popen(command,stdout=so,stderr=se,creationflags=getattr(subprocess,'CREATE_NO_WINDOW',0));record['pid']=process.pid
   ready=False
   while time.perf_counter()-start<180:
    if process.poll() is not None:raise RuntimeError('Server exited '+str(process.returncode))
    try:
     with urllib.request.urlopen(f'http://127.0.0.1:{port}/health',timeout=2) as r:
      if r.status==200:ready=True;break
    except (OSError,urllib.error.URLError):time.sleep(.25)
   if not ready:raise TimeoutError('Health timeout')
   record['load_seconds']=time.perf_counter()-start
   if args.serve:
    print(json.dumps({'url':f'http://127.0.0.1:{port}/v1','pid':process.pid}),flush=True)
    time.sleep(args.serve);return
   encoded=base64.b64encode((EVIDENCE/'russian-two-columns.png').read_bytes()).decode('ascii')
   request={'messages':[{'role':'user','content':[{'type':'image_url','image_url':{'url':'data:image/png;base64,'+encoded}},{'type':'text','text':'OCR:' if args.model=='paddle' else PROMPT}]}],'temperature':0,'max_tokens':1536,'stream':False,'chat_template_kwargs':{'enable_thinking':False}}
   (out/'request.json').write_text(json.dumps({**request,'messages':'image plus official prompt; image stored separately'},ensure_ascii=False,indent=2),encoding='utf8')
   inference=time.perf_counter()
   req=urllib.request.Request(f'http://127.0.0.1:{port}/v1/chat/completions',json.dumps(request).encode(),{'Content-Type':'application/json'})
   with urllib.request.urlopen(req,timeout=360) as r:data=json.load(r)
   record['inference_seconds']=time.perf_counter()-inference
   class Memory(ctypes.Structure):
    _fields_=[('cb',ctypes.c_ulong),('PageFaultCount',ctypes.c_ulong)]+[(name,ctypes.c_size_t) for name in ['PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage','QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage','PrivateUsage']]
   memory=Memory();memory.cb=ctypes.sizeof(memory)
   fn=ctypes.windll.psapi.GetProcessMemoryInfo;fn.argtypes=[ctypes.c_void_p,ctypes.c_void_p,ctypes.c_ulong]
   if fn(int(process._handle),ctypes.byref(memory),memory.cb):record['peak_working_set_bytes']=memory.PeakWorkingSetSize;record['private_bytes_at_end']=memory.PrivateUsage
   (out/'response.json').write_text(json.dumps(data,ensure_ascii=False,indent=2),encoding='utf8')
   content=data['choices'][0]['message']['content']
   (out/'output.md').write_text(content,encoding='utf8');record['characters']=len(content);record['usage']=data.get('usage');record['finish_reason']=data['choices'][0].get('finish_reason');record['status']='success'
 except Exception as e:
  record['status']='error';record['error']=repr(e)
  if isinstance(e,urllib.error.HTTPError):record['http_error_body']=e.read().decode(errors='replace')
 finally:
  if process is not None:
   if process.poll() is None:process.terminate()
   try:process.wait(15)
   except subprocess.TimeoutExpired:process.kill();process.wait()
  record['total_seconds']=time.perf_counter()-start
  (out/'run.json').write_text(json.dumps(record,ensure_ascii=False,indent=2),encoding='utf8');print(json.dumps(record,ensure_ascii=False),flush=True)
if __name__=='__main__':
 p=argparse.ArgumentParser();p.add_argument('--model',choices=['paddle','ovis'],required=True);p.add_argument('--server',required=True);p.add_argument('--backend',required=True);p.add_argument('--layers',type=int,default=0);p.add_argument('--serve',type=int,default=0);run(p.parse_args())
