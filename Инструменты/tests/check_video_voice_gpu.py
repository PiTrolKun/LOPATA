"""Independently check benchmark PCM/provenance and summarize CPU/GPU speed."""
import argparse
import array
import json
from pathlib import Path
import sys
import wave
sys.path.insert(0, str(Path(__file__).resolve().parent))
from check_video_voice_samples import provenance


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument('root', type=Path)
    parser.add_argument('--report', required=True, type=Path)
    args = parser.parse_args()
    latest = {}
    for path in sorted(args.root.rglob('result.json'), key=lambda p: p.stat().st_mtime):
        if not any(part in ('session-comparisons', 'final-comparisons') for part in path.parts):
            continue
        result = json.loads(path.read_text(encoding='utf-8-sig'))
        if len(result.get('rows', [])) != 2:
            continue
        latest[result['id']] = (path, result)
    assert len(latest) == 10, sorted(latest)
    rows = []
    for model, (path, result) in sorted(latest.items()):
        for trial in result['rows']:
            device = trial['device']
            assert trial['isGpu'] == (device == 'gpu')
            audio = path.parent / (device + '.wav')
            facts = provenance(audio.read_bytes())
            assert facts['Program'] == 'LOPATA' and facts['ProgramVersion'] == '0.6.32-dev'
            assert facts['Scenario'] == 'video-editor/narration'
            assert facts['Models'] == trial['Models']
            assert all(item['Revision'] for item in facts['Models'])
            with wave.open(str(audio)) as sound:
                assert sound.getsampwidth() == 2 and sound.getnframes() > 0
                samples = array.array('h', sound.readframes(sound.getnframes()))
                peak = max(abs(v) for v in samples) / 32768
                rms = (sum((v / 32768) ** 2 for v in samples) / len(samples)) ** 0.5
                assert peak > .001 and rms > .001, (model, device, peak, rms)
            if model == 'kitten2':
                assert any(m['Id'] == 'model-chatterbox-turbo-s3gen' for m in facts['Models']) == (device == 'gpu')
        cpu, gpu = result['rows']
        c, g = cpu['execution'], gpu['execution']
        assert not c['isGpu'] and g['isGpu']
        rows.append(dict(id=model, evidence=str(path.relative_to(args.root)),
                         cpuLoad=c['loadSeconds'], gpuLoad=g['loadSeconds'],
                         cpuFirst=c['runs'][0]['seconds'], gpuFirst=g['runs'][0]['seconds'],
                         cpuWarm=c['runs'][-1]['seconds'], gpuWarm=g['runs'][-1]['seconds'],
                         cpuRtf=c['runs'][-1]['rtf'], gpuRtf=g['runs'][-1]['rtf'],
                         normalizedSpeedup=c['runs'][-1]['rtf'] / g['runs'][-1]['rtf'],
                         actual=g['actual'], providers=g['providers'], nodes=g['executedNodes']))
    args.report.write_text(json.dumps(dict(models=rows, pcmFiles=20, metadataVerified=True), ensure_ascii=False, indent=2), encoding='utf-8')
    print('PASS: 10 real CPU/GPU pairs, 20 non-silent PCM16 WAVs with embedded actual model provenance.')


if __name__ == '__main__': main()
