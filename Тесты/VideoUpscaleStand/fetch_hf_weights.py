"""Fetch explicitly listed weights from a pinned Hub revision, checking size and LFS SHA."""
import argparse
import concurrent.futures
import hashlib
import json
import pathlib
import urllib.request


def fetch(root, repository, revision, item):
    relative = pathlib.PurePosixPath(item['rfilename'])
    target = root.joinpath(*relative.parts)
    if not target.resolve().is_relative_to(root.resolve()):
        raise ValueError('Weight path escapes selected root')
    target.parent.mkdir(parents=True, exist_ok=True)
    expected_size, expected_hash = item['size'], item['lfs']['sha256']
    if target.exists():
        digest = hashlib.sha256()
        with target.open('rb') as stream:
            for block in iter(lambda: stream.read(8 * 1024**2), b''):
                digest.update(block)
        if target.stat().st_size == expected_size and digest.hexdigest() == expected_hash:
            print('VERIFIED EXISTING', str(relative), flush=True)
            return
        raise RuntimeError('Existing weight differs; refusing overwrite: ' + str(relative))
    url = f'https://huggingface.co/{repository}/resolve/{revision}/{relative}'
    partial = target.with_suffix(target.suffix + '.part')
    digest, total = hashlib.sha256(), 0
    print('DOWNLOAD', str(relative), expected_size, flush=True)
    with urllib.request.urlopen(url, timeout=60) as response, partial.open('wb') as stream:
        for block in iter(lambda: response.read(8 * 1024**2), b''):
            total += len(block)
            if total > expected_size:
                raise RuntimeError('Download exceeds recorded weight size')
            digest.update(block)
            stream.write(block)
    if total != expected_size or digest.hexdigest() != expected_hash:
        raise RuntimeError('Weight integrity check failed: ' + str(relative))
    partial.replace(target)
    print('VERIFIED', str(relative), flush=True)


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('metadata', type=pathlib.Path)
    parser.add_argument('destination', type=pathlib.Path)
    parser.add_argument('files', nargs='+')
    args = parser.parse_args()
    metadata = json.loads(args.metadata.read_text(encoding='utf-8'))
    selected = [next(x for x in metadata['siblings'] if x['rfilename'] == name)
                for name in args.files]
    with concurrent.futures.ThreadPoolExecutor(max_workers=3) as pool:
        futures = [pool.submit(fetch, args.destination, metadata['id'], metadata['sha'], item)
                   for item in selected]
        for future in futures:
            future.result()
    args.destination.joinpath('weight_receipt.json').write_text(json.dumps(
        dict(repository=metadata['id'], revision=metadata['sha'], files=selected), indent=2),
        encoding='utf-8')


if __name__ == '__main__':
    main()
