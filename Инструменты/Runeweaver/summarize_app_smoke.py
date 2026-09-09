"""Archive local app-runtime evidence without altering application settings."""
import json
import os
from pathlib import Path
import re
import shutil

root = Path(__file__).resolve().parents[2]
out = root / 'Тесты/Runeweaver/app_smoke_20260909'
diag = Path(os.environ['LOCALAPPDATA']) / 'AI_HUB/Diagnostics'
log = max((diag / 'LiteraryShared').glob('*.log'), key=lambda p: p.stat().st_mtime)
text = log.read_text(encoding='utf-8-sig')
paths = re.findall(r'Detailed diagnostics: (.+)', text)
evidence = out / 'diagnostics'
evidence.mkdir(exist_ok=True)
shutil.copy2(log, evidence / 'shared-server.log')
summary = {'server_log': str(log), 'model_loads': text.count('srv  llama_server: model loaded'), 'pids': [], 'requests': [], 'peaks_gib': {}}
peaks = {}
for name in paths:
    path = Path(name.strip())
    shutil.copy2(path, evidence / path.name)
    events = [json.loads(line) for line in path.read_text(encoding='utf-8-sig').splitlines()]
    ready = next((e['data'] for e in events if e['kind'] == 'process_ready'), None)
    budget = next((e['data'] for e in events if e['kind'] == 'budget'), None)
    failure = next((e['data'] for e in events if e['kind'] == 'failure'), None)
    if ready:
        summary['pids'].append(ready['pid'])
    summary['requests'].append({'file': path.name, 'slot': ready['slot'] if ready else None,
        'budget': budget, 'failure': failure['exception'].splitlines()[0] if failure else None})
    for e in events:
        if e['kind'] != 'resources':
            continue
        d = e['data']
        for key in ('workingSetBytes', 'privateBytes'):
            peaks[key] = max(peaks.get(key, 0), d[key])
        for key in ('dedicatedBytes', 'sharedBytes'):
            data = (d.get('gpu') or {}).get(key) or {}
            if data.get('available'):
                peaks[key] = max(peaks.get(key, 0), sum(data.get('instances', {}).values()))
summary['pids'] = sorted(set(summary['pids']))
summary['peaks_gib'] = {k: round(v / 1024**3, 3) for k, v in peaks.items()}
(out / 'summary.json').write_text(json.dumps(summary, ensure_ascii=False, indent=2), encoding='utf-8')
assert len(summary['pids']) == 1 and summary['model_loads'] == 1, summary
print(json.dumps(summary, ensure_ascii=False, indent=2))
