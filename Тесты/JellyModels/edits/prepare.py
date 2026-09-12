"""Simulate confirmations and rejection by fixture, never by an actual user."""
import json
from pathlib import Path
import sys
sys.path.insert(0,str(Path(__file__).resolve().parent))
sys.path.insert(0,str(Path(__file__).resolve().parents[1]))
from revision_store import Revisions
from book_source import save

HERE=Path(__file__).resolve().parent
path=HERE/'правки.lopata'
if path.exists(): raise ValueError('Preserve previous experiment; database already exists')
source='Лизавета передала ключ Германну.'
baseline=dict(giver='Лизавета',item='ключ',recipient='Германну',state='completed')
store=Revisions(path)
packets=[]
checks={}
store.seed('field',source,baseline)
before=store.read('field')
candidate=dict(baseline,recipient='Томскому')
token=store.propose('field',candidate,'field_edit')
checks['pending_does_not_change_active']=store.read('field')==before
store.confirm(token)
checks['only_recipient_changed']=store.read('field')['data']==candidate
checks['source_unchanged']=store.packet('field')['original_text']==source
store.db.close()
store=Revisions(path)
checks['survived_reopen']=store.read('field')['data']==candidate and store.read('field')['version']==2
stale=store.propose('field',dict(candidate,recipient='Анне'),'field_edit')
newer=store.propose('field',dict(candidate,recipient='Сурину'),'field_edit')
store.confirm(newer)
try: store.confirm(stale); checks['stale_blocked']=False
except ValueError: checks['stale_blocked']=True
checks['newer_preserved']=store.read('field')['data']['recipient']=='Сурину'
packets.append(dict(id='field',packet=store.packet('field'),expected=dict(current_recipient='Сурину',current_state='completed',source_recipient='Германну')))
store.seed('pending',source,baseline)
store.propose('pending',candidate,'free_text_parse','Лизавета передала ключ Томскому.')
checks['unconfirmed_parse_not_applied']=store.read('pending')['data']==baseline
packets.append(dict(id='pending',packet=store.packet('pending'),expected=dict(current_recipient='Германну',current_state='completed',source_recipient='Германну')))

for name in ('recipient','direction','negation','intention'):
    result=json.loads((HERE/'runs/nuextract'/(name+'.json')).read_text(encoding='utf-8'))
    if not result['pass']:
        checks['free_'+name]='parse_not_approved'
        continue
    fact={k:v for k,v in result['output'].items() if k in baseline}
    key='free_'+name
    store.seed(key,source,baseline)
    token=store.propose(key,fact,'free_text_parse',result['text'])
    checks[key+'_waited_for_confirmation']=store.read(key)['data']==baseline
    store.confirm(token)
    checks[key+'_saved_exactly']=store.read(key)['data']==fact
    packet=store.packet(key)
    packets.append(dict(id=key,packet=packet,expected=dict(current_recipient=fact['recipient'],current_state=fact['state'],source_recipient='Германну')))
checks['integrity']=store.db.execute('PRAGMA integrity_check').fetchone()[0]
checks['foreign_keys']=store.db.execute('PRAGMA foreign_key_check').fetchall()
checks['historical_source_versions_preserved']=store.db.execute('SELECT count(DISTINCT source_id) FROM history').fetchone()[0]==1
store.db.close()
save(HERE/'mechanics.json',checks)
save(HERE/'packets.json',packets)
print(json.dumps(checks,ensure_ascii=False,indent=2))
