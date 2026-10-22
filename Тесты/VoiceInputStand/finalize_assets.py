from pathlib import Path
import hashlib, json, shutil, sys
import requests
from prepare_assets import DATA, ROOT, HERE, digest, log, download

def main():
    files = [HERE/'worker.py', HERE/'requirements.freeze.txt', Path(sys.executable)]
    for path in (HERE/'.venv/Lib/site-packages').rglob('*'):
        if path.is_file() and '__pycache__' not in path.parts and path.suffix != '.pyc':
            files.append(path)
    # Hash the actual base interpreter entry points as well; never modify the user's Python.
    files.extend(Path(sys.base_prefix).glob('python*.dll'))
    rows = [dict(path=str(path), bytes=path.stat().st_size, sha256=digest(path)) for path in sorted(set(files))]
    (DATA/'runtime-files.json').write_text(json.dumps(rows,indent=2),encoding='utf-8')
    if '--inventory-only' in sys.argv:
        log('inventory_ready',runtime_files=len(rows))
        return
    licenses = DATA/'licenses'
    licenses.mkdir(exist_ok=True)
    sources = {
      'Parakeet-model-card.md': 'https://huggingface.co/nvidia/parakeet-tdt-0.6b-v3/raw/main/README.md',
      'CC-BY-4.0.txt': 'https://raw.githubusercontent.com/spdx/license-list-data/main/text/CC-BY-4.0.txt',
    }
    license_rows=[]
    for name,url in sources.items():
        response=requests.get(url,timeout=60);response.raise_for_status()
        path=licenses/name;path.write_bytes(response.content)
        license_rows.append(dict(file=name,url=url,sha256=digest(path)))
    for path in (HERE/'.venv/Lib/site-packages').rglob('*'):
        if path.is_file() and any('.dist-info' in part for part in path.parts) and ('license' in path.name.lower() or 'copying' in path.name.lower()):
            target=licenses/'python'/path.relative_to(HERE/'.venv/Lib/site-packages')
            target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(path,target)
    (licenses/'sources.json').write_text(json.dumps(license_rows,indent=2),encoding='utf-8')
    info=requests.get('https://huggingface.co/api/models/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8?blobs=true',timeout=60).json()
    row=next(x for x in info['siblings'] if x['rfilename']=='test_wavs/en.wav')
    download(f"https://huggingface.co/csukuangfj/sherpa-onnx-nemo-parakeet-tdt-0.6b-v3-int8/resolve/{info['sha']}/test_wavs/en.wav",
        DATA/'samples/en.wav',row.get('lfs',{}).get('sha256'),row.get('size'),None if row.get('lfs') else row.get('blobId'))
    (DATA/'preparation-receipt.json').write_text(json.dumps(dict(runtimeFiles=len(rows),
        gpu=False,models=json.loads((DATA/'models.json').read_text()),
        python=sys.version,licenseSources=license_rows),indent=2),encoding='utf-8')
    log('runtime_ready',runtime_files=len(rows),selected='parakeet')

if __name__=='__main__':main()
