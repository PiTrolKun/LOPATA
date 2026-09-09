"""Replay the problematic revision independently of any Advisor slot."""
import json
import time
import urllib.request
from probe_server import Server, save
from run_probes import OUT

source = OUT / 'dual_roles_roundtrip/writer_revision-request.json'
body = json.loads(source.read_text(encoding='utf-8'))
s = Server(OUT / 'revision_repeat_uncached', context=8192, slots=1)
try:
    for seed in [431, 432]:
        request = dict(body, seed=seed, stream=False, cache_prompt=False)
        request.pop('stream_options', None)
        s.phase = f'repeat_{seed}'
        save(s.folder / f'repeat_{seed}-request.json', request)
        start = time.monotonic()
        result = s.api('/v1/chat/completions', request)
        save(s.folder / f'repeat_{seed}-response.json', result)
        text = result['choices'][0]['message']['content']
        save(s.folder / f'repeat_{seed}-result.json', {'content': text,
            'seconds': time.monotonic() - start, 'seed': seed,
            'usage': result.get('usage'), 'finish': result['choices'][0]['finish_reason']})
        s.idle(f'repeat_{seed}_idle')
        print(f'Seed={seed}, chars={len(text)}, finish={result["choices"][0]["finish_reason"]}', flush=True)
    url = 'https://huggingface.co/limloop/MN-12B-Runeweaver-RP-RU/raw/main/chat_template.jinja'
    with urllib.request.urlopen(url, timeout=20) as response:
        template = response.read().decode('utf-8')
    (s.folder / 'upstream-chat_template.jinja').write_text(template, encoding='utf-8')
    active = s.api('/props')['chat_template']
    save(s.folder / 'template-check.json', {'url': url, 'equal_after_strip': template.strip() == active.strip()})
finally:
    s.close()
