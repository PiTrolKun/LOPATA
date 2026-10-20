"""Maintainer-only preparation of the pinned bundled office image; never used by the app."""
from pathlib import Path
import hashlib
import json
import subprocess
import sys
import urllib.request
import uuid

ROOT = Path(__file__).resolve().parents[2]
VERSION = '26.2.4.2'
NAME = f'LibreOffice_{VERSION}_Win_x86-64.msi'
URL = f'https://downloadarchive.documentfoundation.org/libreoffice/old/{VERSION}/win/x86_64/{NAME}'
SHA = '202f26cda071c5aa4996a5a28412fddceb3891dceb0366982c62650456c0730f'
SIZE = 372539392
DEST = ROOT / 'Runtime/BookConverter' / f'libreoffice-{VERSION}'


def digest(path):
    with path.open('rb') as stream:
        return hashlib.file_digest(stream, 'sha256').hexdigest()


def prepare():
    # The MSI inside an administrative image is transformed; verify the original download.
    cached = DEST.parent / 'downloads' / NAME
    if not cached.exists():
        cached.parent.mkdir(parents=True, exist_ok=True)
        part = cached.with_suffix('.msi.part')
        with urllib.request.urlopen(URL) as source, part.open('xb') as target:
            while block := source.read(1024 * 1024):
                target.write(block)
        if part.stat().st_size != SIZE or digest(part) != SHA:
            raise ValueError('Pinned office MSI verification failed')
        part.replace(cached)
    if cached.stat().st_size != SIZE or digest(cached) != SHA:
        raise ValueError('Pinned office MSI verification failed')
    if not DEST.exists():
        stage = DEST.with_name(DEST.name + '.stage-' + uuid.uuid4().hex)
        stage.mkdir()
        subprocess.run(['msiexec.exe', '/a', str(cached), '/qn', '/norestart',
                        'TARGETDIR=' + str(stage)], check=True)
        if not (stage / 'program/soffice.com').is_file():
            raise ValueError('Unexpected administrative image; inspect stage')
        stage.replace(DEST)
    previous = DEST / 'provenance.json'
    if previous.exists():
        old = json.loads(previous.read_text(encoding='utf-8-sig'))
        if old['Sha256'].lower() != SHA or old['Version'] != VERSION:
            raise ValueError('Office provenance mismatch')
        for item in old['Files']:
            path = DEST / item['Name']
            if not path.resolve().is_relative_to(DEST.resolve()) or path.is_symlink():
                raise ValueError('Unsafe office path')
            if path.stat().st_size != item['Bytes'] or digest(path) != item['Sha256'].lower():
                raise ValueError('Prepared office image changed: ' + item['Name'])
    files = []
    for path in sorted(DEST.rglob('*')):
        if path.is_symlink():
            raise ValueError('Linked office image entry')
        if not path.is_file() or path.name == 'provenance.json' or path.suffix.lower() == '.msi':
            continue
        files.append({'Name': path.relative_to(DEST).as_posix(),
                      'Bytes': path.stat().st_size, 'Sha256': digest(path)})
    manifest = {'Version': VERSION, 'Source': URL, 'Sha256': SHA, 'Files': files}
    # MSI normally installs these x64 CRT libraries into Windows. Ship identical
    # bytes beside soffice for app-local loading on machines without that CRT.
    for path in sorted((DEST / 'System64').glob('*.dll')):
        name = 'program/' + path.name
        if any(item['Name'].lower() == name.lower() for item in files):
            raise ValueError('Office already provides this app-local CRT: ' + name)
        files.append({'Name': name, 'Bytes': path.stat().st_size, 'Sha256': digest(path)})
    output = ROOT / 'Исходники/AIHub/Tools/book-office-files.json'
    text = json.dumps(manifest, ensure_ascii=False, indent=2) + '\n'
    if output.exists() and output.read_text(encoding='utf-8-sig') != text and '--refresh-manifest' not in sys.argv:
        raise ValueError('Back up existing office manifest before changing it')
    output.write_text(text, encoding='utf-8')
    print(json.dumps({'files': len(files), 'bytes': sum(x['Bytes'] for x in files),
                      'manifest': str(output)}, ensure_ascii=False))


if __name__ == '__main__':
    if '--prepare' not in sys.argv:
        raise SystemExit('Explicit maintainer action required: prepare-office.py --prepare')
    prepare()
