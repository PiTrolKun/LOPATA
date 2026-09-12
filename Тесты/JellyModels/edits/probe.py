"""Compare free-form edits using native extraction APIs; no product changes."""
import argparse
import json
from pathlib import Path
import sys
import time
import urllib.request
HERE=Path(__file__).resolve().parent
sys.path.insert(0,str(HERE.parent))
sys.stdout.reconfigure(encoding='utf-8')
sys.dont_write_bytecode=True
from book_adapters import TorchAdapter,RuneAdapter
from book_source import save

FIELDS=dict(giver='Who transfers the item? Copy exact name, including case ending. Use the pronoun or null when ambiguous.',
    item='What item is transferred? Copy exact text.',recipient='Who receives the item? Copy exact name, including case ending.',
    state='completed = transfer occurred; not_happened = denied transfer; planned = intention not yet carried out; unknown = unclear',
    uncertain='yes when an actor cannot be unambiguously identified, otherwise no')
STATES=['completed','not_happened','planned','unknown']
SCHEMA={k:('verbatim-string' if k not in ('state','uncertain') else STATES if k=='state' else ['yes','no']) for k in FIELDS}
INSTRUCTION=('Parse the revised statement, not an older version. Extract one transfer record. '
             'Do not infer a completed action from a denial or an intention. Preserve ambiguity instead of guessing. '+json.dumps(FIELDS))

def request(server,messages,max_tokens=240):
    payload=dict(messages=messages,temperature=0,seed=42,max_tokens=max_tokens,cache_prompt=False,
                 response_format={'type':'json_object'},stream=False)
    req=urllib.request.Request(server.endpoint+'/v1/chat/completions',data=json.dumps(payload).encode(),headers={'Content-Type':'application/json'})
    with urllib.request.urlopen(req,timeout=45) as r: response=json.load(r)
    raw=response['choices'][0]['message']['content']
    result=dict(raw=raw,request=payload,response=response)
    try: result['output']=json.loads(raw)
    except ValueError as error: result['error']=str(error)
    return result

def extract(adapter,name,text):
    if name=='runeweaver':
        return request(adapter,[dict(role='system',content=INSTRUCTION+' Return only JSON with these fields and allowed values: '+json.dumps(SCHEMA)),dict(role='user',content=text)])
    with adapter.torch.inference_mode():
        if name=='gliner':
            schema=adapter.model.create_schema().structure('transfer')
            for key,description in FIELDS.items():
                schema.field(key,dtype='str',description=description,
                             choices=STATES if key=='state' else ['yes','no'] if key=='uncertain' else None)
            native=adapter.model.extract(text,schema)
            output=native.get('transfer')
            if isinstance(output,list) and len(output)==1: output=output[0]
            return dict(output=output,raw=native,schema=schema.build())
        inputs=adapter.tokenizer.apply_chat_template([dict(role='user',content=text)],template=json.dumps(SCHEMA),
            instructions=INSTRUCTION,enable_thinking=False,add_generation_prompt=True,tokenize=True,
            return_dict=True,return_tensors='pt').to('cuda')
        generated=adapter.model.generate(**inputs,max_new_tokens=256,max_time=35,do_sample=False)
        raw=adapter.tokenizer.decode(generated[0,inputs['input_ids'].shape[1]:],skip_special_tokens=True).strip()
        result=dict(raw=raw,rendered_prompt=adapter.tokenizer.decode(inputs['input_ids'][0]))
        try: result['output']=json.loads(raw)
        except ValueError as error: result['error']=str(error)
        return result

def run(name):
    destination=HERE/'runs'/name
    destination.mkdir(parents=True,exist_ok=False)
    adapter=None
    tick=time.perf_counter()
    try:
        print('Loading',name,flush=True)
        adapter=RuneAdapter(destination) if name=='runeweaver' else TorchAdapter(name)
        save(destination/'settings.json',dict(model=name,load_seconds=time.perf_counter()-tick,instructions=INSTRUCTION,schema=SCHEMA))
        for case in json.loads((HERE/'cases.json').read_text(encoding='utf-8')):
            tick=time.perf_counter()
            try: result=extract(adapter,name,case['text'])
            except Exception as error: result=dict(error=repr(error))
            result.update(id=case['id'],text=case['text'],seconds=time.perf_counter()-tick,expected=case['expected'])
            output=result.get('output')
            result['fields_match']={k:isinstance(output,dict) and (output.get(k) in expected if isinstance(expected,list) else output.get(k)==expected) for k,expected in case['expected'].items()}
            result['pass']=all(result['fields_match'].values())
            save(destination/(case['id']+'.json'),result)
            print(case['id'],result['pass'],json.dumps(output,ensure_ascii=False),flush=True)
        if name=='runeweaver':
            for item in json.loads((HERE/'packets.json').read_text(encoding='utf-8')):
                messages=[dict(role='system',content='Read the stored memory and original source separately. Return JSON with current_recipient, current_state, source_recipient. Copy strings verbatim from the active memory for current fields. Do not silently revert edits to match the original text.'),
                          dict(role='user',content=json.dumps(item['packet'],ensure_ascii=False))]
                result=request(adapter,messages)
                result.update(id=item['id'],expected=item['expected'])
                result['pass']=result.get('output')==item['expected']
                save(destination/('read-'+item['id']+'.json'),result)
                print('Read',item['id'],result['pass'],result.get('output'),flush=True)
    finally:
        if adapter: adapter.close()
        print('Finished',name,flush=True)

if __name__=='__main__':
    p=argparse.ArgumentParser();p.add_argument('model',choices=['gliner','nuextract','runeweaver']);run(p.parse_args().model)
