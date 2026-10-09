"""Pin author source, HF metadata and Windows wheels. Never downloads model weights.

Run with the project's Python 3.12 tooling. --runtime is the managed Torch 2.10
Python executable. Output is staged before any application runtime is changed.
"""
import argparse
import base64
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import subprocess
import tarfile
import tomllib
import urllib.request
import urllib.parse
import zipfile

REVISION = '5858ca8f1ffe58d62be7c007ea55f1033c5ca456'
MODEL_REVISIONS = {
    'HeartMuLa/HeartMuLa-oss-3B-happy-new-year': '41f6fc68490e11dc43fdabaa6b5767946408c903',
    'HeartMuLa/HeartCodec-oss-20260123': 'f889dab0532cfa4bf459f2a3367eb6d346b8eeda',
    'HeartMuLa/HeartMuLaGen': '2e18e01702011f4f7dc8642b6260967df62417ea'}
parser = argparse.ArgumentParser()
parser.add_argument('--runtime', required=True)
parser.add_argument('--stage', required=True)
args = parser.parse_args()
stage = Path(args.stage).resolve()
stage.mkdir(parents=True, exist_ok=True)

def fetch(url):
    return subprocess.run(['curl.exe', '-L', '--fail', '--retry', '2', '--max-time', '90', '-sS', url],
        check=True, stdout=subprocess.PIPE).stdout

def save_json(path, data):
    path.write_text(json.dumps(data, ensure_ascii=False, indent=2), encoding='utf-8')

tree_metadata = json.loads((stage.parent / 'heartmula-tree.json').read_text(encoding='utf-8-sig')) if (stage.parent / 'heartmula-tree.json').exists() else json.loads(fetch('https://api.github.com/repos/HeartMuLa/heartlib/git/trees/' + REVISION + '?recursive=1'))
def source_file(entry):
    path = entry['path']
    cached = stage / 'source' / path
    data = cached.read_bytes() if cached.is_file() else base64.b64decode(json.loads(fetch(entry['url']))['content'])
    git_hash = hashlib.sha1(b'blob ' + str(len(data)).encode() + b'\0' + data).hexdigest()
    if git_hash != entry['sha']: raise ValueError('Author git blob mismatch: ' + path)
    cached.parent.mkdir(parents=True, exist_ok=True)
    cached.write_bytes(data)
    print('Source', path, flush=True)
    return path, data
# Runtime source and notices; demonstration media is not executable and is not
# redistributed. Every included file is checked against its pinned Git blob.
entries = [e for e in tree_metadata['tree'] if e['type'] == 'blob' and
    (not e['path'].startswith('assets/') or e['path'].endswith('.txt'))]
with concurrent.futures.ThreadPoolExecutor(max_workers=4) as pool:
    files = dict(pool.map(source_file, entries))
tree = stage / 'source'
tree.mkdir(exist_ok=True)
for path, data in files.items():
    target = tree / path
    target.parent.mkdir(parents=True, exist_ok=True)
    target.write_bytes(data)
save_json(stage / 'source-origin.json', {'revision': REVISION, 'files': {e['path']: e['sha'] for e in entries}})

manifest = {'Model': [], 'Companions': [], 'Runtime': []}
revisions = {}
for repo, group, prefix, selected in [
    ('HeartMuLa/HeartMuLa-oss-3B-happy-new-year', 'Model', 'HeartMuLa-oss-3B', None),
    ('HeartMuLa/HeartCodec-oss-20260123', 'Companions', 'HeartCodec-oss', None),
    ('HeartMuLa/HeartMuLaGen', 'Model', '', ['config.json', 'gen_config.json', 'tokenizer.json'])]:
    cached_metadata = stage / (repo.split('/')[-1] + '-metadata.json')
    metadata = json.loads(cached_metadata.read_text(encoding='utf-8-sig')) if cached_metadata.exists() else json.loads(fetch('https://huggingface.co/api/models/' + repo + '/revision/' + MODEL_REVISIONS[repo] + '?blobs=true'))
    if metadata['sha'] != MODEL_REVISIONS[repo]: raise ValueError('Unexpected model revision: ' + repo)
    revisions[repo] = metadata['sha']
    save_json(stage / (repo.split('/')[-1] + '-metadata.json'), metadata)
    for item in metadata['siblings']:
        path = item['rfilename']
        if selected is not None and path not in selected: continue
        if selected is None and not (path.endswith('.safetensors') or path.endswith('.json')): continue
        url = 'https://huggingface.co/' + repo + '/resolve/' + metadata['sha'] + '/' + path
        digest = item.get('lfs', {}).get('sha256')
        if not digest: digest = hashlib.sha256(fetch(url)).hexdigest()
        manifest[group].append({'RelativePath': '/'.join(filter(None, [prefix, path])), 'SizeBytes': item['size'], 'Sha256': digest.upper(), 'SourceUrl': url})
save_json(stage / 'revisions.json', revisions)
save_json(stage / 'models.json', manifest)

dependencies = tomllib.loads(files['pyproject.toml'].decode())['project']['dependencies']
dependencies = [d for d in dependencies if not d.startswith(('torch>', 'torchaudio>', 'torchvision>'))]
# The managed hardware pack supplies Torch. Unused audio/video operators are CPU
# wheels of the matching 2.10 ABI; generation uses soundfile for WAV publication.
dependencies += ['torchaudio==2.10.0+cpu', 'torchvision==0.25.0+cpu', 'ipython==8.12.3']
report = stage / 'pip-report.json'
if not report.exists():
    subprocess.run([args.runtime, '-m', 'pip', 'install', '--dry-run', '--ignore-installed', '--only-binary=:all:',
        '--extra-index-url', 'https://download.pytorch.org/whl/cpu', '--report', str(report), *dependencies, 'torch==2.10.0+cpu'], check=True)
plan = json.loads(report.read_text(encoding='utf-8'))
wheel_dir = stage / 'wheels'
wheel_dir.mkdir(exist_ok=True)
overlay = stage / 'overlay'
overlay.mkdir(parents=True, exist_ok=True)
for row in plan['install']:
    if row['metadata']['name'].lower() == 'torch': continue
    url = row['download_info']['url']
    filename = url.rsplit('/', 1)[-1].split('#')[0]
    filename = urllib.parse.unquote(filename)
    target = wheel_dir / filename
    expected = row['download_info']['archive_info'].get('hashes', {}).get('sha256')
    if not expected:
        package = json.loads(fetch('https://pypi.org/pypi/' + row['metadata']['name'] + '/' + row['metadata']['version'] + '/json'))
        pinned = next(f for f in package['urls'] if f['filename'] == filename)
        expected = pinned['digests']['sha256']
        url = pinned['url']
    if not target.exists(): target.write_bytes(fetch(url))
    data = target.read_bytes()
    if hashlib.sha256(data).hexdigest() != expected: raise ValueError(filename)
    manifest['Runtime'].append({'RelativePath': filename, 'SizeBytes': len(data), 'Sha256': expected.upper(), 'SourceUrl': url})
    with zipfile.ZipFile(io.BytesIO(data)) as wheel:
        for entry in wheel.infolist():
            if entry.is_dir(): continue
            path = entry.filename
            if '.data/' in path:
                package, kind, remainder = path.split('/', 2)
                path = {'purelib': 'Lib/site-packages/', 'platlib': 'Lib/site-packages/',
                    'data': '', 'scripts': 'Scripts/', 'headers': 'Include/' + package + '/'}[kind] + remainder
            else:
                path = 'Lib/site-packages/' + path
            destination = overlay / path
            if not destination.resolve().is_relative_to(overlay.resolve()): raise ValueError(path)
            destination.parent.mkdir(parents=True, exist_ok=True)
            destination.write_bytes(wheel.read(entry))
    print('Prepared', filename, flush=True)
save_json(stage / 'models.json', manifest)
save_json(stage / 'python-files.json', {'wheels': [{'path': f['RelativePath'], 'sha256': f['Sha256']} for f in manifest['Runtime']],
    'files': [{'path': p.relative_to(overlay).as_posix(), 'size': p.stat().st_size,
        'sha256': hashlib.sha256(p.read_bytes()).hexdigest().upper()} for p in sorted(overlay.rglob('*')) if p.is_file()]})
print('Prepared metadata and overlay; no model weights downloaded.', flush=True)
