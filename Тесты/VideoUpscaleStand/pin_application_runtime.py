"""Maintenance only: pin the already-tested Windows runtime to original upstream files."""
import importlib.metadata as meta
import json, pathlib, urllib.request, hashlib, sys

def fetch(url):
    with urllib.request.urlopen(url, timeout=90) as f: return json.load(f)

def main():
    root = pathlib.Path(__file__).resolve().parents[2]
    target = root / 'Исходники/AIHub/Tools/VideoUpscale'
    target.mkdir(parents=True, exist_ok=True)
    files = []
    for d in sorted(meta.distributions(), key=lambda d: d.metadata['Name'].lower()):
        name, version = d.metadata['Name'], d.version
        if name.lower() in {'pip', 'setuptools', 'wheel'}: continue
        if name.lower() in {'torch', 'torchvision'}:
            filename = f'{name}-{version}-cp310-cp310-win_amd64.whl'
            url = 'https://download.pytorch.org/whl/cu128/' + filename.replace('+', '%2B')
            index = urllib.request.urlopen('https://download.pytorch.org/whl/cu128/'+name+'/', timeout=90).read().decode()
            import re, html
            match = next((html.unescape(x) for x in re.findall(r'href="([^"]+)"', index) if filename.replace('+','%2B') in x), None)
            if not match: raise RuntimeError(filename)
            url, sha = match.split('#sha256=')
            if url.startswith('/'): url = 'https://download.pytorch.org' + url
            req=urllib.request.Request(url, method='HEAD')
            with urllib.request.urlopen(req, timeout=90) as f: size=int(f.headers['Content-Length'])
        else:
            data=fetch(f'https://pypi.org/pypi/{name}/{version}/json')
            matches=[x for x in data['urls'] if x['filename'].endswith('.whl') and ('win_amd64' in x['filename'] or x['filename'].endswith('none-any.whl')) and ('cp310' in x['filename'] or 'abi3' in x['filename'] or 'none-any' in x['filename'] or 'py3-none' in x['filename'])]
            if not matches: raise RuntimeError((name,version,'wheel unavailable'))
            chosen=next((x for x in matches if 'cp310' in x['filename']), matches[0])
            filename,url,size,sha=chosen['filename'],chosen['url'],chosen['size'],chosen['digests']['sha256']
        files.append(dict(RelativePath=filename, SourceUrl=url, SizeBytes=size, Sha256=sha, Purpose='python-wheel'))
        print(name,version,flush=True)
    # Same standalone distribution as the tested venv, pinned release (not latest).
    release=fetch('https://api.github.com/repos/astral-sh/python-build-standalone/releases/tags/20251007')
    asset=next(a for a in release['assets'] if 'cpython-3.10.18+' in a['name'] and 'x86_64-pc-windows-msvc-install_only_stripped.tar.gz' in a['name'])
    sha=asset.get('digest','').removeprefix('sha256:')
    if len(sha)!=64:
        with urllib.request.urlopen(asset['browser_download_url'],timeout=90) as f: sha=hashlib.file_digest(f,'sha256').hexdigest()
    files.insert(0,dict(RelativePath=asset['name'],SourceUrl=asset['browser_download_url'],SizeBytes=asset['size'],Sha256=sha,Purpose='python-runtime'))
    (target/'runtime.json').write_text(json.dumps(files,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')

if __name__=='__main__': main()
