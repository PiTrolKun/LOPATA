"""Mechanical audit only: these counts are not semantic answer-quality scores."""
import json
import sys
from pathlib import Path
from collections import defaultdict

root = Path(sys.argv[1])
rows = json.loads((root / 'summary.json').read_text(encoding='utf-8-sig'))
groups = defaultdict(list)
checked = 0
buffers = []
for row in rows:
    artifact = root / f"{row['test']}-{row['variant']}-{row['repeat']}.json"
    data = json.loads(artifact.read_text(encoding='utf-8-sig'))
    assert data['record']['result'] == row['result']
    assert data['record']['failure'] == row['failure']
    checked += 1
    groups[row['variant']].append(row)
    for trace in data['trace']:
        if trace['kind'] in ('quote_buffer', 'id_buffer'):
            buffers.append(dict(test=row['test'], variant=row['variant'], repeat=row['repeat'], accepted=trace['accepted']))
result = dict(recordsChecked=checked, requestSeconds=sum(x['elapsedMs'] for x in rows)/1000,
              modelSeconds=sum(x.get('modelMs',0) for x in rows)/1000,
              calls=sum(x.get('modelCalls',0) for x in rows), groups={}, buffers=buffers)
for variant, items in groups.items():
    refs = [x for x in items if x['test'] in ('canon_end','canon_cards','author_change','missing_fact')]
    local = [x for x in items if x['test'] in ('writer','draft_only')]
    result['groups'][variant] = dict(attempts=len(items), answers=sum(bool(x['result']) for x in items),
        errors=sum(bool(x['failure']) for x in items), requiresReference=len(refs),
        referencePrepared=sum(x['referenceDelivered'] for x in refs),
        unnecessaryReads=sum(x['reads']>0 for x in local))
(root/'audit.json').write_text(json.dumps(result,ensure_ascii=False,indent=2),encoding='utf-8')
print(json.dumps({k:v for k,v in result.items() if k!='buffers'},ensure_ascii=False,indent=2))
