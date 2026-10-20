"""Create an unchanged, manifest-verified office bundle for signed file delivery."""
from pathlib import Path
import hashlib
import json
import zipfile

root = Path(__file__).resolve().parents[2]
manifest = json.loads((root / 'Исходники/AIHub/Tools/book-office-files.json').read_text(encoding='utf-8-sig'))
source = root / 'Runtime/BookConverter' / ('libreoffice-' + manifest['Version'])
output = source.parent / (source.name + '.bundle.zip')
entries = manifest['Files']


def verify(archive):
    with zipfile.ZipFile(archive) as bundle:
        if sorted(bundle.namelist()) != sorted(row['Name'] for row in entries):
            raise ValueError('Office bundle inventory differs from pinned manifest')
        for row in entries:
            with bundle.open(row['Name']) as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            if bundle.getinfo(row['Name']).file_size != row['Bytes'] or digest != row['Sha256'].lower():
                raise ValueError('Office bundle checksum mismatch: ' + row['Name'])


if output.exists():
    verify(output)
else:
    pending = output.with_suffix(output.suffix + '.preparing')
    if pending.exists():
        raise RuntimeError('Inspect incomplete office packaging before retrying: ' + str(pending))
    with zipfile.ZipFile(pending, 'x', compression=zipfile.ZIP_DEFLATED, compresslevel=6) as bundle:
        for row in entries:
            name = row['Name']
            path = source / name
            if not path.exists() and name.startswith('program/'):
                path = source / 'System64' / Path(name).name
            if path.is_symlink() or not path.resolve().is_relative_to(source.resolve()):
                raise ValueError('Unsafe office source path')
            with path.open('rb') as stream:
                digest = hashlib.file_digest(stream, 'sha256').hexdigest()
            if path.stat().st_size != row['Bytes'] or digest != row['Sha256'].lower():
                raise ValueError('Prepared office file changed: ' + name)
            info = zipfile.ZipInfo(name, date_time=(2000, 1, 1, 0, 0, 0))
            info.compress_type = zipfile.ZIP_DEFLATED
            with bundle.open(info, 'w') as target, path.open('rb') as stream:
                while data := stream.read(1024 * 1024):
                    target.write(data)
    verify(pending)
    pending.replace(output)
print(json.dumps({'files': len(entries), 'archiveBytes': output.stat().st_size, 'path': str(output)}))
