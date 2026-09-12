"""One-factor controls for chat and explicit draft/confirmed-memory distinction."""
import json
from pathlib import Path
import sys
import time
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE))
from run import call,roles
from context_store import save
from runtime import server

model=sys.argv[1]
base=HERE/'runs'/model
out=HERE/'followups'/model
out.mkdir(parents=True,exist_ok=False)
systems=roles()
deadline=time.monotonic()+300
rule='''
Подтверждённая память фиксирует состояние ДО событий, добавленных в рабочий черновик. При вопросах о текущем положении сравни оба слоя: отдельно назови подтверждённое состояние и более позднее предварительное событие редактора. Новое событие черновика ещё не утверждено, но его нельзя игнорировать. Не подменяй цитатой книги ни один из этих слоёв. Если черновик не задаёт новую деталь, используй подтверждённую память.'''
save(out/'rule.json',dict(addition=rule,purpose='exploratory control after baseline; same for both models'))
with server(model,out) as endpoint:
    for repeat in range(3):
        folder=out/f'series-{repeat}';folder.mkdir()
        original=base/f'series-{repeat}'
        fixture=json.loads((original/'fixture.json').read_text(encoding='utf-8'))
        packet=json.loads((original/'write3-packet.json').read_text(encoding='utf-8'))
        call(endpoint,folder,'write-clean-chat','writer',[],packet,fixture['tasks']['write3'],801+repeat,deadline,systems['writer'])
        packet=json.loads((original/'review-packet.json').read_text(encoding='utf-8'))
        request=json.loads((original/'review-request.json').read_text(encoding='utf-8'))
        call(endpoint,folder,'review-layer-rule','advisor',request['messages'][1:-1],packet,fixture['tasks']['review'],801+repeat,deadline,systems['advisor']+rule)
save(out/'complete.json',dict(requests=6,remainingSeconds=deadline-time.monotonic()))
