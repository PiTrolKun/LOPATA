"""Download the two pinned Jelly candidates using the standard Range downloader."""
import argparse
import hashlib
import importlib.util
import json
import msvcrt
import os
from pathlib import Path
import re
import time
import urllib.parse
import urllib.request


HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DESTINATION = ROOT / 'Тесты' / 'JellyModels' / 'models'
STATUS = DESTINATION / 'download-status.json'


def child_path(parent, name):
    if not name or '\\' in name or ':' in name:
        raise ValueError(f'Invalid relative path: {name!r}')
    relative = Path(name)
    if relative.is_absolute() or any(p in ('.', '..') for p in relative.parts):
        raise ValueError(f'Invalid relative path: {name!r}')
    target = (parent / relative).resolve()
    if not target.is_relative_to(parent.resolve()) or target == parent.resolve():
        raise ValueError('Download path escapes its directory')
    return target


def read_config():
    config = json.loads((HERE / 'config.json').read_text(encoding='utf-8-sig'))
    if not 1 <= config['workers'] <= 8 or config['chunk_bytes'] <= 0:
        raise ValueError('Invalid download settings')
    destinations = set()
    for model in config['models']:
        if not re.fullmatch(r'[\w.-]+/[\w.-]+', model['repository']):
            raise ValueError('Invalid repository')
        if not re.fullmatch(r'[a-f0-9]{40}', model['revision']):
            raise ValueError('An immutable model revision is required')
        directory = child_path(DESTINATION, model['directory'])
        for entry in model['files']:
            target = child_path(directory, entry['filename'])
            if target in destinations or entry['size'] <= 0:
                raise ValueError('Duplicate file or invalid size')
            destinations.add(target)
            checksum = entry.get('sha256') or entry.get('git_blob_sha1', '')
            length = 64 if 'sha256' in entry else 40
            if not re.fullmatch(r'[a-f0-9]{' + str(length) + '}', checksum):
                raise ValueError('A checksum is required for every file')
    return config


def save_status(state, **details):
    value = dict(state=state, updated_at=time.strftime('%Y-%m-%dT%H:%M:%S%z'), **details)
    temporary = STATUS.with_suffix('.tmp')
    temporary.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')
    os.replace(temporary, STATUS)


def git_blob_hash(data):
    return hashlib.sha1(f'blob {len(data)}\0'.encode('ascii') + data).hexdigest()


def download_small(entry, target, url):
    def verify(data):
        if len(data) != entry['size'] or git_blob_hash(data) != entry['git_blob_sha1']:
            raise ValueError(f'Size/Git blob checksum mismatch: {entry["filename"]}')

    if target.exists():
        verify(target.read_bytes())
        return
    for attempt in range(5):
        try:
            request = urllib.request.Request(url, headers={
                'Accept-Encoding': 'identity', 'User-Agent': 'LOPATA-Jelly-Downloader/1'})
            with urllib.request.urlopen(request, timeout=45) as response:
                data = response.read(entry['size'] + 1)
            verify(data)
            target.parent.mkdir(parents=True, exist_ok=True)
            temporary = target.with_name(target.name + '.assembling')
            temporary.write_bytes(data)
            os.replace(temporary, target)
            return
        except (OSError, ValueError):
            if attempt == 4:
                raise
            time.sleep(min(2 ** attempt, 8))


def run(config):
    spec = importlib.util.spec_from_file_location(
        'standard_range_download', HERE.parent / 'Runeweaver' / 'download.py')
    standard = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(standard)
    completed = 0
    total = sum(entry['size'] for model in config['models'] for entry in model['files'])
    print(f'Всего: {total / 1e9:.2f} ГБ. Загрузка по очереди, до {config["workers"]} потоков.', flush=True)
    print('Повторный запуск продолжит загрузку. Модели автоматически не запускаются.', flush=True)
    for model in config['models']:
        directory = child_path(DESTINATION, model['directory'])
        directory.mkdir(parents=True, exist_ok=True)
        model_size = sum(entry['size'] for entry in model['files'])
        print(f'\n{model["repository"]}: {model_size / 1e9:.2f} ГБ\n{directory}', flush=True)
        # Small configuration files first; large files retain the standard resumable download.
        for entry in sorted(model['files'], key=lambda item: 'sha256' in item):
            target = child_path(directory, entry['filename'])
            url = (f'https://huggingface.co/{model["repository"]}/resolve/'
                   f'{model["revision"]}/{urllib.parse.quote(entry["filename"], safe="/")}')
            save_status('downloading', model=model['repository'], file=entry['filename'],
                        verified_bytes=completed, total_bytes=total)
            print(f'\nФайл: {entry["filename"]}', flush=True)
            if 'sha256' in entry:
                target.parent.mkdir(parents=True, exist_ok=True)
                standard.download(dict(entry, filename=target.name, workers=config['workers'],
                                       chunk_bytes=config['chunk_bytes']), target.parent, url)
            else:
                download_small(entry, target, url)
            completed += entry['size']
            print(f'Проверен. Общая готовность: {completed / total:.1%}', flush=True)
        print(f'\nГОТОВО: {model["repository"]}. Все файлы проверены.', flush=True)
    save_status('complete', verified_bytes=completed, total_bytes=total)
    print('\nОБЕ МОДЕЛИ СКАЧАНЫ И ПРОВЕРЕНЫ. Можно закрыть окно.', flush=True)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--check', action='store_true', help='Validate the manifest without downloading')
    args = parser.parse_args()
    config = read_config()
    if args.check:
        for model in config['models']:
            size = sum(entry['size'] for entry in model['files'])
            print(f'{model["repository"]}: {len(model["files"])} files, {size} bytes')
        return
    DESTINATION.mkdir(parents=True, exist_ok=True)
    # One lock covers both packages; closing the console releases it automatically.
    with (DESTINATION / 'download.lock').open('a+b') as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b'0')
            lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        try:
            run(config)
        except BaseException as error:
            save_status('interrupted' if isinstance(error, KeyboardInterrupt) else 'error',
                        error=str(error))
            raise


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'\nОШИБКА: {error}\nЗапустите скрипт ещё раз для продолжения.', flush=True)
        raise SystemExit(1)
