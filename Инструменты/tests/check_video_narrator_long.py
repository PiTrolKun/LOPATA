"""Independent streaming RIFF/ID3 and packet-ledger verification, including hour-long audio."""
import argparse
import hashlib
import json
from pathlib import Path
import struct
import wave
import numpy as np


def synchsafe(data):
    return sum(value << (7 * (3 - index)) for index, value in enumerate(data))


def provenance(path):
    with path.open('rb') as source:
        head = source.read(12)
        assert head[:4] == b'RIFF' and head[8:12] == b'WAVE'
        assert struct.unpack_from('<I', head, 4)[0] + 8 == path.stat().st_size
        while header := source.read(8):
            assert len(header) == 8
            kind, size = header[:4], struct.unpack_from('<I', header, 4)[0]
            if kind != b'id3 ':
                source.seek(size + (size & 1), 1)
                continue
            assert size < 2 * 1024 * 1024
            body = source.read(size)
            assert body[:4] == b'ID3\x04'
            offset = 10
            while offset + 10 <= len(body):
                key, length = body[offset:offset + 4], synchsafe(body[offset + 4:offset + 8])
                value = body[offset + 10:offset + 10 + length]; offset += 10 + length
                if key == b'TXXX' and value[0] == 3:
                    field, content = value[1:].decode('utf-8').split('\0', 1)
                    if field == 'LOPATA_PROVENANCE':
                        return json.loads(content.rstrip('\0'))
    raise AssertionError('Embedded provenance is missing')


def check(path, ledger, expected_text, expected_models):
    facts = provenance(path)
    assert facts['Program'] == 'LOPATA' and facts['ProgramVersion'] == '0.6.34-dev'
    assert facts['Scenario'] == 'video-editor/narration' and facts['Models'] == expected_models
    with wave.open(str(path)) as sound:
        assert sound.getsampwidth() == 2 and sound.getcomptype() == 'NONE'
        frames, read_frames, peak, energy, count = sound.getnframes(), 0, 0, 0.0, 0
        while block := sound.readframes(65536):
            samples = np.frombuffer(block, dtype='<i2').astype(np.int32)
            peak = max(peak, int(np.abs(samples).max()))
            energy += float(np.square(samples.astype(np.float64)).sum())
            count += len(samples); read_frames += len(samples) // sound.getnchannels()
        assert frames == read_frames and peak > 32 and (energy / count) ** .5 > 32
        seconds = frames / sound.getframerate()
    packets = [json.loads(line) for line in ledger.read_text(encoding='utf-8').splitlines()]
    cursor = total_frames = 0
    for index, packet in enumerate(packets):
        assert packet['index'] == index and packet['start'] == cursor
        cursor = packet['end']; text = expected_text[packet['start']:cursor]
        assert packet['textSha256'] == hashlib.sha256(text.encode('utf-8')).hexdigest()
        total_frames += packet['frames']
    assert cursor == len(expected_text) and frames == total_frames
    return dict(seconds=seconds, frames=frames, packets=len(packets), inputChars=cursor,
                reused=sum(bool(p.get('reused')) for p in packets), peak=peak / 32768,
                textSha256=hashlib.sha256(expected_text.encode('utf-8')).hexdigest(), models=facts['Models'])


def main():
    parser = argparse.ArgumentParser(); parser.add_argument('root', type=Path); args = parser.parse_args()
    results = {}
    model_files = list((args.root / 'models').glob('*.json'))
    assert len(model_files) == 11
    for path in model_files:
        entry = json.loads(path.read_text(encoding='utf-8-sig')); variant = entry['variant']
        result = check(path.with_suffix('.wav'), path.with_name(variant + '-chunks.jsonl'), entry['text'], entry['Models'])
        assert result['packets'] == 2
        result['isGpu'] = entry['execution']['isGpu']
        assert result['isGpu'] == (variant != 'kitten2-cpu')
        results[variant] = result
    original = json.loads((args.root / 'original-request.json').read_text(encoding='utf-8-sig'))['text']
    for mode in ('regression', 'hour', 'resume'):
        entry = json.loads((args.root / 'work' / (mode + '.json')).read_text(encoding='utf-8-sig'))
        text = '\n\n'.join([original] * entry['repeats'])
        result = check(args.root / 'work' / (mode + '.wav'), args.root / 'work' / (mode + '-chunks.jsonl'), text, entry['Models'])
        assert entry['execution']['actual']['chunking']['textSha256'] == result['textSha256']
        if mode == 'hour': assert result['seconds'] > 3600 and result['reused'] == 0
        if mode == 'resume': assert 0 < result['reused'] < result['packets']
        results[mode] = result
    (args.root / 'verification.json').write_text(json.dumps(results, ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS: all 10 cards plus Kitten CPU; complete PCM, exact packet coverage, embedded provenance; regression, real hour and partial resume.')


if __name__ == '__main__': main()
