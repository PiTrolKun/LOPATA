"""Rebuild pinned upstream code from author Space and hashed PyPI sdists.

Notices are retained verbatim from the verified distributed archive. No model
weights, environment installation, or application files are changed.
"""
import argparse
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import tarfile
import urllib.request
import zipfile

parser = argparse.ArgumentParser()
parser.add_argument('--stage', required=True)
args = parser.parse_args()
workspace = Path(__file__).resolve().parents[2]
stage = Path(args.stage).resolve()
stage.mkdir(parents=True, exist_ok=True)
archive = workspace/'Runtime/MusicDiffRhythm/source.zip'
expected = '1A5ED6B66B125A7A69AC7867391FC619B30BE29FFA07C78D1FB8E884C1BCACF8'
assert hashlib.sha256(archive.read_bytes()).hexdigest().upper() == expected
with zipfile.ZipFile(archive) as shipped:
    manifest_bytes = shipped.read('manifest.json')
    manifest = json.loads(manifest_bytes)
    dependency_report = json.loads(shipped.read('notices/DEPENDENCIES.json'))['pip_report']
    notices = {p: shipped.read(p) for p in manifest['Files'] if p.startswith('notices/')}

def fetch(url):
    with urllib.request.urlopen(url,timeout=90) as response:
        return response.read()

packages = {}
for row in dependency_report['install']:
    name = row['metadata']['name'].lower()
    if name not in ('muq','jieba'): continue
    data = fetch(row['download_info']['url'])
    assert hashlib.sha256(data).hexdigest() == row['download_info']['archive_info']['hashes']['sha256']
    packages[name] = {}
    with tarfile.open(fileobj=io.BytesIO(data)) as source:
        for item in source.getmembers():
            if not item.isfile(): continue
            parts = item.name.split('/',1)
            prefix = 'src/muq/' if name == 'muq' else 'jieba/'
            if len(parts)==2 and parts[1].startswith(prefix):
                packages[name][name+'/'+parts[1][len(prefix):]] = source.extractfile(item).read()

def rebuild(path):
    if path.startswith('notices/'): data = notices[path]
    elif path.startswith(('muq/','jieba/')): data = packages[path.split('/')[0]][path]
    else:
        data = fetch('https://huggingface.co/spaces/ASLP-lab/DiffRhythm2/resolve/'+manifest['Revision']+'/'+path)
    assert hashlib.sha256(data).hexdigest() == manifest['Files'][path], path
    return path,data

with concurrent.futures.ThreadPoolExecutor(max_workers=6) as pool:
    files = dict(pool.map(rebuild,manifest['Files']))
files['manifest.json'] = manifest_bytes
result = stage/'source-rebuilt.zip'
with zipfile.ZipFile(result,'w',compression=zipfile.ZIP_DEFLATED,compresslevel=9) as out:
    for path,data in sorted(files.items(),key=lambda item: item[0].casefold()):
        info = zipfile.ZipInfo(path,date_time=(2026,10,9,0,0,0))
        info.compress_type = zipfile.ZIP_DEFLATED
        out.writestr(info,data)
assert hashlib.sha256(result.read_bytes()).hexdigest().upper() == expected
print('Pinned source rebuilt and SHA256 matched:',result)
