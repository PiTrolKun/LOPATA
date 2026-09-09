"""Bounded standalone llama-server test harness; never touches app settings."""
import json
import os
from pathlib import Path
import socket
import subprocess
import threading
import time
import urllib.request
from probe_resources import Resources

ROOT = Path(__file__).resolve().parents[2]
CONFIG = json.loads(Path(__file__).with_name('config.json').read_text(encoding='utf-8'))


def save(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')


class Server:
    def __init__(self, folder, context=8192, slots=1, extra_args=()):
        self.folder = folder
        folder.mkdir(parents=True, exist_ok=True)
        self.phase = 'loading'
        self.samples = []
        self.start = time.monotonic()
        with socket.socket() as sock:
            sock.bind(('127.0.0.1', 0))
            port = sock.getsockname()[1]
        self.url = f'http://127.0.0.1:{port}'
        executable = ROOT / Path(CONFIG['backend']).with_name('llama-server.exe')
        args = [str(executable), '-m', str(ROOT / 'Тесты/Runeweaver/model' / CONFIG['filename']),
                '--host', '127.0.0.1', '--port', str(port), '-c', str(context), '-np', str(slots),
                '-ngl', '99', '--device', 'CUDA0', '--fit', 'off', '--cache-ram', '0',
                '--no-context-shift', '--jinja', '--offline', '--slots', '--metrics', '-cb']
        args.extend(extra_args)
        self.log = (folder / 'server.log').open('wb')
        env = {k: v for k, v in os.environ.items() if not k.startswith('LLAMA_ARG_')}
        self.process = subprocess.Popen(args, stdout=self.log, stderr=subprocess.STDOUT,
            cwd=executable.parent, env=env, creationflags=subprocess.CREATE_NO_WINDOW)
        save(folder / 'launch.json', {'args': args, 'pid': self.process.pid})
        self.stop_event = threading.Event()
        self.monitor = threading.Thread(target=self._monitor, daemon=True)
        self.monitor.start()
        self.watchdog = threading.Timer(600, self.process.kill)
        self.watchdog.start()
        try:
            while time.monotonic() - self.start < 60:
                if self.process.poll() is not None:
                    raise RuntimeError('Server exited during load')
                try:
                    self.api('/health', timeout=1)
                    break
                except Exception:
                    time.sleep(.2)
            else:
                raise TimeoutError('Server startup exceeded 60 seconds')
            self.ready_seconds = time.monotonic() - self.start
            save(folder / 'props.json', self.api('/props'))
            self.idle('ready_idle')
            print(f'READY {folder.name} pid={self.process.pid} seconds={self.ready_seconds:.2f}', flush=True)
        except Exception:
            self.close()
            raise

    def _monitor(self):
        resource = Resources(self.process.pid)
        try:
            while not self.stop_event.is_set():
                value = resource.sample()
                value.update(seconds=time.monotonic() - self.start, phase=self.phase)
                self.samples.append(value)
                self.stop_event.wait(.5)
        finally:
            resource.close()

    def api(self, path, body=None, timeout=90):
        request = urllib.request.Request(self.url + path,
            None if body is None else json.dumps(body, ensure_ascii=False).encode('utf-8'),
            {'Content-Type': 'application/json'})
        with urllib.request.urlopen(request, timeout=timeout) as response:
            return json.load(response)

    def idle(self, name):
        self.phase = name
        time.sleep(2)
        save(self.folder / (name + '-slots.json'), self.api('/slots'))

    def chat(self, name, messages, slot=0, limit=1536, temperature=.8, cancel_after=None):
        self.phase = name
        body = dict(messages=messages, id_slot=slot, max_tokens=limit, temperature=temperature,
                    repeat_penalty=1.05, seed=431, cache_prompt=True, stream=True,
                    stream_options={'include_usage': True})
        save(self.folder / (name + '-request.json'), body)
        started = time.monotonic()
        request = urllib.request.Request(self.url + '/v1/chat/completions',
            json.dumps(body, ensure_ascii=False).encode('utf-8'), {'Content-Type': 'application/json'})
        summary = {'name': name, 'slot': slot, 'pid': self.process.pid}
        content = []
        raw = []
        try:
            with urllib.request.urlopen(request, timeout=90) as response:
                for line in response:
                    elapsed = time.monotonic() - started
                    if elapsed > 100:
                        raise TimeoutError('Request exceeded 100 seconds')
                    text = line.decode('utf-8').strip()
                    if not text:
                        continue
                    raw.append({'seconds': elapsed, 'line': text})
                    if text == 'data: [DONE]':
                        summary['done'] = True
                        break
                    if not text.startswith('data: '):
                        continue
                    event = json.loads(text[6:])
                    if event.get('usage'):
                        summary['usage'] = event['usage']
                    if event.get('timings'):
                        summary['timings'] = event['timings']
                    for choice in event.get('choices', []):
                        delta = choice.get('delta', {})
                        if delta.get('content'):
                            summary.setdefault('first_content_seconds', elapsed)
                            content.append(delta['content'])
                        if choice.get('finish_reason'):
                            summary['finish'] = choice['finish_reason']
                    if cancel_after and len(content) >= cancel_after:
                        summary['cancelled_by_client'] = True
                        break
        except Exception as error:
            summary['error'] = repr(error)
            if hasattr(error, 'read'):
                summary['error_body'] = error.read().decode('utf-8', errors='replace')
        summary['seconds'] = time.monotonic() - started
        summary['content'] = ''.join(content)
        save(self.folder / (name + '-result.json'), summary)
        save(self.folder / (name + '-stream.json'), raw)
        self.idle(name + '_idle')
        print(json.dumps({k: v for k, v in summary.items() if k != 'content'}, ensure_ascii=False), flush=True)
        return summary

    def close(self):
        self.watchdog.cancel()
        self.phase = 'shutdown'
        if self.process.poll() is None:
            self.process.terminate()
            self.process.wait(timeout=10)
        time.sleep(1)
        self.stop_event.set()
        self.monitor.join(timeout=5)
        save(self.folder / 'resources.json', self.samples)
        self.log.close()
        print('STOPPED ' + self.folder.name, flush=True)
