"""Fetch pinned author release assets into an explicitly selected local stand."""
import argparse
import concurrent.futures
import hashlib
import json
import pathlib
import stat
import urllib.request
import zipfile


def sha256(path):
    digest = hashlib.sha256()
    with path.open('rb') as stream:
        for block in iter(lambda: stream.read(1024 * 1024), b''):
            digest.update(block)
    return digest.hexdigest()


def prepare(root, item):
    repo = item['repo']
    wanted = {
        '2600th/dlss5-video-player': 'dlss5-video-player-v0.30.0-win64.zip',
        'DaniilSokolyuk/video2dlssnr': 'video2dlssnr_release.zip',
        'Yukikaze1945/Lunarc-DLSS5': 'Lunarc-1.4.0-x64-Setup.exe',
        'Merserk/dlss5-visual-enhancer': 'Visual.Enhancer.v21.0.zip',
        'perseval-BLR/NeuralScreen': 'neuralscreen-v2.6.4-full.zip',
        'HECer/ComfyUI-DLSS5': 'ComfyUI-DLSS5-v0.5.0.zip',
    }.get(repo)
    if not wanted:
        return {'repo': repo, 'status': 'no_selected_asset', 'error': item.get('error')}
    if not item.get('releases'):
        raise ValueError('No release metadata for selected asset: ' + repo)
    release = item['releases'][0]
    asset = next(a for a in release['assets'] if a['name'] == wanted)
    url = asset['browser_download_url']
    assert url.startswith('https://github.com/' + repo + '/releases/download/')
    expected = asset['digest'].removeprefix('sha256:')
    assert len(expected) == 64
    downloads = root / 'downloads'
    downloads.mkdir(parents=True, exist_ok=True)
    archive = downloads / wanted
    if not archive.exists():
        part = archive.with_suffix(archive.suffix + '.part')
        print('DOWNLOAD', repo, asset['size'], flush=True)
        with urllib.request.urlopen(url, timeout=60) as response, part.open('wb') as stream:
            total = 0
            while block := response.read(1024 * 1024):
                total += len(block)
                if total > asset['size']:
                    raise ValueError('Asset exceeds declared size')
                stream.write(block)
        assert total == asset['size'], (repo, total, asset['size'])
        assert sha256(part) == expected, repo
        part.replace(archive)
    assert archive.stat().st_size == asset['size'] and sha256(archive) == expected
    result = {'repo': repo, 'tag': release['tag_name'], 'asset': wanted,
              'url': url, 'size': asset['size'], 'sha256': expected, 'status': 'verified'}
    if archive.suffix.lower() == '.zip':
        destination = root / 'tools' / repo.split('/')[1]
        destination.mkdir(parents=True, exist_ok=True)
        with zipfile.ZipFile(archive) as bundle:
            assert sum(i.file_size for i in bundle.infolist()) < 16 * 1024**3
            for info in bundle.infolist():
                target = (destination / info.filename).resolve()
                if not target.is_relative_to(destination.resolve()):
                    raise ValueError('Unsafe archive path')
                if stat.S_ISLNK(info.external_attr >> 16):
                    raise ValueError('Archive symlink')
            bundle.extractall(destination)
        result['folder'] = str(destination)
    print('VERIFIED', repo, flush=True)
    return result


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('stand', type=pathlib.Path)
    args = parser.parse_args()
    root = args.stand.resolve()
    inventory = json.loads((root / 'inventory.json').read_text(encoding='utf-8'))
    results = []
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = {pool.submit(prepare, root, item): item['repo'] for item in inventory}
        for future in concurrent.futures.as_completed(futures):
            try:
                results.append(future.result())
            except Exception as error:
                results.append({'repo': futures[future], 'status': 'failed', 'error': str(error)})
                print('FAILED', futures[future], str(error), flush=True)
            (root / 'assets.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    assert not any(r['status'] == 'failed' for r in results), 'See assets.json'


if __name__ == '__main__':
    main()
