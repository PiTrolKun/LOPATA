"""Isolated research assets: revision-pinned downloads, resumable transfers and receipts."""
from pathlib import Path
import concurrent.futures, hashlib, json, os, time
import requests

HERE = Path(__file__).resolve().parent
ROOT = HERE.parents[1]
DATA = ROOT / '_tmp/voice-input-stand'
DATA.mkdir(parents=True, exist_ok=True)
PINS = {
    'csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8': '2bda32ec70b097a55adaa07d9a7173915b43cc78',
}

def digest(path):
    with path.open('rb') as f:
        return hashlib.file_digest(f, 'sha256').hexdigest()

def log(event, **data):
    row = dict(utc=time.time(), event=event, **data)
    with (DATA / 'preparation.jsonl').open('a', encoding='utf-8') as f:
        f.write(json.dumps(row, ensure_ascii=False) + '\n')
    print(json.dumps(row, ensure_ascii=False), flush=True)

def download(url, path, expected=None, size=None, git_oid=None):
    path.parent.mkdir(parents=True, exist_ok=True)
    if path.is_file() and (not size or path.stat().st_size == size):
        value = digest(path)
        if expected and value == expected:
            log('reuse_verified', file=str(path), sha256=value)
            return value
    part = path.with_name(path.name + '.part')
    for attempt in range(8):
        try:
            offset = part.stat().st_size if part.exists() else 0
            headers = {'Range': f'bytes={offset}-'} if offset else {}
            with requests.get(url, headers=headers, stream=True, timeout=(30, 90)) as response:
                response.raise_for_status()
                mode = 'ab' if offset and response.status_code == 206 else 'wb'
                log('download_start', url=url, file=str(path), offset=offset, attempt=attempt)
                last = time.monotonic()
                with part.open(mode) as output:
                    for block in response.iter_content(1024 * 1024):
                        output.write(block)
                        if time.monotonic() - last >= 3:
                            last = time.monotonic()
                            log('download_progress', file=path.name, bytes=output.tell(), total=size)
            if size and part.stat().st_size != size:
                raise ValueError(f'Size mismatch: {path}')
            value = digest(part)
            if expected and value != expected:
                part.unlink()
                raise ValueError(f'SHA256 mismatch: {path}')
            if git_oid:
                raw = part.read_bytes()
                assert hashlib.sha1(f'blob {len(raw)}\0'.encode() + raw).hexdigest() == git_oid
            os.replace(part, path)
            log('download_verified', file=str(path), sha256=value, bytes=path.stat().st_size)
            return value
        except Exception as error:
            log('download_retry', file=path.name, attempt=attempt, error=repr(error))
            if attempt == 7:
                raise
            time.sleep(min(2 ** attempt, 20))

def hf(repo, names, folder):
    info = requests.get(f'https://huggingface.co/api/models/{repo}/revision/{PINS[repo]}?blobs=true', timeout=60).json()
    revision = info['sha']
    assets = []
    def one(name):
        row = next(x for x in info['siblings'] if x['rfilename'] == name)
        path = DATA / 'models' / folder / name
        lfs = row.get('lfs', {})
        value = download(f'https://huggingface.co/{repo}/resolve/{revision}/{name}', path,
                         lfs.get('sha256'), row.get('size'), None if lfs else row.get('blobId'))
        return dict(path=str(path), sha256=value, bytes=path.stat().st_size)
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        assets = list(pool.map(one, names))
    return dict(repo=repo, revision=revision, files=assets, directory=str(DATA / 'models' / folder))

def main():
    log('prepare_begin', scope='stand-only', gpu=False)
    models = {'parakeet': hf('csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8',
        ['encoder.int8.onnx', 'decoder.int8.onnx', 'joiner.int8.onnx', 'tokens.txt'], 'parakeet')}
    (DATA / 'models.json').write_text(json.dumps(models, ensure_ascii=False, indent=2), encoding='utf-8')
    log('models_ready', candidates=list(models))

if __name__ == '__main__':
    main()
