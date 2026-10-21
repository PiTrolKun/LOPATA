"""Prepare reviewed, hash-pinned wheel manifests; never pip-install into an existing runtime.

Run with Python 3.12. Downloads go to Runtime/MusicStems/cache. Subsequent runs
use the committed manifest, not mutable package metadata. --refresh is a developer
operation for a deliberate dependency revision, never called by the application.
"""
import argparse
import hashlib
import json
import urllib.request
import time
import zipfile
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
DEST = ROOT / 'Исходники/AIHub/Tools/music-stems-components.json'
CACHE = ROOT / 'Runtime/MusicStems/cache'
VERSIONS = [('onnxruntime', '1.23.2', 'cpu'), ('onnxruntime-gpu', '1.23.2', 'cuda'),
            ('numpy', '2.2.6', 'common'), ('flatbuffers', '25.2.10', 'common'),
            ('packaging', '25.0', 'common'), ('protobuf', '6.31.1', 'common'),
            ('sympy', '1.14.0', 'common'), ('mpmath', '1.3.0', 'common'),
            ('coloredlogs', '15.0.1', 'common'), ('humanfriendly', '10.0', 'common')]


def fetch(url):
    for attempt in range(3):
        try:
            with urllib.request.urlopen(url, timeout=90) as source:
                return source.read()
        except OSError:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def prepare(refresh=False):
    CACHE.mkdir(parents=True, exist_ok=True)
    old = json.loads(DEST.read_text('utf-8')) if DEST.exists() and not refresh else []
    rows = []
    notices = []
    for name, version, role in VERSIONS:
        saved = next((x for x in old if x['name'] == name and x['version'] == version), None)
        if saved:
            row = saved
        else:
            metadata = json.loads(fetch(f'https://pypi.org/pypi/{name}/{version}/json'))
            candidates = [x for x in metadata['urls'] if x['filename'].endswith('.whl') and
                          ('cp312-cp312-win_amd64' in x['filename'] or
                           'cp39-abi3-win_amd64' in x['filename'] or
                           x['filename'].endswith('py3-none-any.whl') or
                           x['filename'].endswith('py2.py3-none-any.whl'))]
            if len(candidates) != 1:
                raise ValueError((name, [x['filename'] for x in candidates]))
            wheel = candidates[0]
            row = dict(id='runtime.music-stems.' + name, name=name, version=version, role=role,
                       url=wheel['url'], file=wheel['filename'], sha256=wheel['digests']['sha256'], size=wheel['size'])
        path = CACHE / row['file']
        if not path.exists() or hashlib.sha256(path.read_bytes()).hexdigest() != row['sha256']:
            data = fetch(row['url'])
            if len(data) != row['size'] or hashlib.sha256(data).hexdigest() != row['sha256']:
                raise ValueError('Wheel digest mismatch: ' + name)
            path.write_bytes(data)
        with zipfile.ZipFile(path) as archive:
            files = []
            for entry in archive.infolist():
                if entry.is_dir():
                    continue
                data = archive.read(entry)
                files.append(dict(path=entry.filename, size=len(data), sha256=hashlib.sha256(data).hexdigest()))
                leaf = Path(entry.filename).name.lower()
                if 'license' in leaf or 'notice' in leaf or 'copying' in leaf:
                    notices.append(f'\n===== {name} {version}: {entry.filename} =====\n' + data.decode('utf-8', errors='replace'))
            row.update(files=files, installedSize=sum(x['size'] for x in files), health=files[0]['path'])
        rows.append(row)
        print(name, version, row['size'], flush=True)
    DEST.parent.mkdir(parents=True, exist_ok=True)
    DEST.write_text(json.dumps(rows, ensure_ascii=False, indent=2) + '\n', 'utf-8')
    licenses = ROOT / 'Исходники/AIHub/Licenses/texts'
    (licenses / 'music.stems_WHEEL-NOTICES.txt').write_text(''.join(notices), 'utf-8')
    card = fetch('https://huggingface.co/StemSplitio/htdemucs-6s-onnx/raw/49df9b6989cf2150840ea65b0bef77a2e471b678/README.md').decode('utf-8')
    (licenses / 'music.stems_MODEL-CARD.md').write_text(card, 'utf-8')
    demucs_license = fetch('https://raw.githubusercontent.com/facebookresearch/demucs/e976d93ecc3865e5757426930257e200846a520a/LICENSE')
    (licenses / 'music.stems_DEMUCS-LICENSE.txt').write_bytes(demucs_license)
    print('Manifest and original notices prepared. Review before appending license catalog.', flush=True)


if __name__ == '__main__':
    parser = argparse.ArgumentParser()
    parser.add_argument('--refresh', action='store_true')
    prepare(parser.parse_args().refresh)
