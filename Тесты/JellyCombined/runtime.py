"""Same native server settings as Vikhr stand; own-handle process cleanup."""
import contextlib
import json
import os
from pathlib import Path
import socket
import subprocess
import time
import urllib.request
from context_store import ROOT,save

@contextlib.contextmanager
def server(name,out):
    config=json.loads((ROOT/f'Инструменты/{name}/config.json').read_text(encoding='utf-8-sig'))
    model=ROOT/f'Тесты/{name}/model'/config['filename']
    backend=ROOT/'Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64/llama-server.exe'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1',0));port=sock.getsockname()[1]
    endpoint=f'http://127.0.0.1:{port}/'
    command=[str(backend),'-m',str(model),'--host','127.0.0.1','--port',str(port),'-c','16384','-np','1',
        '--fit','off','-ngl','99','--cache-ram','0','--offline','--no-context-shift','--jinja',
        '--no-prefill-assistant','--reasoning','off','--reasoning-budget','0','-t','8','-tb','8']
    start=time.monotonic();process=None;info=dict(command=command,status='starting',modelBytes=model.stat().st_size)
    with (out/f'{name}-server.log').open('w',encoding='utf-8') as log:
        try:
            process=subprocess.Popen(command,cwd=backend.parent,stdout=log,stderr=log,
                env={k:v for k,v in os.environ.items() if not k.startswith('LLAMA_ARG_')},creationflags=subprocess.CREATE_NO_WINDOW)
            info['pid']=process.pid;save(out/f'{name}-launch.json',info)
            while time.monotonic()-start<150:
                if process.poll() is not None: raise RuntimeError('Server exited')
                try:
                    with urllib.request.urlopen(endpoint+'health',timeout=2) as r:
                        if r.status==200: break
                except OSError: time.sleep(.4)
            else: raise TimeoutError('Model startup')
            info['readySeconds']=time.monotonic()-start
            print(name,'ready',info['readySeconds'],flush=True)
            yield endpoint
            info['status']='complete'
        finally:
            if process and process.poll() is None:
                process.terminate()
                process.wait(timeout=15)
            info['elapsedSeconds']=time.monotonic()-start
            info['exitCode']=process.poll() if process else None
            save(out/f'{name}-outcome.json',info)
