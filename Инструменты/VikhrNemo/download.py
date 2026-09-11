"""Pinned, resumable Range download. No third-party packages."""
import concurrent.futures
import hashlib
import json
import msvcrt
import os
from pathlib import Path
import shutil
import time
import urllib.request


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def fetch_part(url, path, start, end, total):
    expected = end - start + 1
    if path.exists() and path.stat().st_size > expected:
        raise ValueError(f'Oversized part: {path}')
    for attempt in range(5):
        offset = path.stat().st_size if path.exists() else 0
        if offset == expected:
            return
        try:
            request = urllib.request.Request(url, headers={
                'Range': f'bytes={start + offset}-{end}',
                'Accept-Encoding': 'identity', 'User-Agent': 'LOPATA-VikhrNemo-Stand/1'})
            with urllib.request.urlopen(request, timeout=45) as response:
                wanted = f'bytes {start + offset}-{end}/{total}'
                if response.status != 206 or response.headers.get('Content-Range') != wanted:
                    raise ValueError('Server did not honor exact HTTP Range')
                with path.open('ab') as output:
                    remaining = expected - offset
                    while remaining:
                        data = response.read(min(1024 * 1024, remaining))
                        if not data:
                            raise OSError('Connection ended before range completed')
                        output.write(data)
                        remaining -= len(data)
        except (OSError, ValueError) as error:
            if attempt == 4:
                raise RuntimeError(f'Part {path.name}: {error}') from error
            time.sleep(min(2 ** attempt, 8))


def download(config, directory, url=None):
    directory.mkdir(parents=True, exist_ok=True)
    target = directory / config['filename']
    if target.exists():
        print('Проверяем уже скачанный файл...', flush=True)
        if target.stat().st_size != config['size'] or digest(target) != config['sha256']:
            raise ValueError('Existing model failed verification; file preserved for inspection')
        return target
    parts = directory / (config['filename'] + '.parts')
    parts.mkdir(exist_ok=True)
    partial = directory / (config['filename'] + '.assembling')
    # Conservatively allow room for both range files and final assembly.
    saved = sum(p.stat().st_size for p in parts.glob('*.part'))
    available = shutil.disk_usage(directory).free
    required = 2 * config['size'] - saved - (partial.stat().st_size if partial.exists() else 0)
    if available < required + 256 * 1024 * 1024:
        raise OSError('Not enough free disk space for parts and assembled model')
    url = url or (f"https://huggingface.co/{config['repository']}/resolve/"
                  f"{config['revision']}/{config['filename']}")
    jobs = []
    print(f"Скачивание: {config['size'] / 1e9:.2f} ГБ, потоков: {config['workers']}", flush=True)
    with concurrent.futures.ThreadPoolExecutor(max_workers=config['workers']) as pool:
        for number, start in enumerate(range(0, config['size'], config['chunk_bytes'])):
            jobs.append(pool.submit(fetch_part, url, parts / f'{number:05d}.part', start,
                                    min(start + config['chunk_bytes'], config['size']) - 1,
                                    config['size']))
        pending = set(jobs)
        while pending:
            done, pending = concurrent.futures.wait(pending, timeout=2,
                return_when=concurrent.futures.FIRST_EXCEPTION)
            for future in done:
                if future.exception():
                    for other in pending:
                        other.cancel()
                    future.result()
            downloaded = sum(p.stat().st_size for p in parts.glob('*.part'))
            print(f'\rЗагружено {downloaded / config["size"]:6.1%} '
                  f'({downloaded / 1e9:.2f} / {config["size"] / 1e9:.2f} ГБ)',
                  end='', flush=True)
    print('\nСобираем файл и проверяем SHA256...', flush=True)
    with partial.open('wb') as output:
        for part in sorted(parts.glob('*.part')):
            with part.open('rb') as source:
                shutil.copyfileobj(source, output, 4 * 1024 * 1024)
    if partial.stat().st_size != config['size'] or digest(partial) != config['sha256']:
        raise ValueError('SHA256/size mismatch; parts preserved, model NOT ready')
    os.replace(partial, target)
    # Delete only this downloader's exact numbered part files after verification.
    for number in range(len(jobs)):
        (parts / f'{number:05d}.part').unlink()
    parts.rmdir()
    return target


def main():
    root = Path(__file__).resolve().parents[2]
    directory = root / 'Тесты' / 'VikhrNemo' / 'model'
    directory.mkdir(parents=True, exist_ok=True)
    config = json.loads(Path(__file__).with_name('config.json').read_text(encoding='utf-8-sig'))
    # OS lock releases even after terminal closure; duplicate launch cannot corrupt parts.
    with (directory / 'download.lock').open('a+b') as lock:
        lock.seek(0)
        if not lock.read(1):
            lock.write(b'0')
            lock.flush()
        lock.seek(0)
        msvcrt.locking(lock.fileno(), msvcrt.LK_NBLCK, 1)
        target = download(config, directory)
        print(f'\nГОТОВО. Размер и SHA256 совпали.\n{target}', flush=True)


if __name__ == '__main__':
    try:
        main()
    except Exception as error:
        print(f'\nОШИБКА: {error}\nПовторный запуск продолжит скачивание.', flush=True)
        raise SystemExit(1)
