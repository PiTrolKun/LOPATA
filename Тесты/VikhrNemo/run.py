"""One owned server, isolated test projects, persistent evidence, bounded waits."""
import ctypes
from ctypes import wintypes
import datetime as dt
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import threading
import time
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
import native_probe

ROOT = Path(__file__).resolve().parents[2]
BACKEND = ROOT/'Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64/llama-server.exe'
FIXTURE = ROOT/'Тесты/LiteraryPriorities/runs/novel-07/Project'
PROBE = ROOT/'Тесты/LiteraryPriorities/bin/zoo2/Probe.dll'
OUT = Path(sys.argv[1]).resolve()
OUT.mkdir(parents=True, exist_ok=False)


def save(name, value):
    native_probe.save(OUT/name, value)


def gpu():
    p = subprocess.run(['nvidia-smi','--query-gpu=name,memory.total,memory.used,memory.free',
                        '--format=csv,noheader,nounits'], capture_output=True,text=True,timeout=8,
                       creationflags=subprocess.CREATE_NO_WINDOW)
    return p.stdout.strip()


class Counters(ctypes.Structure):
    _fields_ = [('cb',wintypes.DWORD),('PageFaultCount',wintypes.DWORD)]+[
        (name,ctypes.c_size_t) for name in ['PeakWorkingSetSize','WorkingSetSize','QuotaPeakPagedPoolUsage',
        'QuotaPagedPoolUsage','QuotaPeakNonPagedPoolUsage','QuotaNonPagedPoolUsage','PagefileUsage','PeakPagefileUsage','PrivateUsage']]


def memory(pid):
    kernel=ctypes.WinDLL('kernel32',use_last_error=True)
    kernel.OpenProcess.restype=wintypes.HANDLE
    kernel.OpenProcess.argtypes=[wintypes.DWORD,wintypes.BOOL,wintypes.DWORD]
    kernel.CloseHandle.argtypes=[wintypes.HANDLE]
    api=ctypes.WinDLL('psapi').GetProcessMemoryInfo
    api.argtypes=[wintypes.HANDLE,ctypes.POINTER(Counters),wintypes.DWORD]
    handle=kernel.OpenProcess(0x410,False,pid)
    if not handle:return {}
    try:
        data=Counters();data.cb=ctypes.sizeof(data)
        if not api(handle,ctypes.byref(data),data.cb):return {}
        return {k:getattr(data,k) for k in ['WorkingSetSize','PeakWorkingSetSize','PrivateUsage']}
    finally:kernel.CloseHandle(handle)


def stop(process):
    if process is not None and process.poll() is None:
        subprocess.run(['taskkill','/PID',str(process.pid),'/T','/F'],capture_output=True,timeout=15,
                       creationflags=subprocess.CREATE_NO_WINDOW)
        process.wait(timeout=15)


config=json.loads((ROOT/'Инструменты/VikhrNemo/config.json').read_text())
model=ROOT/'Тесты/VikhrNemo/model'/config['filename']
with socket.socket() as sock:
    sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
endpoint=f'http://127.0.0.1:{port}/'
command=[str(BACKEND),'-m',str(model),'--host','127.0.0.1','--port',str(port),
         '-c','16384','-np','1','--fit','on','--fit-target','2048','--cache-ram','0',
         '--no-context-shift','--offline','--jinja','--slots','--reasoning','off',
         '--reasoning-budget','0','--device','CUDA0','-t','8','-tb','8']
info=dict(command=command,endpoint=endpoint,startUtc=dt.datetime.now(dt.timezone.utc).isoformat(),
          model=config,baselineGpu=gpu(),status='starting')
save('launch.json',info)
server=probe=None
done=threading.Event()
samples=[]
phase='loading'


def monitor():
    while not done.is_set():
        try:
            samples.append(dict(utc=dt.datetime.now(dt.timezone.utc).isoformat(),phase=phase,
                                gpu=gpu(),memory=memory(server.pid)))
            save('resources.json',samples)
        except Exception as error:
            samples.append(dict(error=repr(error)))
        done.wait(2)


start=time.monotonic()
try:
    with (OUT/'server.log').open('w',encoding='utf-8') as log:
        server=subprocess.Popen(command,cwd=BACKEND.parent,
            env={k:v for k,v in os.environ.items() if not k.startswith('LLAMA_ARG_')},
            stdout=log,stderr=log,creationflags=subprocess.CREATE_NO_WINDOW)
        info['pid']=server.pid;save('launch.json',info)
        threading.Thread(target=monitor,daemon=True).start()
        while True:
            if server.poll() is not None:raise RuntimeError(f'Server exit {server.returncode}')
            if time.monotonic()-start>150:raise TimeoutError('Server startup')
            try:
                with urllib.request.urlopen(endpoint+'health',timeout=2) as r:
                    if r.status==200:break
            except OSError:time.sleep(.4)
        info['readySeconds']=time.monotonic()-start
        for route in ['props','slots']:
            with urllib.request.urlopen(endpoint+route,timeout=5) as r:save(route+'.json',json.load(r))
        # Confirm that documents are really present under the original role in the model template.
        messages=[dict(role='system',content=native_probe.GROUND),
                  dict(role='documents',content='[{"doc_id":0,"content":"TEST_DOCUMENT_957"}]'),
                  dict(role='user',content='Проверка шаблона')]
        applied=native_probe.post(endpoint,'apply-template',dict(messages=messages,add_generation_prompt=True))
        save('documents-template.json',applied)
        if '<|start_header_id|>documents<|end_header_id|>' not in applied['prompt']:
            raise ValueError('Native documents role was not preserved')
        print('READY',round(info['readySeconds'],2),'seconds; documents role preserved',flush=True)
        phase='existing_literary_probe'
        with (OUT/'probe.log').open('w',encoding='utf-8') as plog:
            probe=subprocess.Popen(['dotnet',str(PROBE),str(OUT/'existing'),str(FIXTURE),
                    '--novel','--fixture','--endpoint',endpoint],cwd=ROOT,stdout=plog,stderr=plog,
                    creationflags=subprocess.CREATE_NO_WINDOW)
            probe.wait(timeout=600)
        info['probeExit']=probe.returncode;save('launch.json',info)
        print('EXISTING PROBE END',probe.returncode,flush=True)
        phase='native_and_roles'
        native_probe.run(endpoint,OUT/'native',FIXTURE)
        info['status']='complete'
except KeyboardInterrupt:
    info['status']='user_stopped'
except Exception as error:
    info['status']='error';info['error']=repr(error)
    print('ERROR',repr(error),flush=True)
finally:
    done.set()
    stop(probe);stop(server)
    info['elapsedSeconds']=time.monotonic()-start
    info['endedUtc']=dt.datetime.now(dt.timezone.utc).isoformat()
    info['afterGpu']=gpu()
    save('outcome.json',info)
    print('END',info['status'],round(info['elapsedSeconds'],1),flush=True)
