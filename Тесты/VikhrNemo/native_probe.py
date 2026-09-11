"""Document-format and role controls; exact requests and responses are retained."""
import hashlib
import json
from pathlib import Path
import time
import urllib.request

GROUND = "Your task is to answer the user's questions using only the information from the provided documents. Give two answers to each question: one with a list of relevant document identifiers and the second with the answer to the question itself, using documents with these identifiers."


def post(endpoint, route, body):
    request = urllib.request.Request(endpoint + route, json.dumps(body, ensure_ascii=False).encode(),
                                     {'Content-Type': 'application/json'})
    with urllib.request.urlopen(request, timeout=70) as response:
        return json.load(response)


def save(path, value):
    path.write_text(json.dumps(value, ensure_ascii=False, indent=2), encoding='utf-8')


def infer(endpoint, folder, key, messages, seed, temperature=.3, max_tokens=2048):
    body = dict(messages=messages, seed=seed, temperature=temperature, top_k=40,
                top_p=.95, min_p=.05, repeat_penalty=1.05, max_tokens=max_tokens,
                cache_prompt=False, stream=False, id_slot=0)
    save(folder / (key + '-request.json'), body)
    template = post(endpoint, 'apply-template', dict(messages=messages, add_generation_prompt=True))
    tokens = post(endpoint, 'tokenize', dict(content=template['prompt'], add_special=False))
    if len(tokens['tokens']) + max_tokens + 256 > 16384:
        raise ValueError('Context budget exceeded')
    start = time.monotonic()
    result = post(endpoint, 'v1/chat/completions', body)
    result['wallSeconds'] = time.monotonic() - start
    result['inputTokensMeasured'] = len(tokens['tokens'])
    save(folder / (key + '-response.json'), result)
    return result['choices'][0]['message']['content']


def run(endpoint, out, fixture):
    out.mkdir()
    sections = [p.read_text(encoding='utf-8-sig') for p in sorted((fixture/'Materials').glob('*.txt'))]
    truth = json.loads((fixture.parent/'ground-truth.json').read_text(encoding='utf-8-sig'))
    digest = hashlib.sha256('\n\n'.join(sections).encode()).hexdigest()
    if digest.upper() != truth['sourceSha256']:
        raise ValueError('Fixture hash mismatch')
    docs = [dict(doc_id=i, title=f'Тихий шлюз Вейраны — раздел {i+1}', content=s)
            for i, s in enumerate(sections)]
    save(out/'ground-truth.json', truth)
    save(out/'documents.json', docs)
    tasks = {
        'ending': 'Чем заканчивается история Нэлвы Риан в оригинале «Тихий шлюз Вейраны»? Где она оказалась и чем стала заниматься? Ответь коротко.',
        'objects': 'Какие ровно три предмета и в каком порядке действительно открывают шлюз в оригинале «Тихий шлюз Вейраны»? Ответь коротко.',
        'missing': 'Какова точная дата рождения Нэлвы Риан? Если в документах её нет, прямо скажи об этом.',
        'relation': 'Кто был чьим наставником: Нэлва Риан или Орт Девель? Были ли они родственниками?',
        'code': 'Что обозначает ЖУ-5831: дату, номер шлюза или что-то другое?'
    }
    records = []
    for repeat in range(3):
        for name, question in tasks.items():
            for mode in ['plain', 'native']:
                key = f'{name}-{mode}-{repeat}'
                item = dict(key=key, repeat=repeat, task=question, mode=mode)
                try:
                    if mode == 'native':
                        messages = [dict(role='system', content=GROUND),
                                    dict(role='documents', content=json.dumps(docs, ensure_ascii=False)),
                                    dict(role='user', content=question)]
                        selected = infer(endpoint, out, key+'-select', messages, 701+repeat, 0)
                        item['selected'] = selected
                        messages.append(dict(role='assistant', content=selected))
                    else:
                        messages = [dict(role='system', content='Answer in Russian using only the supplied documents. If the documents do not contain the answer, say so. Do not invent missing facts.'),
                                    dict(role='user', content='Документы:\n'+json.dumps(docs,ensure_ascii=False)+'\n\nВопрос:\n'+question)]
                    item['answer'] = infer(endpoint,out,key+'-final',messages,701+repeat)
                except Exception as error:
                    item['error'] = repr(error)
                records.append(item)
                save(out/'summary.json', records)
                print(key, json.dumps(item,ensure_ascii=False), flush=True)
    # Role controls use the same supplied source, with explicit separation of author changes.
    role_tasks = {
        'advisor': ('You are a literary advisor. Distinguish original facts from accepted author changes and your suggestions. Respect accepted changes. Answer in Russian, briefly. Do not write the story instead of giving advice.',
            'В моей версии Нэлва остаётся в Вейране и становится учительницей. Это принятое изменение. Сравни с финалом оригинала и предложи один способ связать версии. Не отменяй моё решение.'),
        'writer': ('You are a fiction writer. Write only the requested Russian story passage, without commentary. Follow the author instructions. Original source facts are reference material; explicit author changes take precedence in the new story.',
            'Напиши продолжение из 120–180 слов. Принятое изменение: Нэлва остаётся в Вейране и становится учительницей. Она не уезжает и не возвращается из Кевара. Текущий набросок: Нэлва Риан остановилась у закрытого шлюза. Орт Девель держал пустую коробку. Они ещё не решили, следует ли открывать проход. Продолжи эту сцену: Нэлва объясняет ребёнку назначение трёх предметов, а Орт помогает. Не добавляй новых магических свойств предметам. Только художественный текст.')
    }
    for repeat in range(3):
        for name,(system,question) in role_tasks.items():
            key=f'{name}-role-{repeat}'
            item=dict(key=key, repeat=repeat, task=question,mode='role_plain_source')
            try:
                messages=[dict(role='system',content=system),dict(role='user',content='Первоисточник:\n'+json.dumps(docs,ensure_ascii=False)+'\n\nЗадание автора:\n'+question)]
                item['answer']=infer(endpoint,out,key,messages,701+repeat,.5)
            except Exception as error:item['error']=repr(error)
            records.append(item);save(out/'summary.json',records)
            print(key,json.dumps(item,ensure_ascii=False),flush=True)
    return records
