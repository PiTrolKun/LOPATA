import collections
import json
import re
from probe_server import save
from run_probes import OUT

summary = {'memory': {}, 'requests': [], 'checks': {}}
for folder in OUT.iterdir():
    if not folder.is_dir():
        continue
    memory_file = folder / 'resources.json'
    if memory_file.exists():
        samples = json.loads(memory_file.read_text(encoding='utf-8'))
        phases = {}
        for phase in sorted({x['phase'] for x in samples}):
            group = [x for x in samples if x['phase'] == phase]
            phases[phase] = {key: max((x[key] for x in group if x.get(key) is not None), default=None)
                for key in ['working_set', 'private_bytes', 'dedicated', 'shared']}
        summary['memory'][folder.name] = phases
    for file in folder.glob('*-result.json'):
        result = json.loads(file.read_text(encoding='utf-8'))
        text = result.pop('content', '')
        result['words'] = len(re.findall(r'\b[\w]+(?:[-–][\w]+)*\b', text))
        result['chars'] = len(text)
        result['path'] = str(file.relative_to(OUT))
        result['repeated_lines'] = collections.Counter(x.strip() for x in text.splitlines()
            if len(x.strip()) > 15).most_common(3)
        summary['requests'].append(result)


def text(folder, name):
    return json.loads((OUT / folder / (name + '-result.json')).read_text(encoding='utf-8'))['content']


for cycle in range(3):
    for slot, marker, colour in [(0, 'Кедр-731', 'медный'), (1, 'Ирис-284', 'серебряный')]:
        answer = text('dual_8192_each', f'recall_{cycle}_{slot}')
        summary['checks'][f'isolated_recall_{cycle}_{slot}'] = marker in answer and colour in answer
for slot, marker, colour in [(0, 'Кедр-731', 'медный'), (1, 'Ирис-284', 'серебряный')]:
    for folder, name in [('dual_restart', f'restored_{slot}'),
                         ('dual_near_capacity', f'near_switch_{slot}')]:
        answer = text(folder, name)
        summary['checks'][name] = marker in answer and colour in answer
summary['checks']['no_history_does_not_disclose'] = 'НЕ ЗНАЮ' in text('dual_8192_each', 'no_history_privacy').upper()
for folder, names in [('dual_8192_each', ['cancel_writer_idle', 'after_cancel_idle']),
                      ('dual_near_capacity', ['exhaust_during_answer_idle', 'healthy_after_limit_idle'])]:
    for name in names:
        states = json.loads((OUT / folder / (name + '-slots.json')).read_text(encoding='utf-8'))
        summary['checks'][name + '_all_idle'] = all(not x['is_processing'] for x in states)
save(OUT / 'summary.json', summary)
print(json.dumps({'checks': summary['checks'], 'requests': len(summary['requests'])}, ensure_ascii=False, indent=2))
for r in summary['requests']:
    print(r['path'], 'words=', r['words'], 'finish=', r.get('finish'), 'error=', r.get('error'))
