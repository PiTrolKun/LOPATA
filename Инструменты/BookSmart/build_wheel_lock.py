"""Freeze this prepared CPython 3.12 Windows stand into verified PyPI wheels.
No runtime dependency resolver is needed by LOPATA.
"""
import concurrent.futures
import importlib.metadata
import json
from pathlib import Path
import time
import urllib.request
from packaging.tags import sys_tags
from packaging.utils import parse_wheel_filename

TAGS = {t: i for i, t in enumerate(sys_tags())}


def resolve(dist):
    name, version = dist.metadata['Name'], dist.version
    last = None
    for attempt in range(4):
        try:
            with urllib.request.urlopen('https://pypi.org/pypi/' + name + '/' + version + '/json', timeout=45) as response:
                metadata = json.load(response)
            candidates = []
            for item in metadata['urls']:
                if item['packagetype'] != 'bdist_wheel' or item.get('yanked'):
                    continue
                _, _, _, tags = parse_wheel_filename(item['filename'])
                ranks = [TAGS[t] for t in tags if t in TAGS]
                if ranks:
                    candidates.append((min(ranks), item))
            if not candidates:
                raise ValueError('No compatible published wheel: ' + name)
            item = min(candidates, key=lambda entry: entry[0])[1]
            return dict(name=name, version=version, file=item['filename'], size=item['size'],
                        sha256=item['digests']['sha256'], url=item['url'],
                        license=metadata['info'].get('license_expression') or metadata['info'].get('license'),
                        licenseClassifiers=[x for x in metadata['info']['classifiers'] if x.startswith('License ::')])
        except Exception as error:
            last = error
            time.sleep(attempt + 1)
    raise last


if __name__ == '__main__':
    distributions = [d for d in importlib.metadata.distributions() if d.metadata['Name'].lower() != 'pip']
    with concurrent.futures.ThreadPoolExecutor(max_workers=6) as executor:
        result = list(executor.map(resolve, distributions))
    result.sort(key=lambda entry: entry['name'].lower())
    path = Path(__file__).resolve().parents[2] / 'Тесты' / 'BookSmartStand' / 'windows-cp312-wheel-lock.json'
    path.write_text(json.dumps(result, ensure_ascii=False, indent=2), encoding='utf-8')
    print(len(result), 'wheels,', sum(x['size'] for x in result), 'bytes,', path)
