import json, re, hashlib
from pathlib import Path
from datetime import datetime

ROOT = Path('H:/AI_HUB')
OUT = Path(__file__).resolve().parent
IDS = ['e42def33d0554f1eb3b84cef31c796bd', '6719c66128aa4f56a31de8dbe0a41330', 'bea70c1c430540c2971ed058db44d094']
results = []
for sid in IDS:
    directory = ROOT / 'Тесты/1/AI_HUB/Scenarios/ImageAnalysis/Projects' / sid
    s = json.loads((directory / 'session.json').read_text(encoding='utf-8-sig'))
    responses, analysis = [], None
    for path in sorted((directory / 'OmniResponses').glob('*.json')):
        r = json.loads(path.read_text(encoding='utf-8-sig'))
        content, finish, done, usage = '', None, False, None
        for line in r['rawProtocol'].splitlines():
            if line == 'data: [DONE]': done = True
            if not line.startswith('data: {'): continue
            c = json.loads(line[6:])
            if c.get('usage'): usage = c['usage']
            if not c.get('choices'): continue
            choice = c['choices'][0]
            content += choice.get('delta', {}).get('content') or ''
            finish = choice.get('finish_reason') or finish
        answer = content.strip()
        item = dict(file=str(path), sha256=hashlib.sha256(path.read_bytes()).hexdigest(), stage=r['stage'], finish=finish, done=done, usage=usage)
        assert done and finish == 'stop', item
        if r['stage'] == 'analyze': analysis = answer
        if r['stage'] == 'compose':
            clean = re.sub(r'^```(?:json)?\s*|\s*```$', '', answer)
            parsed = json.loads(clean)
            item.update(exactAnalysis=r['conversation'][1]['content'] == analysis,
                        savedVersionExact='\r\n\r\n'.join([parsed['title']] + parsed['paragraphs']) == s['versions'][0]['text'],
                        paragraphs=len(parsed['paragraphs']), uniqueParagraphs=len(set(parsed['paragraphs'])), title=parsed['title'], text=parsed['paragraphs'])
            assert item['exactAnalysis'] and item['savedVersionExact'], item
        responses.append(item)
    events = s['events']
    start = next(e['createdAt'] for e in events if e['code'] == 'VisionStarted')
    end = next(e['createdAt'] for e in events if e['code'] == 'DescriptionReady')
    results.append(dict(id=sid, file=s['file']['displayName'], model=s['modelId'], revision=s['modelRevision'], pipeline=s['pipelineId'], status=s['status'], seconds=(datetime.fromisoformat(end)-datetime.fromisoformat(start)).total_seconds(), metrics=s['runtimeMetrics'], speech=s['speechResult'], events=[e['code'] for e in events], responses=responses))
resources, warnings = [], []
for path in sorted((ROOT/'Тесты/1/AI_HUB/Core/Sessions').glob('2026-09-06_22*.jsonl')):
    for line in path.read_text(encoding='utf-8-sig').splitlines():
        e = json.loads(line)
        if e.get('localTime', '') > '2026-09-06T22:18': continue
        msg = (e.get('payload') or {}).get('message', '')
        if any(k in msg for k in ['Runtime resources:', 'Runtime launch:', 'loaded multimodal model', 'cache is enabled', 'total time', 'Kokoro warmup:']):
            resources.append(dict(file=str(path), at=e['localTime'], session=e.get('payload',{}).get('sessionId'), message=msg))
        if re.search(r'\bW |\bE |failed|out of memory|retry|attempt', msg, re.I):
            warnings.append(dict(at=e['localTime'], message=msg))
for name, obj in [('audit.json', results), ('resources.json', resources), ('warnings.json', warnings)]:
    (OUT/name).write_text(json.dumps(obj,ensure_ascii=False,indent=2),encoding='utf-8')
for r in results:
    print(r['id'], r['seconds'], 'seconds', len(r['responses']), 'responses', r['responses'][-1]['paragraphs'], 'paragraphs', r['responses'][-1]['uniqueParagraphs'], 'unique')
for r in resources:
    if 'Runtime resources:' in r['message']: print(r['at'], r['message'])
print('WARNINGS:', json.dumps(warnings,ensure_ascii=False))
