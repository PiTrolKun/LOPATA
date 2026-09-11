"""Allocation/startup control for the project's shared 8k+16k context budget."""
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0,str(Path(__file__).resolve().parent))
from native_probe import post, save

root=Path(__file__).resolve().parents[2]
out=Path(sys.argv[1]).resolve();out.mkdir(parents=True,exist_ok=False)
model=root/'Тесты/VikhrNemo/model'/json.loads((root/'Инструменты/VikhrNemo/config.json').read_text())['filename']
backend=root/'Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64/llama-server.exe'
with socket.socket() as sock:
    sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
endpoint=f'http://127.0.0.1:{port}/'
cmd=[str(backend),'-m',str(model),'--host','127.0.0.1','--port',str(port),'-c','24576',
     '-np','2','--kv-unified','--fit','off','-ngl','99','--cache-ram','0','--offline',
     '--no-context-shift','--jinja','--reasoning','off','--reasoning-budget','0','--slots','-lv','4']
if '--protocol' in sys.argv:
    prior=json.loads((root/'Тесты/VikhrNemo/runs/first-01/launch.json').read_text())
    cmd=prior['command'][:]
    cmd[cmd.index('--port')+1]=str(port)
    cmd+=['--no-prefill-assistant','-lv','4']

def gpu():
    return subprocess.check_output(['nvidia-smi','--query-gpu=name,memory.total,memory.used,memory.free','--format=csv,noheader,nounits'],text=True,creationflags=subprocess.CREATE_NO_WINDOW).strip()
info=dict(command=cmd,baselineGpu=gpu(),status='starting',control='Allocation plus two short sequential requests; not a filled-context quality test')
server=None;start=time.monotonic()
try:
    with (out/'server.log').open('w',encoding='utf-8') as log:
        server=subprocess.Popen(cmd,cwd=backend.parent,env={k:v for k,v in os.environ.items() if not k.startswith('LLAMA_ARG_')},
            stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
        info['pid']=server.pid;save(out/'launch.json',info)
        while True:
            if server.poll() is not None:raise RuntimeError(f'Server exit {server.returncode}')
            if time.monotonic()-start>150:raise TimeoutError('Startup')
            try:
                with urllib.request.urlopen(endpoint+'health',timeout=2) as r:
                    if r.status==200:break
            except OSError:time.sleep(.4)
        info['readySeconds']=time.monotonic()-start;info['loadedGpu']=gpu()
        for slot in [0,1]:
            body=dict(messages=[dict(role='user',content='Ответь одним словом: готово.')],id_slot=slot,
                      max_tokens=16,temperature=0,cache_prompt=False,stream=False)
            save(out/f'slot-{slot}-request.json',body)
            replyStart=time.monotonic()
            response=post(endpoint,'v1/chat/completions',body)
            response['requestWallSeconds']=time.monotonic()-replyStart
            response['replyFromServerStartSeconds']=time.monotonic()-start
            if slot==0:
                info['firstReplyFromStartSeconds']=response['replyFromServerStartSeconds']
                info['firstRequestWallSeconds']=response['requestWallSeconds']
            save(out/f'slot-{slot}-response.json',response)
        with urllib.request.urlopen(endpoint+'slots',timeout=5) as r:save(out/'slots.json',json.load(r))
        info['afterRequestsGpu']=gpu()
        if '--protocol' in sys.argv:
            import protocol_probe
            protocol_probe.run(endpoint,out/'protocol',root/'Тесты/VikhrNemo/runs/first-01/native')
        info['status']='complete'
except Exception as error:info.update(status='error',error=repr(error))
finally:
    if server is not None and server.poll() is None:
        subprocess.run(['taskkill','/PID',str(server.pid),'/T','/F'],capture_output=True,timeout=15,creationflags=subprocess.CREATE_NO_WINDOW)
        server.wait(timeout=15)
    info['elapsedSeconds']=time.monotonic()-start;info['releasedGpu']=gpu();save(out/'outcome.json',info)
    print(json.dumps(info,ensure_ascii=False,indent=2),flush=True)
