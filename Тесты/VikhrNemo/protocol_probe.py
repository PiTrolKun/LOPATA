"""Replay unchanged second-step requests with assistant prefill disabled."""
import json
import time
from native_probe import GROUND, infer, post, save


def run(endpoint,out,prior):
    out.mkdir()
    records=[]
    for path in sorted(prior.glob('*-native-*-final-request.json')):
        key=path.name.removesuffix('-request.json')
        body=json.loads(path.read_text(encoding='utf-8'))
        save(out/path.name,body)
        start=time.monotonic();item=dict(key=key)
        try:
            response=post(endpoint,'v1/chat/completions',body)
            response['wallSeconds']=time.monotonic()-start
            save(out/(key+'-response.json'),response)
            item['answer']=response['choices'][0]['message']['content']
            item['tokens']=response['usage']['completion_tokens']
        except Exception as error:item['error']=repr(error)
        records.append(item);save(out/'summary.json',records)
        print(key,json.dumps(item,ensure_ascii=False),flush=True)
    docs=json.loads((prior/'documents.json').read_text(encoding='utf-8'))
    question='Сколько окон было в обсерватории Мелар? Если переданные документы не содержат ответа, сообщи об этом.'
    for repeat in range(3):
        key=f'unrelated-native-{repeat}'
        item=dict(key=key,task=question)
        try:
            messages=[dict(role='system',content=GROUND),dict(role='documents',content=json.dumps(docs,ensure_ascii=False)),dict(role='user',content=question)]
            selected=infer(endpoint,out,key+'-select',messages,701+repeat,0)
            item['selected']=selected;messages.append(dict(role='assistant',content=selected))
            item['answer']=infer(endpoint,out,key+'-final',messages,701+repeat)
        except Exception as error:item['error']=repr(error)
        records.append(item);save(out/'summary.json',records)
        print(key,json.dumps(item,ensure_ascii=False),flush=True)
