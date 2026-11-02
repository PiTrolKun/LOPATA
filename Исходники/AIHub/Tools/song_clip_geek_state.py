"""Owned, input-bound commits for the Geekatplay card/FL2VA pipeline."""
import hashlib
import json
from pathlib import Path
from song_clip_motion_state import atomic_json, canonical, contained, file_hash, reject_links


def object_hash(value):
    return hashlib.sha256(canonical(value).encode('utf-8')).hexdigest()


class GeekState:
    def __init__(self, job, identity):
        self.job = Path(job)
        self.file = contained(job, 'geek-state.json')
        self.identity = identity
        self.value = {'schema': 1, 'identity': identity, 'cards': [], 'clips': []}
        if self.file.exists():
            value = json.loads(self.file.read_text(encoding='utf-8'))
            if (value.get('schema') != 1 or value.get('identity') != identity
                    or not isinstance(value.get('cards'), list) or not isinstance(value.get('clips'), list)):
                raise ValueError('Geekatplay checkpoint inputs changed; start a new attempt.')
            self.value = value

    def accepted(self, category, index, expected):
        entries = self.value[category]
        if index >= len(entries):
            return None
        item = entries[index]
        if (not isinstance(item, dict) or item.get('index') != index or item.get('input') != expected
                or not isinstance(item.get('files'), dict) or not item['files']):
            raise ValueError('Geekatplay artifact binding mismatch.')
        for relative, sha in item['files'].items():
            path = contained(self.job, relative)
            reject_links(path)
            if not path.is_file() or file_hash(path) != sha:
                raise ValueError('A committed Geekatplay artifact is missing or changed.')
        return item

    def commit(self, category, index, expected, files, **facts):
        if len(self.value[category]) != index:
            raise ValueError('Geekatplay artifacts must be committed in order.')
        hashes = {}
        for relative in files:
            path = contained(self.job, relative)
            reject_links(path)
            if not path.is_file() or path.stat().st_size == 0:
                raise ValueError('An empty artifact cannot be committed.')
            hashes[relative] = file_hash(path)
        item = dict(index=index, input=expected, files=hashes, **facts)
        self.value[category].append(item)
        atomic_json(self.file, self.value)
        return item

    def validate_counts(self, cards, clips):
        if len(self.value['cards']) > cards or len(self.value['clips']) > clips:
            raise ValueError('Geekatplay checkpoint has artifacts from a different plan.')
