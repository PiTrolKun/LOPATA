"""Sequential Vikhr fact extraction -> Runeweaver final output; matched controls."""
import contextlib
import hashlib
import json
import os
from pathlib import Path
import socket
import subprocess
import sys
import time
import urllib.request

sys.path.insert(0, str(Path(__file__).resolve().parent))
from native_probe import GROUND, infer, save

ROOT = Path(__file__).resolve().parents[2]
PRIOR = ROOT / 'Тесты/VikhrNemo/runs/first-01/native'


@contextlib.contextmanager
def server(name, out):
    config = json.loads((ROOT / f'Инструменты/{name}/config.json').read_text(encoding='utf-8-sig'))
    model = ROOT / f'Тесты/{name}/model' / config['filename']
    backend = ROOT / 'Runtime/Backends/llama.cpp/b9442/win-cuda-12.4-x64/llama-server.exe'
    with socket.socket() as sock:
        sock.bind(('127.0.0.1', 0))
        port = sock.getsockname()[1]
    endpoint = f'http://127.0.0.1:{port}/'
    cmd = [str(backend), '-m', str(model), '--host', '127.0.0.1', '--port', str(port),
           '-c', '16384', '-np', '1', '--fit', 'off', '-ngl', '99', '--cache-ram', '0',
           '--offline', '--no-context-shift', '--jinja', '--no-prefill-assistant',
           '--reasoning', 'off', '--reasoning-budget', '0', '-t', '8', '-tb', '8']
    info = dict(command=cmd, modelBytes=model.stat().st_size, status='starting')
    started = time.monotonic()
    process = None
    with (out / f'{name}-server.log').open('w', encoding='utf-8') as log:
        try:
            process = subprocess.Popen(cmd, cwd=backend.parent,
                env={k: v for k, v in os.environ.items() if not k.startswith('LLAMA_ARG_')},
                stdout=log, stderr=log, creationflags=subprocess.CREATE_NO_WINDOW)
            info['pid'] = process.pid
            save(out / f'{name}-launch.json', info)
            while True:
                if process.poll() is not None:
                    raise RuntimeError(f'Server exited: {process.returncode}')
                if time.monotonic() - started > 150:
                    raise TimeoutError('Model startup')
                try:
                    with urllib.request.urlopen(endpoint + 'health', timeout=2) as response:
                        if response.status == 200:
                            break
                except OSError:
                    time.sleep(.4)
            info['readySeconds'] = time.monotonic() - started
            print(name, 'ready', round(info['readySeconds'], 2), flush=True)
            yield endpoint
            info['status'] = 'complete'
        except BaseException as error:
            info.update(status='error', error=repr(error))
            raise
        finally:
            if process is not None and process.poll() is None:
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'],
                    capture_output=True, timeout=15, creationflags=subprocess.CREATE_NO_WINDOW)
                process.wait(timeout=15)
            info['elapsedSeconds'] = time.monotonic() - started
            save(out / f'{name}-outcome.json', info)


def main():
    out = Path(sys.argv[1]).resolve()
    out.mkdir(parents=True, exist_ok=False)
    docs = json.loads((PRIOR / 'documents.json').read_text(encoding='utf-8'))
    truth = json.loads((PRIOR / 'ground-truth.json').read_text(encoding='utf-8'))
    digest = hashlib.sha256('\n\n'.join(d['content'] for d in docs).encode()).hexdigest()
    assert digest.upper() == truth['sourceSha256'].upper()
    save(out / 'documents.json', docs)
    save(out / 'ground-truth.json', truth)
    roles = {}
    for role in ['advisor', 'writer']:
        request = json.loads((PRIOR / f'{role}-role-0-request.json').read_text(encoding='utf-8'))
        system = request['messages'][0]['content']
        task = request['messages'][1]['content'].split('\n\nЗадание автора:\n', 1)[1]
        roles[role] = (system, task)
    save(out / 'tasks.json', roles)
    facts_only = '--facts-only' in sys.argv
    save(out / 'variant.json', dict(factsOnly=facts_only, queryRouting='Manually specified stand queries; no automatic planner'))
    records = []
    with server('VikhrNemo', out) as endpoint:
        for repeat in range(3):
            for role, (_, task) in roles.items():
                key = f'{role}-{repeat}'
                # The exact author task is preserved separately for the final stage.
                question = ('Подготовь фактическую справку по документам для выполнения задания ниже. '
                    'Само задание не выполняй: не пиши сцену и не предлагай сюжет. '
                    'Укажи относящиеся к нему факты оригинала с номерами документов, '
                    'кратко приведи опорные цитаты. Не приписывай оригиналу изменения автора. '
                    'Отметь, какие необходимые сведения отсутствуют.\n\nЗадание автора:\n' + task)
                if facts_only:
                    question = {
                        'advisor': 'Чем заканчивается история Нэлвы Риан в оригинале? Куда она уезжает, какую работу выбирает и почему? Что делает Орт Девель? Приведи только факты книги с номерами документов, без советов и новых сюжетов.',
                        'writer': 'Какие ровно три предмета в каком порядке открывают шлюз? Что именно делает каждый предмет? Какие отношения у Нэлвы Риан и Орта Девеля? Приведи только факты книги с номерами документов, без советов и новых сюжетов.'
                    }[role]
                messages = [dict(role='system', content=GROUND),
                            dict(role='documents', content=json.dumps(docs, ensure_ascii=False)),
                            dict(role='user', content=question)]
                selected = infer(endpoint, out, key + '-source-select', messages, 701 + repeat, 0, 256)
                selection = json.loads(selected)['relevant_doc_ids']
                if not isinstance(selection, list) or any(type(i) is not int or i not in range(len(docs)) for i in selection):
                    raise ValueError(f'Invalid document selection: {selected}')
                messages.append(dict(role='assistant', content=selected))
                brief = infer(endpoint, out, key + '-source-brief', messages, 701 + repeat, .3, 1800)
                record = dict(key=key, role=role, repeat=repeat, ids=selection, brief=brief)
                records.append(record)
                save(out / 'buffers.json', records)
                print(key, 'Vikhr documents', selection, 'brief chars', len(brief), flush=True)
    results = []
    with server('Runeweaver', out) as endpoint:
        for record in records:
            role, repeat = record['role'], record['repeat']
            system, task = roles[role]
            modes = ['brief', 'brief_documents'] if facts_only else ['direct', 'brief', 'brief_documents']
            for mode in modes:
                if mode == 'direct':
                    material = 'Первоисточник:\n' + json.dumps(docs, ensure_ascii=False)
                else:
                    material = ('Справка читателя по первоисточнику (это справочный материал, '
                        'не задание автора):\n' + record['brief'])
                    if mode == 'brief_documents':
                        selected_docs = [d for d in docs if d['doc_id'] in record['ids']]
                        material += '\n\nОригинальные документы:\n' + json.dumps(selected_docs, ensure_ascii=False)
                messages = [dict(role='system', content=system),
                            dict(role='user', content=material + '\n\nЗадание автора:\n' + task)]
                key = f'{role}-{repeat}-{mode}'
                answer = infer(endpoint, out, key, messages, 701 + repeat, .5, 2048)
                result = dict(key=key, role=role, repeat=repeat, mode=mode, answer=answer)
                results.append(result)
                save(out / 'summary.json', results)
                print(key, 'answer chars', len(answer), flush=True)
    print('COMPLETE', len(records), 'buffers;', len(results), 'final outputs', flush=True)


if __name__ == '__main__':
    main()
